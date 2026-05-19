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

        /// <summary>
        /// [Phase 3 Slice E-1] Vessel-keyed migration for the per-router
        /// <see cref="AgencyState.KolonyEntries"/> partition. Moves every
        /// entry whose dict-key prefix matches <paramref name="movedVesselId"/>
        /// from <paramref name="source"/> to <paramref name="destination"/>.
        ///
        /// <para><b>Caller contract</b> (pre-spec §4.e dual-lock ordering):
        /// caller MUST hold BOTH
        /// <c>AgencySystem.GetAgencyLock(source.AgencyId)</c> AND
        /// <c>AgencySystem.GetAgencyLock(destination.AgencyId)</c> for the
        /// duration of this call. Acquire the locks in
        /// <see cref="Guid.CompareTo"/> order — lower-comparing AgencyId
        /// first, then higher-comparing. (String-form ordering would
        /// disagree with byte-form ordering for some Guids; the .NET
        /// <see cref="Guid.CompareTo"/> is the authoritative comparison.)
        /// This prevents AB-BA deadlock against a concurrent reverse-
        /// direction transfer. The
        /// <see cref="ScenarioDataUpdater.GetSemaphore"/> BUG-033 precedent
        /// is the design template.</para>
        ///
        /// <para><b>Slice E-2 caller contract (load-bearing for the
        /// setvesselagency command author).</b> The full transfer
        /// orchestration the migration helper expects:</para>
        /// <list type="number">
        ///   <item><b>Same-stamp short-circuit BEFORE acquiring locks.</b>
        ///         If <c>vessel.OwningAgencyId == destination.AgencyId</c>,
        ///         return success-no-op WITHOUT calling this helper,
        ///         WITHOUT broadcasting <see cref="LmpCommon.Message.Data.Agency.AgencyVisibilityMsgData"/>,
        ///         and WITHOUT calling <c>SaveAgency</c>. The helper's
        ///         <c>ReferenceEquals(source, destination)</c> guard is a
        ///         defense-in-depth backstop — DO NOT rely on it for the
        ///         user-visible no-op log line.</item>
        ///   <item><b>Acquire dual locks in <see cref="Guid.CompareTo"/>
        ///         order</b> (lower-comparing AgencyId first). Same rule as
        ///         <see cref="ScenarioDataUpdater.GetSemaphore"/> BUG-033
        ///         precedent.</item>
        ///   <item><b>Mutate <c>vessel.OwningAgencyId = destination.AgencyId</c>
        ///         BEFORE calling the migration helpers</b> so the post-
        ///         migration cross-agency-rejection path (5.17a) treats
        ///         destination as the authoritative owner immediately.</item>
        ///   <item><b>Call all three per-router helpers</b>:
        ///         <c>AgencyKolonyRouter.MigrateForVesselTransfer</c>,
        ///         <c>AgencyOrbitalRouter.MigrateForVesselTransfer</c>,
        ///         <c>AgencyPlanetaryRouter.InspectAffectedEntriesForVesselTransfer</c>
        ///         (read-only, Q2 NO-MIGRATE).</item>
        ///   <item><b>Persist BOTH agencies</b> —
        ///         <c>AgencySystem.SaveAgency(source.AgencyId)</c> AND
        ///         <c>AgencySystem.SaveAgency(destination.AgencyId)</c>
        ///         before releasing the agency locks. Without this pair,
        ///         a crash before the next periodic
        ///         <c>BackupSystem.RunBackup</c> silently loses the
        ///         migration (both agencies re-load pre-migration state).
        ///         Pre-spec §4.e line 567 invariant.</item>
        ///   <item><b>Call <c>BackupSystem.RunBackup()</c> AFTER the
        ///         SaveAgency pair</b> to flush
        ///         <c>vessel.OwningAgencyId</c> to disk. Without this, the
        ///         vessel.cfg keeps the OLD agency stamp until the next
        ///         periodic backup — a crash in that window leaves
        ///         vessel.cfg disagreeing with AgencyState.txt, producing
        ///         a cross-agency-reject loop on reconnect. Mirrors
        ///         <c>DeleteAgencyCommand.cs:178-191</c>.</item>
        ///   <item><b>Release the source owner's stale vessel-scoped locks</b>
        ///         on the moved vessel (Control / Update / UnloadedUpdate).
        ///         The 5.17a cross-agency guard rejects NEW acquires from
        ///         the source owner, but EXISTING held locks remain — the
        ///         source owner's KSP keeps emitting vessel messages that
        ///         the 5.17a soak Finding-2 write-path guard silently drops.
        ///         Visible result: source owner's vessel freezes from her
        ///         perspective. Mirror
        ///         <c>DeleteAgencyCommand.cs:241-269 ReleaseOldOwnerLocksOnDemotedVessels</c>
        ///         filtered by <c>lock.VesselId == movedVesselId</c> rather
        ///         than the demoted-vessel set.</item>
        ///   <item><b>Emit wire messages in ORDER</b>: (a)
        ///         <c>AgencySystemSender.BroadcastVisibilityChange</c> with
        ///         the V→Bob entry FIRST, then (b) source-owner removal
        ///         echo, (c) destination-owner add echo. Channel 22 is
        ///         ReliableOrdered per-recipient, so the emit order = apply
        ///         order on the client side. Pre-Visibility echoes briefly
        ///         render "I still own V but its entries are gone" on the
        ///         source owner's client.</item>
        ///   <item><b>Release the dual lock.</b></item>
        /// </list>
        ///
        /// <para><b>Disk-flush cost under dual-lock critical section.</b>
        /// Both <c>SaveAgency</c> calls perform a <c>FileHandler.WriteAtomic</c>
        /// (full disk flush) under their respective agency lock acquires.
        /// If E-2 holds the dual lock across both SaveAgency calls,
        /// concurrent postfix mutations from players in EITHER source or
        /// destination agency block for two serial disk flushes. Latency
        /// hit, not a correctness bug — acceptable for an admin operation
        /// that typically fires once per session.</para>
        ///
        /// <para><b>Partition strategy</b>: prefix-scan
        /// <see cref="AgencyState.KolonyEntries"/> keys for
        /// <c>{movedVesselId:N}|*</c> — the dict-key shape established by
        /// <see cref="Upsert"/> is <c>$"{vesselId:N}|{bodyIndex}"</c>, so any
        /// entry contributed by the moved vessel (across any body index)
        /// matches. The body-index suffix is intentionally arbitrary —
        /// a vessel can have kolony entries on multiple bodies (a hopper
        /// touring Mun + Minmus + Eve), and all of them migrate together.</para>
        ///
        /// <para><b>Destination-collision policy</b>: in normal operation a
        /// vessel only belongs to one agency at a time so destination
        /// cannot already hold a key the source had. Defensively, this
        /// helper PREFERS source's entry on collision (more recent, since
        /// source held the vessel until now) — a destination collision
        /// implies operator hand-editing or a prior failed migration.
        /// The behavior is documented for symmetry with the orbital sibling
        /// (which has the same convention).</para>
        ///
        /// <para><b>Defensive guards</b>: returns an empty result without
        /// mutation when <paramref name="source"/> and
        /// <paramref name="destination"/> are the same instance (same-agency
        /// no-op; the Slice E-2 caller short-circuits this earlier with a
        /// same-stamp check, but the helper must not corrupt its own state
        /// if invoked defensively), and when <paramref name="movedVesselId"/>
        /// is <see cref="Guid.Empty"/> (the Unassigned sentinel never has
        /// agency-attributed entries by construction — entries land via the
        /// router's <see cref="TryRoute"/> first-served bypass into the
        /// CURRENT writer's agency, never into an "Empty-vessel agency"; a
        /// caller that passes Empty has confused the vessel id with the
        /// agency id).</para>
        ///
        /// <para><b>Internal visibility</b> matches <see cref="Upsert"/> —
        /// ServerTest reaches in to pin migration semantics directly without
        /// bringing up the full <see cref="TryRoute"/> path; cross-router
        /// MockClientTest scenarios (Slice E-2) cover the wire-level
        /// integration including the dual-lock acquire ordering.</para>
        /// </summary>
        internal static KolonyMigrationResult MigrateForVesselTransfer(
            AgencyState source, AgencyState destination, Guid movedVesselId)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            var result = new KolonyMigrationResult();
            if (ReferenceEquals(source, destination)) return result;
            if (movedVesselId == Guid.Empty) return result;

            // Snapshot keys-to-move first — mutating the dict while iterating
            // it is undefined. The prefix matches the Upsert key construction
            // at line 229: $"{vesselId:N}|{bodyIndex}". StringComparison.Ordinal
            // is correct because both the prefix and the dict keys are
            // canonical hex Guid "N" form (no culture-sensitive variation).
            var prefix = $"{movedVesselId:N}|";
            List<string> keysToMove = null;
            foreach (var kvp in source.KolonyEntries)
            {
                if (kvp.Key != null && kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (keysToMove == null) keysToMove = new List<string>();
                    keysToMove.Add(kvp.Key);
                }
            }
            if (keysToMove == null) return result;

            foreach (var key in keysToMove)
            {
                if (!source.KolonyEntries.TryGetValue(key, out var entry)) continue;
                source.KolonyEntries.Remove(key);
                // [Integration-logic review C1] Destination-collision
                // warning. By construction a vessel only belongs to one
                // agency, so dest cannot legitimately hold the same key.
                // Operator hand-edits or a prior failed migration could
                // produce a collision; source-wins is documented in the
                // class XML, but the operator gets a grep target via this
                // Warning so "my MKS pool numbers don't match" soak
                // reports can be traced.
                if (destination.KolonyEntries.ContainsKey(key))
                {
                    LunaLog.Warning(
                        $"[fix:MKS-R2] kolony migration collision: dest agency {destination.AgencyId:N} " +
                        $"already had key {key}; source agency {source.AgencyId:N} value wins per pre-spec §4.e.");
                }
                destination.KolonyEntries[key] = entry;
                result.RemovedKeys.Add(key);
                result.AddedEntries.Add(entry);
            }
            return result;
        }
    }

    /// <summary>
    /// [Phase 3 Slice E-1] Result of
    /// <see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/>. Wire-only
    /// transient — neither <see cref="RemovedKeys"/> nor
    /// <see cref="AddedEntries"/> is persisted to disk; the migration helper
    /// already mutated the canonical <c>AgencyState.KolonyEntries</c> dicts
    /// in-place, and these lists exist solely to drive the post-migration
    /// owner-only echo sends. Caller emits TWO sends:
    /// <list type="bullet">
    ///   <item><b>Source owner</b>:
    ///         <c>AgencySystemSender.SendKolonyStateToOwner(sourceOwner,
    ///         sourceAgencyId, entries: null, removedKeys: result.RemovedKeys)</c>
    ///         so the future client mirror's dict-remove apply clears its
    ///         source-agency cache. The named-arg <c>removedKeys:</c> shape
    ///         is the load-bearing call form — passing <c>entries: null</c>
    ///         is supported by the sender's optional-parameter contract.</item>
    ///   <item><b>Destination owner</b>:
    ///         <c>AgencySystemSender.SendKolonyStateToOwner(destOwner,
    ///         destAgencyId, entries: result.AddedEntries)</c>
    ///         (removedKeys omitted, defaults null) so the future client
    ///         mirror upserts the entries into its destination-agency
    ///         cache.</item>
    /// </list>
    /// Both echoes ride the same channel-22 wire and are guaranteed to arrive
    /// after the Slice 5.18d <c>AgencyVisibilityMsgData</c> broadcast of the
    /// vessel-ownership change (single channel = ReliableOrdered apply order).
    ///
    /// <para><b>Chunking note for Slice E-2 author</b>: realistic per-vessel
    /// migration produces small lists (capped by KSP body count, ~50). If a
    /// future cohort accumulates more entries than
    /// <see cref="AgencyKolonyStateMsgData.MaxEntryCount"/> or
    /// <see cref="AgencyKolonyStateMsgData.MaxRemovedKolonyKeyCount"/>, the
    /// sender's send-side cap-throw will fire (asymmetric-cap protection
    /// per [[feedback-wire-msgdata-chunking-caps]]). E-2 callers handling
    /// large migrations must split into multiple sends — model on
    /// <c>SendOrbitalCatchupTo</c>'s chunking loop.</para>
    /// </summary>
    internal class KolonyMigrationResult
    {
        public List<string> RemovedKeys { get; } = new List<string>();

        /// <summary>
        /// Entries the destination agency receives. Named "Added" rather
        /// than "Moved" to mirror the destination-side wire field shape
        /// (<see cref="AgencyKolonyStateMsgData.Entries"/> is the
        /// added-batch for the receiver). The same entry instances are
        /// referenced by both the helper's source-removal and destination-
        /// add operations; the caller pairs <see cref="RemovedKeys"/> with
        /// the source-owner echo and <see cref="AddedEntries"/> with the
        /// destination-owner echo (NOT one wire message carrying both —
        /// each agency owner only sees their own side).
        /// </summary>
        public List<AgencyKolonyEntry> AddedEntries { get; } = new List<AgencyKolonyEntry>();
    }
}
