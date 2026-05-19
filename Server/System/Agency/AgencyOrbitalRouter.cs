using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Log;
using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 3 Slice D — server-side per-agency router for MKS' orbital-logistics
    /// transfer queue. Sits behind the <c>AgencyMsgReader</c> dispatch for
    /// <see cref="LmpCommon.Message.Types.AgencyMessageType.OrbitalState"/> (slot
    /// 8). Replaces the legacy 30s SHA broadcast of <c>ScenarioOrbitalLogistics</c>
    /// with per-agency routing when <see cref="AgencySystem.PerAgencyEnabled"/> is
    /// true (gate=on AND Career mode). Owner-only echo + persistence; the
    /// projector (<see cref="AgencyScenarioProjector"/>) splices per-agency
    /// transfers into outgoing <c>ScenarioOrbitalLogistics</c> blobs at
    /// <c>SendScenarioModules</c> time so each agency sees ONLY their own
    /// orbital transfers — spec §10 Q1 PrivateAgencyResources=true.
    ///
    /// <para><b>Structural template: Slice C's
    /// <see cref="AgencyPlanetaryRouter"/>.</b> Same single-try/catch
    /// isolation per entry, same dual-mode silence gate, same cross-agency
    /// rejection at Warning log level per the 5.17a soak Finding-2 precedent,
    /// same internal-visibility on <see cref="Upsert"/> for ServerTest
    /// reach-in. The DIFFERENCES from Slice B/C:</para>
    /// <list type="bullet">
    ///   <item><b>Partition key:</b>
    ///        <see cref="AgencyOrbitalTransferEntry.TransferGuid"/> — direct
    ///        Guid-keyed, no string composition. Three different key shapes
    ///        for three different per-router partitions:
    ///        kolony=<c>$"{vesselId:N}|{bodyIndex}"</c>,
    ///        planetary=<c>$"{bodyIndex}|{resourceName}"</c>,
    ///        orbital=<c>Guid</c> directly.</item>
    ///   <item><b>Cross-agency check applies to DESTINATION, not Origin.</b>
    ///        The destination's owning agency executes Deliver per pre-spec
    ///        §2.d Decision Table. Origin's owning agency is informational
    ///        (it was the agency that paid for the launch resources but
    ///        Deliver-time authority is destination-side). A transfer
    ///        emitted by sender S whose DESTINATION belongs to a different
    ///        agency than S is a wire violation — reject with Warning log.
    ///        Origin can be any agency or Empty; we don't gate on it.</item>
    ///   <item><b>Defensive byte-array copy on PayloadBytes.</b> Per pre-spec
    ///        §3.c — orbital is the FIRST Phase 3 entry with a mutable
    ///        <c>byte[]</c> field. Same hazard
    ///        <see cref="AgencyContractRouter"/> caught at lines 222-231:
    ///        storing the wire-buffer reference directly into
    ///        <see cref="AgencyState.OrbitalTransfers"/> lets a subsequent
    ///        re-arrival mutate the same buffer in place. <c>Buffer.BlockCopy</c>
    ///        before <see cref="Upsert"/>. The
    ///        <see cref="AgencyKolonyRouter"/> +
    ///        <see cref="AgencyPlanetaryRouter"/> entries have no mutable
    ///        byte-array fields so the defensive copy isn't needed there;
    ///        the asymmetry is intentional.</item>
    ///   <item><b>Migration policy on vessel-stamp change.</b> Per pre-spec
    ///        §4.e: transfers where the moved vessel is the DESTINATION
    ///        migrate (move-and-echo: source agency's owner-removal +
    ///        destination agency's owner-add); transfers where the moved
    ///        vessel is the ORIGIN stay in the source agency (default
    ///        policy, operator-confirmable). Slice E ships the MKS-aware
    ///        <c>transferagency</c> extension. Slice D's
    ///        <see cref="Upsert"/> just persists; the migration scan happens
    ///        at admin-command time and value-field-scans against both
    ///        <see cref="AgencyOrbitalTransferEntry.OriginVesselId"/> +
    ///        <see cref="AgencyOrbitalTransferEntry.DestinationVesselId"/> —
    ///        DIFFERENT from Slice B kolony's key-prefix scan and Slice C
    ///        planetary's no-migration policy.</item>
    /// </list>
    ///
    /// <para><b>Single-class wire contract (pre-spec §2.e).</b>
    /// <see cref="AgencyOrbitalStateMsgData"/> is used both directions on slot
    /// 8; inbound from the client state-machine postfixes carries
    /// wire-supplied <see cref="AgencyOrbitalStateMsgData.AgencyId"/> that
    /// the server IGNORES. Sender authority is derived authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c> — same trust
    /// posture as <see cref="AgencyKolonyRouter"/> +
    /// <see cref="AgencyPlanetaryRouter"/> + the Stage 5.17d
    /// <see cref="AgencyContractRouter"/>. Spoofing which agency a transfer is
    /// attributed to is structurally impossible.</para>
    ///
    /// <para><b>Per-entry isolation (pre-spec §3.a).</b> Cross-agency check +
    /// destination-vessel lookup + defensive PayloadBytes copy + upsert are
    /// wrapped in a SINGLE try/catch per entry. A malformed
    /// DestinationVesselId, a missing-from-store destination, a malformed
    /// PayloadBytes, or an upsert failure for one transfer never aborts
    /// siblings. Matches <see cref="AgencyKolonyRouter.TryRoute"/>'s shape
    /// directly.</para>
    ///
    /// <para><b>Cross-agency rejection (pre-spec §2.b.iii).</b> If the
    /// inbound entry's resolved destination vessel has a non-Empty
    /// <c>OwningAgencyId</c> that does NOT match the sender's agency, the
    /// entry is dropped with a Warning log line (5.17a soak Finding-2
    /// precedent, applied uniformly across Phase 3). Unassigned-sentinel
    /// (<c>OwningAgencyId == Guid.Empty</c>) bypasses the check per spec
    /// §10 Q3 — any agency may interact with pre-0.31 vessels until the
    /// admin stamps them via Stage 5.18d <c>transferagency</c>.</para>
    ///
    /// <para><b>Destination-vessel-not-in-store decision.</b> Same pragmatic
    /// DROP as Slice B/C — if the entry's
    /// <see cref="AgencyOrbitalTransferEntry.DestinationVesselId"/> is not
    /// in <see cref="VesselStoreSystem.CurrentVessels"/>, the entry is
    /// dropped with a Debug log line. The pre-spec asymmetry vs 5.17a is
    /// intentional: the postfix-vs-proto-ingest race is benign for orbital
    /// entries because the projector reads ownership from the canonical
    /// vessel store at splice time, so a not-in-store id would never project
    /// anyway.</para>
    ///
    /// <para><b>Empty-destination tolerance.</b> A
    /// <c>DestinationVesselId == Guid.Empty</c> at this stage means the
    /// client-side <c>ResolveDestinationVesselGuid</c> helper couldn't resolve
    /// the transfer's destination at postfix time (e.g. mid-scene-load when
    /// <c>FlightGlobals.Vessels</c> doesn't yet contain the target). Drop
    /// silently at Debug — the NEXT state-machine postfix tick on a stable
    /// vessel will produce a real id. Same posture as Slice C planetary's
    /// empty-OwningVesselId branch.</para>
    ///
    /// <para><b>Dual-mode gate (spec §11).</b> With
    /// <see cref="AgencySystem.PerAgencyEnabled"/> false, <see cref="TryRoute"/>
    /// returns <c>false</c> immediately and the caller — <c>AgencyMsgReader</c>
    /// — drops the inbound silently. Under uniform gate=off the state-machine
    /// postfixes are also no-ops so this branch shouldn't fire in practice.
    /// The early-return matches every other Agency* surface for consistency.
    /// Note that the Deliver-prefix (<c>OrbitalLogisticsTransferRequest_DeliverPrefix</c>)
    /// runs under BOTH gate=on AND gate=off — that's a sibling client-side
    /// concern, not this router's concern.</para>
    ///
    /// <para><b>No-echo contract for fully-rejected non-empty batches</b>
    /// (consumer-lens MUST FIX MF1). When every entry in a non-empty inbound
    /// batch is rejected (cross-agency on Destination + empty-Destination
    /// drop + not-in-store drop + empty-TransferGuid reject), <see cref="TryRoute"/>
    /// returns <c>true</c> (handled) but emits NO owner-only echo and DOES
    /// NOT persist (the <c>if (accepted.Count &gt; 0)</c> guard at the end of
    /// the method). This is intentional — there's nothing to echo and no
    /// state to persist — but consumers expecting "every inbound produces
    /// some response" need to know. The Slice D-2 MockClientTest for the
    /// cross-agency case asserts <i>watcher observes no echo</i>; do not
    /// assert "watcher observes empty-batch echo" because no message is
    /// sent. The contract is parallel to the EntryCount=0 empty-batch case
    /// documented at <see cref="AgencyOrbitalStateMsgData"/>.</para>
    /// </summary>
    public static class AgencyOrbitalRouter
    {
        /// <summary>
        /// Attempts to route the inbound orbital state batch through the
        /// per-agency path. Returns <c>true</c> if this method handled the
        /// inbound; returns <c>false</c> when the gate is off, the client
        /// lacks an agency mapping (defensive — under gate=on every
        /// authenticated client has one via the handshake auto-register), or
        /// the agency registry entry is missing (defensive — same).
        /// </summary>
        public static bool TryRoute(ClientStructure client, AgencyOrbitalStateMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            // Empty-batch observability — same shape as Slice C's MUST FIX
            // M1. EntryCount=0 inbound is structurally possible (buggy
            // client, future Slice E author probing wire shape) and would
            // otherwise produce zero log lines AND zero echo. Caller contract
            // on no echo per the AgencyOrbitalStateMsgData XML: inbound with
            // no accepted entries produces no echo by design (the postfix
            // never emits empty batches; only a buggy/probing client reaches
            // here with EntryCount=0).
            if (msg.EntryCount == 0)
            {
                LunaLog.Debug($"[fix:MKS-R2] empty orbital batch from {client.PlayerName} (agency {agencyId:N}) — no-op by design.");
                return true;
            }

            // Per-entry classify + upsert. The single-try/catch wraps the entire
            // per-entry pipeline (destination lookup, cross-agency check,
            // defensive PayloadBytes copy, upsert) so one malformed entry never
            // derails siblings. Same shape as AgencyKolonyRouter.TryRoute +
            // AgencyPlanetaryRouter.TryRoute step 5.
            var accepted = new List<AgencyOrbitalTransferEntry>(msg.EntryCount);
            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                for (var i = 0; i < msg.EntryCount; i++)
                {
                    var entry = msg.Entries[i];
                    if (entry == null)
                        continue;

                    try
                    {
                        // TransferGuid is the partition key; Empty would
                        // produce a Guid.Empty dict key collision across all
                        // empty-transfer-id submissions. Reject (rather than
                        // drop silently) at Warning level — operator visibility
                        // for a clear-error case where the client failed to
                        // generate / propagate the transfer's identity.
                        if (entry.TransferGuid == Guid.Empty)
                        {
                            LunaLog.Warning($"[fix:MKS-R2] orbital entry rejected: empty TransferGuid (agency {agencyId:N})");
                            continue;
                        }

                        // DestinationVesselId is the cross-agency gate; if
                        // empty, the client-side resolver couldn't pick a
                        // vessel at postfix time (mid-scene-load defensive).
                        // Drop at Debug — the next state-machine postfix on
                        // a stable vessel will produce a real id.
                        if (entry.DestinationVesselId == Guid.Empty)
                        {
                            LunaLog.Debug($"[fix:MKS-R2] orbital entry skipped: empty DestinationVesselId (agency {agencyId:N}, transfer {entry.TransferGuid:N})");
                            continue;
                        }

                        if (!VesselStoreSystem.CurrentVessels.TryGetValue(entry.DestinationVesselId, out var destVessel))
                        {
                            // Destination-vessel-not-in-store — pragmatic
                            // drop (see class XML). Same posture as Slice
                            // B/C.
                            LunaLog.Debug($"[fix:MKS-R2] orbital entry skipped: destination vessel {entry.DestinationVesselId:N} not in store (agency {agencyId:N})");
                            continue;
                        }

                        // Cross-agency rejection — gate on DESTINATION agency.
                        // Origin is informational (it paid for launch resources)
                        // but Deliver-time authority is destination-side per
                        // pre-spec §2.d. Unassigned-sentinel (Guid.Empty)
                        // bypasses per spec §10 Q3 — any agency may interact
                        // with pre-0.31 vessels until admin transferagency
                        // stamps them.
                        //
                        // Warning level matches Slice B/C per the 5.17a soak
                        // Finding-2 precedent (operator-grep visibility for
                        // cross-agency claim attempts). Malformed-DestinationVesselId
                        // + vessel-not-in-store stay at Debug (race-window cases
                        // on legitimate clients).
                        if (destVessel.OwningAgencyId != Guid.Empty && destVessel.OwningAgencyId != agencyId)
                        {
                            LunaLog.Warning($"[fix:MKS-R2] orbital entry rejected: destination vessel {entry.DestinationVesselId:N} owning agency {destVessel.OwningAgencyId:N} != requester {agencyId:N} (transfer {entry.TransferGuid:N})");
                            continue;
                        }

                        // Defensive copy of PayloadBytes per pre-spec §3.c.
                        // Slice D is the first Phase 3 entry with a mutable
                        // byte[] field; we copy out of the wire buffer into a
                        // stable per-agency buffer so a subsequent re-arrival
                        // can't tear the stored entry. Mirrors
                        // AgencyContractRouter's per-entry Buffer.BlockCopy at
                        // lines 222-231.
                        var srcBytes = entry.PayloadBytes ?? Array.Empty<byte>();
                        var srcLen = Math.Max(0, Math.Min(entry.NumBytes, srcBytes.Length));
                        var copiedBytes = srcLen > 0 ? new byte[srcLen] : Array.Empty<byte>();
                        if (srcLen > 0)
                            Buffer.BlockCopy(srcBytes, 0, copiedBytes, 0, srcLen);

                        var storedEntry = new AgencyOrbitalTransferEntry
                        {
                            TransferGuid = entry.TransferGuid,
                            OriginVesselId = entry.OriginVesselId,
                            DestinationVesselId = entry.DestinationVesselId,
                            Status = entry.Status,
                            StartTime = entry.StartTime,
                            Duration = entry.Duration,
                            PayloadBytes = copiedBytes,
                            NumBytes = srcLen,
                        };

                        Upsert(agency, storedEntry);
                        // For the owner-only echo we ship the SAME defensively-
                        // copied buffer reference. Wire serialization reads
                        // from this buffer; the dict holds it; both consumers
                        // get a stable per-entry buffer.
                        accepted.Add(storedEntry);
                    }
                    catch (Exception ex)
                    {
                        var transferId = entry?.TransferGuid ?? Guid.Empty;
                        LunaLog.Error($"[fix:MKS-R2] orbital entry skipped for transfer {transferId:N} (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            if (accepted.Count > 0)
            {
                AgencySystem.SaveAgency(agencyId);
                AgencySystemSender.SendOrbitalStateToOwner(client, agencyId, accepted);
            }

            return true;
        }

        /// <summary>
        /// Inserts or replaces the orbital transfer keyed by
        /// <see cref="AgencyOrbitalTransferEntry.TransferGuid"/>. Caller MUST
        /// hold <c>AgencySystem.GetAgencyLock(agencyId)</c> per the
        /// <see cref="AgencyState.OrbitalTransfers"/> concurrency contract.
        ///
        /// <para><b>Key construction.</b> Direct <c>Guid TransferGuid</c> —
        /// no string composition (distinct from Slice B's
        /// <c>$"{vesselId:N}|{bodyIndex}"</c> + Slice C's
        /// <c>$"{bodyIndex}|{resourceName}"</c> partitions). The Guid is
        /// already canonical from the wire entry's
        /// <see cref="AgencyOrbitalTransferEntry.TransferGuid"/> field; no
        /// "N"/"D" form drift is possible because the wire serializes Guid as
        /// 16 bytes via <c>GuidUtil</c>.</para>
        ///
        /// <para><b>Migration scan strategy</b> (for the Stage 5.18d MKS-aware
        /// <c>transferagency</c> extension in Slice E): per pre-spec §4.e,
        /// orbital transfers value-field-scan against both
        /// <see cref="AgencyOrbitalTransferEntry.OriginVesselId"/> AND
        /// <see cref="AgencyOrbitalTransferEntry.DestinationVesselId"/>.
        /// When the moved vessel matches an entry's Destination, MOVE the
        /// entry from source agency to destination agency (operator policy
        /// — destination's owner is the deliverer per §2.d). When the moved
        /// vessel matches an entry's Origin, KEEP in source agency by default
        /// (operator may override; see pre-spec §11 Q8 for the Slice E
        /// implementation-time prompt). NEITHER strategy is key-prefix —
        /// the partition key is Guid TransferGuid and there's no encoded
        /// vessel-id substring to scan. Distinct from Slice B kolony's
        /// key-prefix scan and Slice C planetary's no-migration policy —
        /// three different strategies for three different key shapes.</para>
        ///
        /// <para><b>Defensive copy.</b> The caller (<see cref="TryRoute"/>)
        /// performs <c>Buffer.BlockCopy</c> on PayloadBytes BEFORE invoking
        /// <see cref="Upsert"/>. The orbital entry has a mutable
        /// <c>byte[] PayloadBytes</c> field; the caller-side copy avoids
        /// alias-mutation hazards from wire-buffer re-use. Slice E migration
        /// callers passing pre-snapshotted entries from inside their
        /// per-agency lock critical section can pass the entry directly
        /// without re-copying — the entry is already stable in
        /// <see cref="AgencyState.OrbitalTransfers"/>.</para>
        ///
        /// <para><b>Internal visibility</b> matches
        /// <see cref="AgencyKolonyRouter.Upsert"/> +
        /// <see cref="AgencyPlanetaryRouter.Upsert"/> — ServerTest reaches in
        /// to pin the upsert semantics without bringing the full
        /// <see cref="TryRoute"/> path up; the MockClientTest harness covers
        /// the wire-level integration.</para>
        /// </summary>
        internal static void Upsert(AgencyState agency, AgencyOrbitalTransferEntry entry)
        {
            if (agency == null)
                throw new ArgumentNullException(nameof(agency));
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            agency.OrbitalTransfers[entry.TransferGuid] = entry;
        }

        /// <summary>
        /// [Phase 3 Slice E-1] Vessel-as-Destination migration for the
        /// per-router <see cref="AgencyState.OrbitalTransfers"/> partition.
        /// Moves every transfer whose
        /// <see cref="AgencyOrbitalTransferEntry.DestinationVesselId"/>
        /// matches <paramref name="movedVesselId"/> from
        /// <paramref name="source"/> to <paramref name="destination"/>.
        /// Transfers where ONLY the
        /// <see cref="AgencyOrbitalTransferEntry.OriginVesselId"/> matches
        /// (operator session 29 Q1) KEEP in source — the launch obligation
        /// was incurred in source's frame, source retains the "what did I
        /// ship out" history, destination only inherits transfers whose
        /// delivery authority follows the moved destination vessel.
        ///
        /// <para><b>Caller contract</b>: same dual-lock ordering as
        /// <see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/> — caller
        /// holds both source + destination agency locks, acquired in
        /// <see cref="Guid.CompareTo"/> order (lower-comparing AgencyId
        /// first). See that helper's XML for the BUG-033 design template +
        /// the lock-ordering rationale.</para>
        ///
        /// <para><b>Slice E-2 caller contract</b>: see
        /// <see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/>'s XML
        /// for the full 9-step orchestration (same-stamp short-circuit →
        /// dual-lock acquire → vessel.OwningAgencyId mutation → three
        /// per-router helper calls → dual SaveAgency → BackupSystem.RunBackup
        /// → stale-lock release → wire emit order → lock release). The
        /// orbital helper sits at step 4 alongside the kolony + planetary
        /// helpers; all three share the same caller orchestration. This
        /// helper additionally produces <see cref="OrbitalMigrationResult.OriginOnlyKeptGuids"/>
        /// for the per-spec §4.e operator-visible info-log emit.</para>
        ///
        /// <para><b>Origin-only retained transfers (Q1 KEEP) carry a
        /// cross-agency reference after migration.</b> A transfer where V
        /// is Origin-only keeps in source agency A, but its
        /// <see cref="AgencyOrbitalTransferEntry.OriginVesselId"/> field
        /// continues to point at V which now belongs to destination agency
        /// B. The projector splice on <c>ScenarioOrbitalLogistics</c> still
        /// projects this transfer to A's scenario; A's client UI may
        /// render "Transfer from &lt;B's vessel&gt; to &lt;A's vessel&gt;" —
        /// confusing but spec-correct (A retains the launch obligation;
        /// the rendering of the origin vessel's owning-agency label is a
        /// Slice 5.18c UI concern, not Phase 3 routing). Operator awareness
        /// is the per-guid info log emitted from
        /// <see cref="OrbitalMigrationResult.OriginOnlyKeptGuids"/>.</para>
        ///
        /// <para><b>Self-transfer edge case</b>: when the moved vessel is
        /// BOTH the Origin AND the Destination of the same transfer (a
        /// self-delivery — uncommon but valid in MKS for in-place resource
        /// movement), the Destination-match path fires (transfer migrates).
        /// Pre-spec §4.e line 217-218: "For self-transfer prefer the
        /// Destination-match path (deliverer authority)." Implemented via
        /// checking <c>DestinationVesselId == movedVesselId</c> first; if it
        /// matches, the Origin-match check is unreachable for that entry.</para>
        ///
        /// <para><b>Defensive guards</b>: returns an empty result when
        /// <paramref name="source"/> and <paramref name="destination"/> are
        /// the same instance, and when <paramref name="movedVesselId"/> is
        /// <see cref="Guid.Empty"/> (Unassigned sentinel — see kolony
        /// sibling's rationale).</para>
        ///
        /// <para><b>Defensive copy</b>: entries in
        /// <see cref="AgencyState.OrbitalTransfers"/> are the canonical
        /// store; their <see cref="AgencyOrbitalTransferEntry.PayloadBytes"/>
        /// have been defensively-copied by the original router
        /// <see cref="TryRoute"/> at ingest. The migration only re-homes the
        /// reference between dicts — no re-copy needed (the entry's bytes
        /// were never aliased to a wire-buffer in the first place once
        /// upserted).</para>
        ///
        /// <para><b>Internal visibility</b> matches <see cref="Upsert"/>.</para>
        /// </summary>
        internal static OrbitalMigrationResult MigrateForVesselTransfer(
            AgencyState source, AgencyState destination, Guid movedVesselId)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            var result = new OrbitalMigrationResult();
            if (ReferenceEquals(source, destination)) return result;
            if (movedVesselId == Guid.Empty) return result;

            // Snapshot guids-to-move and origin-keep guids first. Mutating
            // the dict during iteration is undefined. Capture origin-keeps
            // by guid (not just count) so operator logs can list per-guid
            // detail per spec §4.e — the Slice E-2 author calls
            // `result.OriginOnlyKeptGuids.ForEach(log)` to emit one info
            // line per kept obligation, plus uses `Count` for the summary.
            List<Guid> guidsToMove = null;
            foreach (var kvp in source.OrbitalTransfers)
            {
                var entry = kvp.Value;
                if (entry == null) continue;
                if (entry.DestinationVesselId == movedVesselId)
                {
                    if (guidsToMove == null) guidsToMove = new List<Guid>();
                    guidsToMove.Add(kvp.Key);
                }
                else if (entry.OriginVesselId == movedVesselId)
                {
                    // Q1 KEEP — Origin-only match stays in source. No
                    // mutation; recorded for caller diagnostic.
                    result.OriginOnlyKeptGuids.Add(kvp.Key);
                }
            }

            if (guidsToMove == null) return result;

            foreach (var transferGuid in guidsToMove)
            {
                if (!source.OrbitalTransfers.TryGetValue(transferGuid, out var entry)) continue;
                source.OrbitalTransfers.Remove(transferGuid);
                // [Integration-logic review C1] Destination-collision
                // warning. TransferGuid is a global Guid so collision is
                // essentially impossible without operator hand-edit;
                // emit a Warning for defensive symmetry with the kolony
                // sibling so soak operators see a single grep pattern
                // for migration-collision events.
                if (destination.OrbitalTransfers.ContainsKey(transferGuid))
                {
                    LunaLog.Warning(
                        $"[fix:MKS-R2] orbital migration collision: dest agency {destination.AgencyId:N} " +
                        $"already had transfer {transferGuid:N}; source agency {source.AgencyId:N} value wins.");
                }
                destination.OrbitalTransfers[transferGuid] = entry;
                result.RemovedTransferGuids.Add(transferGuid);
                result.AddedEntries.Add(entry);
            }
            return result;
        }
    }

    /// <summary>
    /// [Phase 3 Slice E-1] Result of
    /// <see cref="AgencyOrbitalRouter.MigrateForVesselTransfer"/>. Wire-only
    /// transient — neither <see cref="RemovedTransferGuids"/> nor
    /// <see cref="AddedEntries"/> nor <see cref="OriginOnlyKeptGuids"/> is
    /// persisted to disk; the migration helper already mutated the canonical
    /// <c>AgencyState.OrbitalTransfers</c> dicts in-place (and left
    /// <see cref="OriginOnlyKeptGuids"/> entries in source per Q1).
    ///
    /// <para><b>Caller emits TWO owner-only sends</b>:
    /// <list type="bullet">
    ///   <item><b>Source owner</b>:
    ///         <c>AgencySystemSender.SendOrbitalStateToOwner(sourceOwner,
    ///         sourceAgencyId, entries: null, removedTransferGuids: result.RemovedTransferGuids)</c></item>
    ///   <item><b>Destination owner</b>:
    ///         <c>AgencySystemSender.SendOrbitalStateToOwner(destOwner,
    ///         destAgencyId, entries: result.AddedEntries)</c>
    ///         (removedTransferGuids omitted, defaults null).</item>
    /// </list>
    /// Both echoes ride channel 22 in ReliableOrdered apply order behind the
    /// 5.18d <see cref="LmpCommon.Message.Data.Agency.AgencyVisibilityMsgData"/>
    /// vessel-ownership broadcast.</para>
    ///
    /// <para><b>Operator log emit pattern (pre-spec §4.e):</b> for each
    /// guid in <see cref="OriginOnlyKeptGuids"/>, emit a
    /// <c>[fix:MKS-R2] transferagency kept origin-transfer={guid:N}</c>
    /// line so operators see which in-flight obligations source agency
    /// retained. The Count alone gives the summary; the per-guid list
    /// gives the audit trail.</para>
    ///
    /// <para><b>Chunking note for Slice E-2 author</b>: realistic per-vessel
    /// migration produces small lists. A vessel that has been Destination
    /// of more than <see cref="AgencyOrbitalStateMsgData.MaxEntryCount"/>
    /// (1024) historical transfers would trip the sender's send-side
    /// cap-throw — split into multiple sends per the
    /// <c>SendOrbitalCatchupTo</c> chunking template.</para>
    /// </summary>
    internal class OrbitalMigrationResult
    {
        public List<Guid> RemovedTransferGuids { get; } = new List<Guid>();

        /// <summary>
        /// Entries the destination agency receives. Named "Added" rather
        /// than "Moved" to mirror the destination-side wire field shape
        /// (<see cref="AgencyOrbitalStateMsgData.Entries"/> is the
        /// added-batch for the receiver). Same entry instances are
        /// referenced by both the helper's source-removal and destination-
        /// add operations.
        /// </summary>
        public List<AgencyOrbitalTransferEntry> AddedEntries { get; } = new List<AgencyOrbitalTransferEntry>();

        /// <summary>
        /// Transfer guids where the moved vessel was the Origin only
        /// (NOT also the Destination). Per operator session 29 Q1, these
        /// transfers KEEP in source — the launch obligation was incurred
        /// in source's frame. Source agency continues to deliver to the
        /// (unchanged) destination vessel; the moved vessel just stops
        /// being attributed to source. The caller emits per-guid log lines
        /// for the operator audit trail.
        /// </summary>
        public List<Guid> OriginOnlyKeptGuids { get; } = new List<Guid>();

        /// <summary>
        /// Convenience: <c>OriginOnlyKeptGuids.Count</c>. Operators reading
        /// the one-line summary want the count; the full guid list is in
        /// <see cref="OriginOnlyKeptGuids"/> for per-guid audit lines.
        /// </summary>
        public int OriginOnlyKeptCount => OriginOnlyKeptGuids.Count;
    }
}
