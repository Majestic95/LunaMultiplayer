using LmpClient.Diagnostics;
using LmpClient.Extensions;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselRemoveSys;
using LmpClient.Utilities;
using LmpClient.VesselUtilities;
using System;

namespace LmpClient.Systems.VesselProtoSys
{
    public class VesselProto
    {
        public Guid VesselId;
        public byte[] RawData = new byte[0];
        public int NumBytes;
        public double GameTime;
        public bool ForceReload;

        public Vessel LoadVessel()
        {
            return null;
        }

        public ProtoVessel CreateProtoVessel()
        {
            var configNode = RawData.DeserializeToConfigNode(NumBytes);
            if (configNode == null || configNode.VesselHasNaNPosition())
            {
                LunaLog.LogError($"Received a malformed vessel from SERVER. Id {VesselId}");
                VesselSyncDiagnostics.LogDiscarded(VesselId, vesselName: null, parts: -1,
                    reason: configNode == null
                        ? "DeserializeToConfigNode returned null (wire payload was unparseable)"
                        : "ConfigNode contained NaN position values (configNode.VesselHasNaNPosition)");
                VesselRemoveSystem.Singleton.KillVessel(VesselId, true, "Malformed vessel");
                return null;
            }

            // [Stage 5.18b] Record this vessel's owning agency from the wire
            // ConfigNode BEFORE handing off to KSP's ProtoVessel ctor — KSP silently
            // drops the unknown top-level lmpOwningAgency field, so this is the only
            // point on the receive path where the value is reachable. The registry
            // is the client-side mirror of the server's authoritative
            // Vessel.OwningAgencyId (Server/System/Vessel/Classes/Vessel.cs).
            //
            // Relay-safety: this call site sees both authoritative VesselSync replies
            // (which serialize from the server's canonical store via
            // GetVesselInConfigNodeFormat and DO carry lmpOwningAgency) AND relayed
            // protos (server forwards the ORIGINAL sender bytes per the warning at
            // Server/Message/VesselMsgReader.cs:188-198 — those have no
            // lmpOwningAgency because KSP's BackupVessel/Save strips the unknown
            // field on every local-owner resend). RecordOwnership applies the
            // preservation rule: incoming non-Empty wins; incoming Empty inserts
            // only when there's no prior entry, never downgrades a known real id.
            var owningAgency = AgencyMembership.TryParseAgencyId(configNode.GetValue("lmpOwningAgency"));
            AgencyMembership.RecordOwnership(AgencySystem.Singleton?.VesselOwnership, VesselId, owningAgency);

            // [Phase 6 follow-up: foreign-vessel crew strip — fixes v8.1 soak
            // "Agency A only has Jeb available; B/C are flying Bill/Val"]
            // Foreign-agency vessels carry `crew = NAME` references in their PART
            // nodes that resolve in this client's local CrewRoster — each agency
            // owns its own copy of stock-4 with identical names per spec §Q-Seed.
            // Passing them to KSP's ProtoVessel.Load (in VesselLoader.LoadVesselIntoGame)
            // binds the local same-named ProtoCrewMember to the foreign vessel's
            // seat via KSP's name-keyed CrewRoster lookup, flipping the local
            // kerbal to rosterStatus=Assigned on the wrong vessel. Net effect:
            // every agency permanently loses access to any kerbal name another
            // agency has assigned to a vessel — every name becomes "first agency
            // to fly it claims it for the cohort."
            //
            // Strip the names from the ConfigNode BEFORE the ProtoVessel ctor +
            // Load run so KSP's CrewRoster lookup never finds them. The post-Load
            // ScrubInvalidProtoCrew in VesselLoader is too late — by the time it
            // runs, ProtoVessel.Load has already bound the local kerbals + flipped
            // their rosterStatus, and the damage is durable across save/load.
            //
            // Spec §Q-Seed amendment + CLAUDE.md Stack Notes entry document the
            // broader failure-mode rationale (Stage 6 spec assumed Phase 6.4's
            // request filter prevented cross-roster collisions; that filter only
            // covers KerbalsRequest, not the VesselProto relay channel which
            // intentionally cross-fans for visibility).
            //
            // Gate: PerAgencyKerbalRosterEnabled (composite, NOT PerAgencyCareerEnabled
            // alone — the BUG-023 race window during the Stage 5→6 ramp would
            // otherwise mis-classify legitimate in-flight crew as foreign). Local-
            // agency confirmed vessels (OwningAgency == LocalAgency) pass through
            // unchanged. PESSIMISTIC strip fires on Empty/Unassigned/MISS ownership
            // under combined gate=on — closes the initial-connect race where a
            // peer's relay-stripped VesselProto arrives BEFORE the authoritative
            // VesselSync reply stamps the agency-id (server's relay forwards
            // original bytes which lack `lmpOwningAgency` after the first
            // BackupVessel resend per the [[5.18b relay-vs-store note]]).
            // Decision matrix pinned by AgencyMembership.ShouldStripForeignCrew
            // unit tests in LmpClientTest — see that helper's XML for the full
            // truth table.
            //
            // ForeignCrewCount is populated HERE (not at the post-Load scrub site)
            // so the Phase 6.6 "Crew: N (Agency)" label renders with the pre-strip
            // count — see updated VesselLoader.ScrubInvalidProtoCrew which now
            // skips its own Phase 6.6 registry mgmt when IsForeignVessel returns
            // true (pre-Load owns it on this path).
            if (AgencyMembership.ShouldStripForeignCrew(
                    SettingsSystem.ServerSettings.PerAgencyKerbalRosterEnabled,
                    VesselId,
                    AgencySystem.Singleton?.LocalAgencyId ?? Guid.Empty,
                    AgencySystem.Singleton?.VesselOwnership))
            {
                var stripped = StripForeignCrewNames(configNode);
                if (stripped > 0)
                {
                    // Log only when the foreign-crew count CHANGES — fires on first
                    // sight of a foreign-crewed vessel and again only when crew
                    // composition shifts (EVA, dock/undock, death). Prevents the
                    // periodic ~10s vessel-proto resync cadence from flooding the
                    // log with identical lines (consumer-lens review SHOULD FIX:
                    // per-tick instrumentation would emit thousands of lines/hour
                    // in a 10-agency cohort). Tag prefix aligns with the
                    // [fix:per-agency-kerbal-roster] audit-grep convention
                    // established across Phases 6.4/6.5/6.7/6.8.
                    var previousCount = 0;
                    var hasPrevious = AgencySystem.Singleton != null
                        && AgencySystem.Singleton.ForeignCrewCount.TryGetValue(VesselId, out previousCount);
                    if (!hasPrevious || previousCount != stripped)
                    {
                        LunaLog.Log(
                            $"[fix:per-agency-kerbal-roster] foreign-crew-scrub {VesselId:N} stripped={stripped}" +
                            (hasPrevious ? $" previous={previousCount}" : " first-sight=true"));
                    }
                    if (AgencySystem.Singleton != null)
                        AgencySystem.Singleton.ForeignCrewCount[VesselId] = stripped;
                }
                else if (AgencySystem.Singleton != null)
                {
                    // Foreign drone (no crew aboard at the wire level) — evict any
                    // stale registry entry from a prior crewed pass.
                    AgencySystem.Singleton.ForeignCrewCount.TryRemove(VesselId, out _);
                }
            }

            var newProto = VesselSerializer.CreateSafeProtoVesselFromConfigNode(configNode, VesselId);
            if (newProto == null)
            {
                LunaLog.LogError($"Received a malformed vessel from SERVER. Id {VesselId}");
                VesselSyncDiagnostics.LogDiscarded(VesselId, vesselName: null, parts: -1,
                    reason: "VesselSerializer.CreateSafeProtoVesselFromConfigNode returned null (ProtoVessel ctor refused the node)");
                VesselRemoveSystem.Singleton.KillVessel(VesselId, true, "Malformed vessel");
                return null;
            }

            return newProto;
        }

        /// <summary>
        /// [Phase 6 follow-up] Removes every <c>crew = NAME</c> value from the
        /// vessel's <c>PART</c> child nodes. Called from
        /// <see cref="CreateProtoVessel"/> when the vessel is owned by a
        /// foreign agency under
        /// <see cref="Settings.SettingsServerStructure.PerAgencyKerbalRosterEnabled"/>.
        /// KSP's <c>ProtoVessel</c> ctor + <c>Load</c> then see the parts as
        /// empty-seated and never resolve any crew names against this client's
        /// local <c>HighLogic.CurrentGame.CrewRoster</c>.
        ///
        /// <para>Returns the total count of <c>crew = …</c> values removed
        /// across all PART nodes — used as the
        /// <see cref="AgencySystem.ForeignCrewCount"/> value for the Phase 6.6
        /// "Crew: N (Agency)" label.</para>
        ///
        /// <para><b>What this does NOT strip.</b> Only the top-level
        /// <c>PART.crew</c> values that <c>ProtoVessel.Load</c> reads when
        /// populating <c>ProtoPartSnapshot.protoCrewNames</c>. Any other
        /// crew-bearing PartModule fields (mod-side kerbal-name fields on
        /// custom modules) are NOT touched — they don't feed the
        /// <c>protoCrewNames</c> → <c>protoModuleCrew</c> → <c>CrewRoster</c>
        /// binding path that's the pollution vector. If a future mod-compat
        /// finding shows another field feeds the binding (e.g. a mod's
        /// PartModule that calls <c>HighLogic.CurrentGame.CrewRoster[name]</c>
        /// directly during its own <c>OnLoad</c>), extend this helper rather
        /// than adding a parallel scrubber. No such consumer exists in the
        /// current cohort's mod set (MKS / WOLF / Final Frontier / Contract
        /// Configurator) per the 2026-05-20 audit.</para>
        /// </summary>
        private static int StripForeignCrewNames(ConfigNode vesselNode)
        {
            if (vesselNode == null) return 0;
            var total = 0;
            foreach (var partNode in vesselNode.GetNodes("PART"))
            {
                if (partNode == null) continue;
                var crewValues = partNode.GetValues("crew");
                if (crewValues == null || crewValues.Length == 0) continue;
                total += crewValues.Length;
                partNode.RemoveValues("crew");
            }
            return total;
        }
    }
}
