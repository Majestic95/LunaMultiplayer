using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Log;
using Server.System.Vessel;
using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 4 Slice E — server-side per-agency router for MKS WOLF crew
    /// routes. Sits behind the <c>AgencyMsgReader</c> dispatch for
    /// <see cref="LmpCommon.Message.Types.AgencyMessageType.WolfCrewRouteState"/>
    /// (slot 13). Owner-only echo + persistence; the projector
    /// (<see cref="AgencyScenarioProjector"/>) splices per-agency entries into
    /// outgoing <c>WOLF_ScenarioModule</c> blobs at <c>SendScenarioModules</c>
    /// time so each agency sees ONLY their own crew routes — spec §10 Q1
    /// PrivateAgencyResources=true.
    ///
    /// <para><b>Distinctive surface for Phase 4: cross-agency kerbal
    /// authority gate (pre-spec §8 / spec §10 Q3).</b> Unlike Slices B/C/D
    /// (which never touch vessel-proxy authority — depots/routes/hoppers/
    /// terminals are body+biome or Guid keyed, not vessel-keyed), CrewRoute
    /// passengers are kerbals — each kerbal's authority is derived from
    /// the <c>OwningAgencyId</c> of whichever vessel currently carries them
    /// (vessel-proxy authority, mirrors the K1 grief guard pattern from
    /// Stage 5.17e-8 <c>KerbalSystem.CanRemoveKerbalUnderK1</c>).
    ///
    /// <para><b>Reject semantics (Option A per pre-spec §8).</b> If ANY
    /// passenger on an inbound entry is currently aboard a vessel whose
    /// <c>OwningAgencyId</c> is non-Empty AND different from the requester's
    /// agency, the ENTIRE entry is dropped (not partial-accepted). The prior
    /// server-side snapshot stays canonical; the projector overwrites the
    /// requester's local UI back to the pre-Embark state on the next
    /// <c>SendScenarioModules</c> tick. Operator-visible Warning log per
    /// rejected entry (one log per griefing attempt; rate-limit pressure is
    /// bounded by WOLF UI cadence — Embark is a click, not a per-tick
    /// stream). Per pre-spec §8.f legitimate-client UX is the in-game
    /// prefix on <c>WOLF_CrewTransferScenario.Launch</c> (Slice F if it
    /// surfaces an operator demand); modified-client desync is structurally
    /// acceptable.</para>
    ///
    /// <para><b>Bypass cases for the kerbal gate</b> (all match K1 precedent
    /// at <c>KerbalSystem.cs:103-136</c>):
    /// <list type="bullet">
    ///   <item><b>Empty passenger Name</b> — defensive fall-through (a
    ///         malformed wire entry; per-entry validation below rejects
    ///         the entry separately).</item>
    ///   <item><b>Kerbal not aboard any vessel</b> — unassigned / AC-pool /
    ///         pre-launch kerbal; any agency may interact.</item>
    ///   <item><b>Vessel <c>OwningAgencyId</c> = Empty</b> — Unassigned-
    ///         sentinel vessel per spec §10 Q3 (pre-0.31 universes,
    ///         admin-demoted vessels); any agency may interact.</item>
    ///   <item><b>Requester has no agency mapping</b> — already filtered at
    ///         the top of <see cref="TryRoute"/>; we'd not reach the
    ///         kerbal-gate check at all.</item>
    /// </list></para>
    ///
    /// <para><b>Single-class wire contract (pre-spec §2.e).</b>
    /// <see cref="AgencyWolfCrewRouteStateMsgData"/> is used both directions
    /// on slot 13; inbound from the client postfix carries wire-supplied
    /// <c>AgencyId</c> that the server IGNORES. Sender authority is derived
    /// authoritatively from <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>
    /// — same trust posture as the Slice B-D siblings.</para>
    ///
    /// <para><b>Per-entry isolation (pre-spec §3.a).</b> Validation +
    /// cross-agency check + upsert wrapped in a SINGLE try/catch per entry.
    /// A malformed entry (empty UniqueId or Origin/Destination Body/Biome)
    /// or a cross-agency rejection never aborts siblings. Matches the
    /// <see cref="AgencyWolfHopperRouter"/> precedent.</para>
    ///
    /// <para><b>Key form preservation</b> (pre-spec §2.f.iv).
    /// <c>CrewRoute.UniqueId</c> is a Guid in <c>ToString("N")</c> form
    /// per <c>CrewRoute.cs:90</c>. Distinct from
    /// <c>WolfHoppers</c>' with-hyphens form (matches
    /// <see cref="AgencyState.WolfTerminals"/>' "N" form precedent). The
    /// router uses the raw wire string as the dictionary key — do NOT
    /// normalize at any boundary.</para>
    ///
    /// <para><b>FK-integrity decoupling (mirrors Slice C Routes).</b>
    /// CrewRoutes have BOTH origin AND destination FK to depots per
    /// <c>CrewRoute.cs:249-250</c> (<c>_registry.GetDepot</c> throws
    /// <c>DepotDoesNotExistException</c> when missing). The router does
    /// NOT enforce depot existence at upsert time: the wire batch may
    /// arrive before the parent depot's postfix-driven upsert (different
    /// Harmony patches, different messages, no ordering guarantee). FK
    /// integrity is enforced LATER in the projector splice — see
    /// <see cref="AgencyScenarioProjector"/>'s <c>SpliceAgencyWolfState</c>
    /// CrewRoutes block.</para>
    ///
    /// <para><b>RemovedKeys is admin/migration-only (matches Routes, NOT
    /// Hoppers/Terminals).</b> WOLF has no <c>RemoveCrewRoute</c> API
    /// (verified s41 source walk against <c>ScenarioPersister.cs:432-449</c>
    /// — only <c>RemoveHopper</c> + <c>RemoveTerminal</c> exist), so the
    /// <c>RemovedKeys</c> tail is reserved for Slice F admin paths
    /// (deleteagency cascade / transferagency MKS-aware companion).</para>
    ///
    /// <para><b>Dual-mode gate (spec §11).</b> With
    /// <see cref="AgencySystem.PerAgencyEnabled"/> false,
    /// <see cref="TryRoute"/> returns <c>false</c> immediately and the caller
    /// — <c>AgencyMsgReader</c> — drops the inbound silently.</para>
    ///
    /// <para><b>Per-agency lock contract (pre-spec §3.b).</b> The per-entry
    /// for-loop is wrapped in
    /// <c>lock (AgencySystem.GetAgencyLock(agencyId))</c>. Mirrors the
    /// Slice B-D siblings. The kerbal-authority pre-scan (against
    /// <see cref="VesselStoreSystem.CurrentVessels"/>) runs OUTSIDE the
    /// per-agency lock — different lock domains; the vessel store has its
    /// own concurrency model and the pre-scan only READS vessel serialized
    /// text, never mutates state.</para>
    /// </summary>
    public static class AgencyWolfCrewRouter
    {
        /// <summary>
        /// Attempts to route the inbound crew-route state batch through the
        /// per-agency path. Returns <c>true</c> if this method handled the
        /// inbound; returns <c>false</c> when the gate is off, the client
        /// lacks an agency mapping (defensive), or the agency registry
        /// entry is missing (defensive).
        /// </summary>
        public static bool TryRoute(ClientStructure client, AgencyWolfCrewRouteStateMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            // Pre-scan kerbal → owning-agency map ONCE per router invocation,
            // BEFORE entering the per-agency lock. The vessel-store
            // serialization is an expensive read that does NOT touch the
            // per-agency lock domain — keep it outside the lock window so
            // the per-agency lock isn't held during a multi-vessel
            // text serialization. (Multi-lens review MUST FIX — the v1
            // implementation lazy-built the map inside the lock, widening
            // the lock window unnecessarily AND contradicting the class
            // XML's documented lock-domain contract.)
            //
            // Only build the map when at least one entry has passengers.
            // Empty-passenger batches (CreateCrewRoute postfix on a
            // freshly-minted empty route, Disembark draining the last
            // passenger) skip the scan entirely — common cases.
            Dictionary<string, Guid> kerbalAgencyMap = null;
            for (var ei = 0; ei < msg.EntryCount; ei++)
            {
                var pre = msg.Entries[ei];
                if (pre != null && pre.Passengers != null && pre.Passengers.Count > 0)
                {
                    kerbalAgencyMap = BuildKerbalAgencyMap();
                    break;
                }
            }

            var accepted = new List<AgencyWolfCrewRouteEntry>(msg.EntryCount);
            var removedKeys = new List<string>();

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                for (var i = 0; i < msg.EntryCount; i++)
                {
                    var entry = msg.Entries[i];
                    if (entry == null)
                        continue;

                    try
                    {
                        if (string.IsNullOrEmpty(entry.UniqueId)
                            || string.IsNullOrEmpty(entry.OriginBody) || string.IsNullOrEmpty(entry.OriginBiome)
                            || string.IsNullOrEmpty(entry.DestinationBody) || string.IsNullOrEmpty(entry.DestinationBiome))
                        {
                            LunaLog.Debug($"[fix:WOLF-R4] crew-route entry skipped: empty UniqueId/Origin/Destination (agency {agencyId:N})");
                            continue;
                        }

                        if (entry.Passengers != null && entry.Passengers.Count > 0
                            && kerbalAgencyMap != null)
                        {
                            if (RejectIfCrossAgencyPassenger(entry, agencyId, kerbalAgencyMap, out var offendingKerbal, out var offendingAgency))
                            {
                                LunaLog.Warning(
                                    $"[fix:WOLF-R4] CrewRoute '{entry.UniqueId}' DROPPED for agency {agencyId:N} ({client.PlayerName}): " +
                                    $"passenger '{offendingKerbal}' is aboard a vessel owned by agency {offendingAgency:N}. " +
                                    "Cross-agency kerbal reject per pre-spec §8 / spec §10 Q3.");
                                continue;
                            }
                        }

                        Upsert(agency, entry);
                        accepted.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        LunaLog.Error($"[fix:WOLF-R4] crew-route entry skipped for '{entry.UniqueId}' at '{entry.OriginBody}/{entry.OriginBiome}→{entry.DestinationBody}/{entry.DestinationBiome}' (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Removal tail. CrewRoutes have no normal-op Remove API in
                // WOLF (no ScenarioPersister.RemoveCrewRoute method exists
                // per s41 source verification against MKS SHA ed0f6aa6 —
                // only RemoveHopper + RemoveTerminal). RemovedKeys is
                // reserved for Slice F admin / migration paths (deleteagency
                // cascade, transferagency MKS-aware companion). Same
                // posture as Slice C Routes. Validation: skip null/empty;
                // drop any key that's not in the dict (no-op).
                if (msg.RemovedKeyCount > 0 && msg.RemovedKeys != null)
                {
                    for (var i = 0; i < msg.RemovedKeyCount; i++)
                    {
                        var key = msg.RemovedKeys[i];
                        if (string.IsNullOrEmpty(key))
                            continue;
                        if (agency.WolfCrewRoutes.Remove(key))
                            removedKeys.Add(key);
                    }
                }
            }

            if (accepted.Count > 0 || removedKeys.Count > 0)
            {
                AgencySystem.SaveAgency(agencyId);
                AgencySystemSender.SendWolfCrewRouteStateToOwner(client, agencyId, accepted, removedKeys);
            }

            return true;
        }

        /// <summary>
        /// Inserts or replaces the crew-route entry keyed by
        /// <c>entry.UniqueId</c> (Guid string in <c>ToString("N")</c> form —
        /// preserve at the wire boundary). Caller MUST hold
        /// <c>AgencySystem.GetAgencyLock(agencyId)</c> per the
        /// <see cref="AgencyState.WolfCrewRoutes"/> concurrency contract.
        ///
        /// <para><b>Defensive copy of <see cref="AgencyWolfCrewRouteEntry.Passengers"/></b>
        /// (pre-spec §3.c). The nested list is a mutable
        /// <c>List&lt;AgencyWolfPassengerEntry&gt;</c> from the wire.
        /// Storing the reference directly would let a subsequent re-arrival
        /// mutate the stored entry in place — defensive shallow copy of the
        /// list + the wire values preserves the at-ingest snapshot.
        /// Passenger entries themselves are value-shape (no nested mutable
        /// state) so a shallow copy suffices.</para>
        ///
        /// <para><b>Internal visibility</b> matches
        /// <see cref="AgencyWolfHopperRouter.Upsert"/> — ServerTest reaches
        /// in to pin the upsert semantics without bringing the full
        /// <see cref="TryRoute"/> path up.</para>
        /// </summary>
        internal static void Upsert(AgencyState agency, AgencyWolfCrewRouteEntry entry)
        {
            if (agency == null)
                throw new ArgumentNullException(nameof(agency));
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.UniqueId))
                throw new ArgumentException("CrewRoute entry UniqueId must be non-empty", nameof(entry));

            var copy = new AgencyWolfCrewRouteEntry
            {
                ArrivalTime = entry.ArrivalTime,
                OriginBody = entry.OriginBody ?? string.Empty,
                OriginBiome = entry.OriginBiome ?? string.Empty,
                DestinationBody = entry.DestinationBody ?? string.Empty,
                DestinationBiome = entry.DestinationBiome ?? string.Empty,
                Duration = entry.Duration,
                EconomyBerths = entry.EconomyBerths,
                LuxuryBerths = entry.LuxuryBerths,
                FlightNumber = entry.FlightNumber ?? string.Empty,
                FlightStatus = entry.FlightStatus ?? string.Empty,
                UniqueId = entry.UniqueId,
                Passengers = new List<AgencyWolfPassengerEntry>(entry.Passengers?.Count ?? 0),
            };
            if (entry.Passengers != null)
            {
                foreach (var passenger in entry.Passengers)
                {
                    if (passenger == null)
                        continue;
                    copy.Passengers.Add(new AgencyWolfPassengerEntry
                    {
                        Name = passenger.Name ?? string.Empty,
                        DisplayName = passenger.DisplayName ?? string.Empty,
                        IsTourist = passenger.IsTourist,
                        Occupation = passenger.Occupation ?? string.Empty,
                        Stars = passenger.Stars,
                    });
                }
            }

            agency.WolfCrewRoutes[entry.UniqueId] = copy;
        }

        /// <summary>
        /// Returns true (with rejection details via out-params) when ANY
        /// passenger on the entry is currently aboard a vessel whose
        /// <c>OwningAgencyId</c> is non-Empty AND different from
        /// <paramref name="requesterAgency"/>. See class XML for the bypass
        /// rules.
        ///
        /// <para><b>Internal visibility</b> for ServerTest pinning the
        /// reject decision without bringing the full TryRoute path up.</para>
        /// </summary>
        internal static bool RejectIfCrossAgencyPassenger(
            AgencyWolfCrewRouteEntry entry,
            Guid requesterAgency,
            IReadOnlyDictionary<string, Guid> kerbalAgencyMap,
            out string offendingKerbal,
            out Guid offendingAgency)
        {
            offendingKerbal = null;
            offendingAgency = Guid.Empty;

            if (entry?.Passengers == null || entry.Passengers.Count == 0)
                return false;
            if (kerbalAgencyMap == null)
                return false;

            for (var i = 0; i < entry.Passengers.Count; i++)
            {
                var p = entry.Passengers[i];
                if (p == null || string.IsNullOrEmpty(p.Name))
                    continue;

                if (!kerbalAgencyMap.TryGetValue(p.Name, out var owning))
                    continue; // Kerbal not aboard any vessel — unassigned, bypass.
                if (owning == Guid.Empty)
                    continue; // Unassigned-sentinel vessel — spec §10 Q3 bypass.
                if (owning == requesterAgency)
                    continue; // Same-agency passenger — OK.

                offendingKerbal = p.Name;
                offendingAgency = owning;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pre-scans <see cref="VesselStoreSystem.CurrentVessels"/> and
        /// builds a kerbal-name → owning-agency map by parsing each
        /// vessel's serialized ConfigNode text for <c>"crew = {name}"</c>
        /// entries. Mirrors the K1 helper at
        /// <c>KerbalSystem.CanRemoveKerbalUnderK1</c> but produces a single
        /// map covering all kerbals in one pass (the router needs to check
        /// multiple passengers per call — pre-building amortises the
        /// per-vessel serialization cost).
        ///
        /// <para><b>Cost.</b> O(N vessels * vessel-text-size) per router
        /// invocation that has at least one entry with passengers. Router
        /// is called on Embark / Launch / Disembark postfixes, so cadence is
        /// bounded by WOLF UI clicks (not per-frame). Cost is also bounded
        /// by the per-test rebuild — we don't memoize across calls because
        /// VesselStore mutations (vessel proto re-broadcast, lmpOwningAgency
        /// transferagency push) would silently invalidate a cached map.</para>
        ///
        /// <para><b>Last-vessel-wins on duplicate kerbal sightings</b> — if
        /// the same kerbal name appears in two vessels (which shouldn't
        /// happen post-BUG-023 ProtoCrew scrub but is defensive), the LAST
        /// vessel's owning-agency wins. Matches the K1 helper's first-hit
        /// loop semantics (it exits on first match; we don't, because we
        /// build the full map upfront — but for cross-agency reject the
        /// "any non-Empty mismatch rejects" rule covers either case).</para>
        ///
        /// <para><b>Internal visibility</b> so ServerTest can inject a
        /// canned map and avoid the VesselStoreSystem dependency in the
        /// unit suite. Production callers always pass null and let
        /// <see cref="TryRoute"/> build the map.</para>
        /// </summary>
        internal static Dictionary<string, Guid> BuildKerbalAgencyMap()
        {
            var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var kvp in VesselStoreSystem.CurrentVessels)
            {
                var vessel = kvp.Value;
                if (vessel == null) continue;

                // **Multi-lens review MUST FIX #3** — snapshot OwningAgencyId
                // ONCE per vessel, NOT once per crew line. Reading the
                // property in the inner loop opens a race window with
                // concurrent admin /setvesselagency calls that could leave
                // kerbals from the same vessel mis-classified to different
                // agencies. The snapshot is also passed to the
                // preservation rule below so the entire per-vessel pass
                // sees a consistent OwningAgencyId.
                var owningAgencyId = vessel.OwningAgencyId;

                string vesselText;
                try
                {
                    vesselText = VesselStoreSystem.GetVesselInConfigNodeFormat(kvp.Key);
                }
                catch (Exception)
                {
                    continue; // skip vessels that fail to serialize; don't deadlock the router
                }
                if (string.IsNullOrEmpty(vesselText))
                    continue;

                // Scan for "crew = {name}" lines. Same shape as the K1
                // needle at KerbalSystem.cs:110 but extracting ALL crew
                // entries per vessel into the map instead of needle-matching
                // a single name. Crew entries appear on their own lines so
                // a simple split+trim is correct and fast.
                //
                // **Known limitation:** the scan matches any line starting
                // with "crew = " after Trim() — including (unlikely)
                // module-internal fields a mod might define. KSP's own
                // ProtoCrewMember persistence uses other key shapes (name=
                // / gender=) inside CREW nodes, so false positives from
                // stock content are absent. Mod-defined "crew = " in
                // unexpected scopes would pollute the map; downstream
                // effect would be a single benign reject. A ConfigNode-
                // parse-based scan would be tighter but adds substantial
                // cost; deferred as a Slice F+ hardening item if soak
                // surfaces an actual collision.
                var lines = vesselText.Split('\n');
                for (var li = 0; li < lines.Length; li++)
                {
                    var line = lines[li].Trim();
                    if (!line.StartsWith("crew = ", StringComparison.Ordinal))
                        continue;
                    var name = line.Substring(7).Trim();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    // **Multi-lens review MUST FIX #2 (preservation rule)**
                    // — the [[5.18b relay-vs-store note]] preservation
                    // pattern: non-Empty wins over Empty; never downgrade
                    // a real agency stamp to Unassigned-sentinel just
                    // because a different vessel listed the kerbal with
                    // Empty agency. Mirrors AgencyMembership.RecordOwnership
                    // semantics so the cross-agency reject decision is
                    // deterministic + griefer-resistant: a modified client
                    // can't put a target kerbal in a freshly-minted
                    // Empty-sentinel vessel to launder them into
                    // bypass-eligibility.
                    if (map.TryGetValue(name, out var existing)
                        && existing != Guid.Empty && owningAgencyId == Guid.Empty)
                    {
                        continue; // Preserve non-Empty entry; ignore Empty replacement.
                    }
                    map[name] = owningAgencyId;
                }
            }
            return map;
        }
    }
}
