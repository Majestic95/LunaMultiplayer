using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Log;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 3 Slice B — server-side per-agency router for MKS' kolony research.
    /// Sits behind the <c>AgencyMsgReader</c> dispatch for
    /// <see cref="LmpCommon.Message.Types.AgencyMessageType.KolonyState"/>. Replaces
    /// the legacy 30s SHA broadcast of <c>KolonizationScenario</c> with per-agency
    /// routing when <see cref="AgencySystem.PerAgencyEnabled"/> is true (gate=on AND
    /// Career mode). Owner-only echo + persistence; the projector
    /// (<see cref="AgencyScenarioProjector"/>) splices per-agency entries into outgoing
    /// <c>KolonizationScenario</c> blobs at <c>SendScenarioModules</c> time so each
    /// agency sees ONLY their own kolony entries — spec §10 Q1
    /// PrivateAgencyResources=true.
    ///
    /// <para><b>Single-class wire contract (pre-spec §2.e).</b>
    /// <see cref="AgencyKolonyStateMsgData"/> is used both directions on slot 6;
    /// inbound from the client postfix carries wire-supplied <c>AgencyId</c> that
    /// the server IGNORES. Sender authority is derived authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c> — same trust posture
    /// as <see cref="AgencyContractRouter.TryRoute"/>. Spoofing which agency a
    /// mutation is attributed to is structurally impossible.</para>
    ///
    /// <para><b>Per-entry isolation (pre-spec §3.a).</b> Classification +
    /// vessel lookup + cross-agency check + upsert are wrapped in a SINGLE try/catch
    /// per entry. A malformed VesselId, a missing-from-store vessel, or an upsert
    /// failure for one entry never aborts siblings. Matches
    /// <see cref="AgencyContractRouter"/>'s pre-spec §2.b.i step 5 shape (NOT the
    /// two-step shape — Slice B Round 4 review caught the same isolation regression
    /// AgencyContractRouter fixed in 5.17d).</para>
    ///
    /// <para><b>Cross-agency rejection (pre-spec §2.b.i step 5).</b> If the inbound
    /// entry's resolved vessel has a non-Empty <c>OwningAgencyId</c> that does NOT
    /// match the sender's agency, the entry is dropped with a Debug log line.
    /// Unassigned-sentinel (<c>OwningAgencyId == Guid.Empty</c>) bypasses the check
    /// per spec §10 Q3 — any agency may interact with pre-0.31 vessels until the
    /// admin stamps them via Stage 5.18d <c>transferagency</c>.</para>
    ///
    /// <para><b>Vessel-not-in-store decision (pre-spec §2.b.i step 5).</b> If the
    /// vessel referenced by the entry's <see cref="AgencyKolonyEntry.VesselId"/> is
    /// not present in <see cref="VesselStoreSystem.CurrentVessels"/>, the entry is
    /// dropped silently. The kolony postfix race against the vessel-proto store is
    /// inverted relative to 5.17a's lock-acquire — for vessel-mutation messages the
    /// 5.17a guard REJECTS the not-in-store case to close the proto-ingest race; for
    /// kolony entries we DROP because the entry would have no consumer (the
    /// projector reads <c>vessel.OwningAgencyId</c> at splice time and a not-in-store
    /// id would never project anyway). Same pragmatic asymmetry the soak-Finding-2
    /// VesselMsgReader cross-agency-write helper used for relay-vs-acquire branches.</para>
    ///
    /// <para><b>No shared-pool slot freeing.</b> Unlike
    /// <see cref="AgencyContractRouter"/>'s <c>RemoveContractFromSharedOfferedPool</c>
    /// path (Stage 5.17d), kolony entries have no Offered analogue — no peer-acceptance
    /// race. The shared <c>KolonizationScenario</c> still accumulates from any clients
    /// running with gate=off (in mixed-mode futures); under uniform gate=on the
    /// IgnoredScenarios Option B filter (Slice B item 10) prevents clients from
    /// broadcasting <c>KolonizationScenario</c> via the 30s SHA pass, so the shared
    /// scenario stays at the operator-supplied baseline.</para>
    ///
    /// <para><b>Dual-mode gate (spec §11).</b> With
    /// <see cref="AgencySystem.PerAgencyEnabled"/> false, <see cref="TryRoute"/>
    /// returns <c>false</c> immediately and the caller — <c>AgencyMsgReader</c> —
    /// drops the inbound silently (a client emitting <c>AgencyKolonyStateMsgData</c>
    /// under gate=off is already protocol-violating; the read path's gate-off branch
    /// in <c>AgencyMsgReader.HandleMessage</c> handles the same posture for sibling
    /// subtypes). Under uniform gate=off the postfix is also a no-op so this branch
    /// shouldn't fire in practice. The early-return matches every other Agency*
    /// surface for consistency.</para>
    /// </summary>
    public static class AgencyKolonyRouter
    {
        /// <summary>
        /// Attempts to route the inbound kolony state batch through the per-agency
        /// path. Returns <c>true</c> if this method handled the inbound; returns
        /// <c>false</c> when the gate is off, the client lacks an agency mapping
        /// (defensive — under gate=on every authenticated client has one via the
        /// handshake auto-register), or the agency registry entry is missing
        /// (defensive — same).
        /// </summary>
        public static bool TryRoute(ClientStructure client, AgencyKolonyStateMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            // Per-entry classify + upsert. The single-try/catch wraps the entire
            // per-entry pipeline (VesselId parse, vessel lookup, cross-agency
            // check, upsert) so one malformed entry never derails siblings.
            var accepted = new List<AgencyKolonyEntry>(msg.EntryCount);
            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                for (var i = 0; i < msg.EntryCount; i++)
                {
                    var entry = msg.Entries[i];
                    if (entry == null)
                        continue;

                    try
                    {
                        if (!Guid.TryParse(entry.VesselId, out var vesselGuid))
                        {
                            LunaLog.Debug($"[fix:MKS-R2] kolony entry skipped: malformed VesselId '{entry.VesselId}' (agency {agencyId:N})");
                            continue;
                        }

                        // [Round-1 general-lens MUST-FIX M1] Normalize VesselId to
                        // Guid "N" form at the TOP of the per-entry block — before
                        // any code path observes the raw wire value. This guarantees
                        // the same canonical form propagates through (a) the dict
                        // key in `Upsert`, (b) the owner-only echo in the `accepted`
                        // list, (c) the on-disk persisted form via SaveAgency, and
                        // (d) a future client mirror keying its local cache by
                        // VesselId. Without this normalization, a client sending
                        // "D" form (with hyphens) would receive an "N"-form echo,
                        // missing dedup against its own cache.
                        entry.VesselId = vesselGuid.ToString("N", CultureInfo.InvariantCulture);

                        if (!VesselStoreSystem.CurrentVessels.TryGetValue(vesselGuid, out var v))
                        {
                            // Vessel-not-in-store — pragmatic drop (see class XML).
                            LunaLog.Debug($"[fix:MKS-R2] kolony entry skipped: vessel {vesselGuid:N} not in store (agency {agencyId:N})");
                            continue;
                        }

                        // Cross-agency rejection. Unassigned-sentinel (Guid.Empty)
                        // bypasses per spec §10 Q3 — any agency may interact with
                        // pre-0.31 vessels until admin transferagency stamps them.
                        //
                        // [Round-1 general-lens CONSIDER C2 → applied] Bump from
                        // Debug to Warning per the 5.17a soak Finding-2 precedent
                        // (CLAUDE.md "Stack Notes" entry on cross-agency-write
                        // logging visibility). Malformed-VesselId + vessel-not-in-
                        // store stay at Debug (race-window cases on legitimate
                        // clients); only the cross-agency-claim case warrants
                        // operator-grep visibility.
                        if (v.OwningAgencyId != Guid.Empty && v.OwningAgencyId != agencyId)
                        {
                            LunaLog.Warning($"[fix:MKS-R2] kolony entry rejected: vessel {vesselGuid:N} owning agency {v.OwningAgencyId:N} != requester {agencyId:N}");
                            continue;
                        }

                        Upsert(agency, entry);
                        accepted.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        LunaLog.Error($"[fix:MKS-R2] kolony entry skipped for '{entry.VesselId}' (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            if (accepted.Count > 0)
            {
                AgencySystem.SaveAgency(agencyId);
                AgencySystemSender.SendKolonyStateToOwner(client, agencyId, accepted);
            }

            return true;
        }

        /// <summary>
        /// Inserts or replaces the kolony entry keyed by
        /// <c>$"{vesselId:N}|{bodyIndex}"</c>. Caller MUST hold
        /// <c>AgencySystem.GetAgencyLock(agencyId)</c> per the
        /// <see cref="AgencyState.KolonyEntries"/> concurrency contract. The
        /// caller-side lock is the same shape as
        /// <see cref="AgencyContractRouter.Upsert"/> + the AgencyState.cs:166-169
        /// dict XML.
        ///
        /// <para><b>Key construction (consumer-lens Lens-1 MF1).</b> The dict
        /// key is <c>$"{<see cref="AgencyKolonyEntry.VesselId"/>}|{bodyIndex}"</c>
        /// — VesselId is already Guid "N" form at this point (the router
        /// normalizes at ingest, line 109-118). <c>BodyIndex.ToString(CultureInfo.InvariantCulture)</c>
        /// is locale-safe for the integer case; the invariant-culture call is
        /// defensive belt-and-braces, not required for correctness. Slice C
        /// authors mirroring this pattern for planetary entries can read the key
        /// as <c>$"{bodyIndex}|{resourceName}"</c> WITHOUT carrying the
        /// invariant-culture concern (planetary is body+resource, no doubles).
        /// Slice D's <c>OrbitalTransfers</c> is keyed by
        /// <c>Guid TransferGuid</c> directly — no string key at all, no
        /// invariant-culture concern.</para>
        ///
        /// <para><b>Migration scan strategy</b> (for the Stage 5.18d
        /// MKS-aware <c>transferagency</c> extension in Slice E,
        /// consumer-lens Lens-3): per pre-spec §4.e, vessel-keyed entries
        /// migrate A→B with the moved vessel. Slice E's scan strategy MUST
        /// prefix-scan <see cref="AgencyState.KolonyEntries"/> keys for entries
        /// starting with <c>{movedVesselId:N}|</c> — the body-index suffix is
        /// arbitrary (a vessel can have entries for multiple body indices). A
        /// future Slice C planetary migration would value-field-scan against
        /// <c>OwningVesselId</c> (NOT key-prefix — planetary is body-keyed, not
        /// vessel-keyed); Slice D orbital migration value-field-scans against
        /// <c>OriginVesselId</c> + <c>DestinationVesselId</c>. Three different
        /// strategies for three different key shapes.</para>
        ///
        /// <para><b>Defensive copy.</b> Unlike
        /// <see cref="AgencyContractRouter.Upsert"/>'s ContractInfo byte-array copy,
        /// the kolony entry has no mutable byte-array fields — all 13 fields are
        /// value types or an immutable string. Reference assignment is safe under
        /// the per-agency lock + same-message-single-thread Lidgren receive
        /// contract. (A future field addition that introduces an array/byte-buffer
        /// payload would need a defensive copy here — pre-spec §3.c precedent.
        /// Slice D's <c>AgencyOrbitalTransferEntry.PayloadBytes</c> IS such a
        /// case; that slice will need <c>Buffer.BlockCopy</c> before
        /// upserting, matching the contracts precedent.)</para>
        ///
        /// <para><b>Internal visibility</b> matches
        /// <see cref="AgencyContractRouter.Upsert"/> — ServerTest reaches in to
        /// pin the upsert semantics without bringing the full <see cref="TryRoute"/>
        /// path up; the MockClientTest harness covers the wire-level integration.</para>
        /// </summary>
        internal static void Upsert(AgencyState agency, AgencyKolonyEntry entry)
        {
            if (agency == null)
                throw new ArgumentNullException(nameof(agency));
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            var key = $"{entry.VesselId}|{entry.BodyIndex.ToString(CultureInfo.InvariantCulture)}";
            agency.KolonyEntries[key] = entry;
        }
    }
}
