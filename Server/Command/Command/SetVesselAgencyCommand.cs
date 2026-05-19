using LmpCommon.Locks;
using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Command.Command.Base;
using Server.Log;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Vessel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Command.Command
{
    /// <summary>
    /// Phase 3 Slice E-2 (MKS-compat Phase 3 closer). Admin command that
    /// REASSIGNS a single vessel from its current owning agency to a different
    /// one. Mutates <c>Vessel.OwningAgencyId</c> in the canonical store,
    /// migrates the per-router MKS partitions per pre-spec §4.e, releases the
    /// source owner's stale vessel-scoped locks, and broadcasts the ownership
    /// transition via <see cref="AgencyVisibilityMsgData"/>.
    ///
    /// <para><b>Why a NEW command, not extending <c>/transferagency</c>.</b>
    /// Stage 5.18d slice (e) <c>/transferagency</c> is an OWNER-RENAME — it
    /// preserves the agency's <c>AgencyId</c>, vessels keep their
    /// <c>lmpOwningAgency</c> stamp, only the player handle attached to the
    /// agency changes. The vessel-level A→B substantive reassignment surface
    /// is THIS command (operator confirmed session 30 / 2026-05-18). The
    /// pre-spec §4.e migration logic (per-router policies, dual-lock ordering,
    /// wire echo shape) is preserved on this command — only the command-name
    /// attachment shifted.</para>
    ///
    /// <para><b>9-step orchestration</b> (matches the load-bearing contract
    /// documented in <see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/>
    /// XML, replicated here for the operator-command reader):</para>
    /// <list type="number">
    ///   <item><b>Same-stamp short-circuit BEFORE acquiring locks.</b>
    ///         No-op log; no helpers, no broadcast, no SaveAgency. Returns
    ///         true (success-no-op).</item>
    ///   <item><b>Acquire dual locks in <see cref="Guid.CompareTo"/> order.</b>
    ///         Lower-comparing AgencyId first. BUG-033 precedent. When the
    ///         source is Unassigned (vessel.OwningAgencyId == Guid.Empty)
    ///         there is no source AgencyState — collapse to single-lock on
    ///         destination only.</item>
    ///   <item><b>Mutate <c>vessel.OwningAgencyId = destination.AgencyId</c>
    ///         BEFORE calling migration helpers.</b> Post-migration the
    ///         5.17a cross-agency rejection treats destination as
    ///         authoritative immediately.</item>
    ///   <item><b>Call the three per-router helpers</b> (skip when source
    ///         is null — Unassigned-source has no per-router entries by
    ///         construction; the routers' first-served path puts entries
    ///         under the CURRENT writer's agency, never under an "Empty
    ///         vessel agency"):
    ///         <see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/>,
    ///         <see cref="AgencyOrbitalRouter.MigrateForVesselTransfer"/>,
    ///         <see cref="AgencyPlanetaryRouter.InspectAffectedEntriesForVesselTransfer"/>
    ///         (read-only, Q2 NO-MIGRATE).</item>
    ///   <item><b>Persist BOTH agencies</b> (only destination when source
    ///         is null/orphaned).
    ///         <see cref="AgencySystem.SaveAgency"/> before releasing the
    ///         agency locks. Pre-spec §4.e line 567 invariant — without
    ///         this pair a crash before the next periodic
    ///         <see cref="BackupSystem.RunBackup"/> silently loses the
    ///         migration.</item>
    ///   <item><b><see cref="BackupSystem.RunBackup"/> AFTER the SaveAgency
    ///         pair</b> to flush <c>vessel.OwningAgencyId</c> to the
    ///         universe vessel file. Without this the vessel.cfg keeps the
    ///         OLD agency stamp until the next periodic backup; a crash in
    ///         that window leaves vessel.cfg disagreeing with
    ///         AgencyState.txt → cross-agency-reject loop on reconnect.
    ///         Same call shape as <see cref="DeleteAgencyCommand"/>.</item>
    ///   <item><b>Release the source owner's stale vessel-scoped locks</b>
    ///         (Control / Update / UnloadedUpdate) on the moved vessel.
    ///         The 5.17a guard rejects NEW acquires but existing held
    ///         locks remain — the source owner's KSP keeps emitting vessel
    ///         messages that the soak Finding-2 write-path guard silently
    ///         drops → vessel freezes from her perspective. Mirror
    ///         <see cref="DeleteAgencyCommand"/>'s
    ///         <c>ReleaseOldOwnerLocksOnDemotedVessels</c> filtered by
    ///         <c>lock.VesselId == movedVesselId</c> rather than the
    ///         demoted-vessel set. For Unassigned-source, release ALL
    ///         vessel-scoped locks on this vessel held by players NOT in
    ///         the destination agency (anyone could have grabbed a lock
    ///         under the spec §10 Q3 Unassigned bypass; we don't have a
    ///         single old-owner handle to filter by).</item>
    ///   <item><b>Emit wire messages in ORDER</b>: (a)
    ///         <see cref="AgencySystemSender.BroadcastVisibilityChange"/>
    ///         with the V→destAgencyId entry FIRST, then (b) source-owner
    ///         removal echoes (kolony + orbital), then (c)
    ///         destination-owner add echoes (kolony + orbital). Channel
    ///         22 is ReliableOrdered per-recipient so emit order = apply
    ///         order client-side. Pre-Visibility echoes would briefly
    ///         render "I still own V but its entries are gone" on the
    ///         source owner's client.</item>
    ///   <item><b>Release the dual lock.</b> (Handled implicitly by
    ///         the nested <c>lock</c> statements unwinding.)</item>
    /// </list>
    ///
    /// <para><b>Reversibility.</b> Re-run with the original agency token to
    /// undo. No <c>--confirm</c> flag — the operation is reversible and the
    /// state mutation is bounded (one vessel + the agency's per-router
    /// partitions referencing that vessel).</para>
    ///
    /// <para><b>Connected-source-owner handling.</b> If the source owner is
    /// currently connected, their client's view briefly mismatches:
    /// vessel.OwningAgencyId is now destination on the server but their
    /// VesselSync cache still says source. The next VesselSync round-trip
    /// + the AgencyVisibilityMsgData broadcast both correct this. The
    /// source-owner removal echoes (kolony + orbital) prune their per-agency
    /// state caches for V. Operators wanting an instant client-side
    /// convergence can <c>/kick</c> the source owner; on reconnect they get
    /// fresh per-agency catch-up.</para>
    /// </summary>
    public class SetVesselAgencyCommand : SimpleCommand
    {
        private static readonly HashSet<LockType> VesselScopedLockTypes = new HashSet<LockType>
        {
            LockType.Control,
            LockType.Update,
            LockType.UnloadedUpdate,
        };

        public override bool Execute(string commandArgs)
        {
            // (1) Pure parse.
            if (!SetVesselAgencyCommandParser.TryParse(commandArgs, out var vesselToken, out var agencyToken, out var parseError))
            {
                LunaLog.Error(parseError);
                LunaLog.Normal(SetVesselAgencyCommandParser.UsageBanner);
                return false;
            }

            // (2) Gate refusal — same shape as /setagency + /transferagency +
            // /deleteagency. Both off-states refuse loudly with the resolution
            // path inlined.
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
            {
                LunaLog.Error(
                    "setvesselagency: requires PerAgencyCareer=true. Under PerAgencyCareer=false vessels carry " +
                    "no agency stamp; there is no per-vessel ownership to reassign.");
                return false;
            }
            if (!AgencySystem.PerAgencyEnabled)
            {
                LunaLog.Error(
                    "setvesselagency: requires GameMode=Career. PerAgencyCareer=true but GameMode is not Career — set " +
                    "GameMode=Career in Settings/GeneralSettings.xml to activate, or set PerAgencyCareer=false in " +
                    "Settings/GameplaySettings.xml to disable per-agency cleanly (may flip GameDifficulty to Custom " +
                    "— see CLAUDE.md Settings caveat).");
                return false;
            }

            // (3) Resolve vessel guid. Accept both "N" (32 hex) and "D" (with hyphens)
            // forms — Guid.TryParse handles both. Normalise to canonical Guid for
            // VesselStoreSystem lookup (the dict key is Guid not string).
            if (!Guid.TryParse(vesselToken, out var movedVesselId))
            {
                LunaLog.Error(
                    $"setvesselagency: vessel-guid '{vesselToken}' is not a parseable Guid. Accept either " +
                    "the 32-hex \"N\" form (matches lmpOwningAgency on-disk shape) or the hyphenated \"D\" form " +
                    "(matches /listclients output). Run /vessel info to confirm vessel guids on this server.");
                return false;
            }
            if (movedVesselId == Guid.Empty)
            {
                LunaLog.Error(
                    "setvesselagency: vessel-guid must be non-empty. Guid.Empty is the Unassigned-vessel sentinel " +
                    "(spec §10 Q3), not a real vessel identity.");
                return false;
            }
            if (!VesselStoreSystem.CurrentVessels.TryGetValue(movedVesselId, out var vessel))
            {
                LunaLog.Error(
                    $"setvesselagency: no vessel with guid {movedVesselId:N} is present in the universe store. " +
                    "Vessel guids change when KSP regenerates a vessel (e.g. recovery + relaunch); run /vessel info " +
                    "to confirm the current guid.");
                return false;
            }

            // (4) Resolve destination agency token.
            if (!AgencySystem.TryResolveAgencyToken(agencyToken, out var destination))
            {
                if (AgencySystem.Agencies.IsEmpty)
                {
                    LunaLog.Error(
                        "setvesselagency: no agencies are registered yet. An agency mints on the owning player's " +
                        "first connect under PerAgencyCareer=true. /listagencies confirms the current registry state.");
                }
                else
                {
                    LunaLog.Error(
                        $"setvesselagency: agency token '{agencyToken}' does not match any registered agency. " +
                        "Pass either an agency id (run /listagencies) or the agency owner's REGISTRATION-time " +
                        "LMP handle. Orphaned agency ids from boot warnings have no in-memory AgencyState; recover " +
                        "by restoring Universe/Agencies/{guid}.txt(.bak), or accept the loss and let owning players " +
                        "mint fresh agencies on reconnect.");
                }
                return false;
            }

            // Snapshot pre-mutation source identity. Source AgencyState may be:
            //  a) Non-null + registered — typical A→B
            //  b) Null because vessel.OwningAgencyId == Guid.Empty (Unassigned)
            //  c) Null because the id is non-Empty but orphaned (AgencyState file
            //     missing — caught by boot warning). Treated same as (b) below.
            var sourceAgencyId = vessel.OwningAgencyId;
            AgencySystem.Agencies.TryGetValue(sourceAgencyId, out var source);
            var sourceIsUnassigned = sourceAgencyId == Guid.Empty;
            var sourceIsOrphaned = !sourceIsUnassigned && source == null;
            if (sourceIsOrphaned)
            {
                // Round-1 upgrade-lens SHOULD FIX S2 — orphan upgrade gets BOTH
                // a Warning (operator may want to investigate why an agency
                // file went missing) and a Normal-level line (so the
                // (verb, vessel-id, key=value) GUI grep picks up the
                // operator-action in the same stream as the regular summary).
                LunaLog.Warning(
                    $"[fix:per-agency-career] setvesselagency vessel={movedVesselId:N} has orphaned source agency " +
                    $"{sourceAgencyId:N} (stamp present, AgencyState absent). Proceeding without source-side " +
                    "migration — there are no per-router partitions to move FROM. The destination stamp + " +
                    "visibility broadcast + lock release still apply.");
                LunaLog.Normal(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} upgrade-from-orphan " +
                    $"source-id={sourceAgencyId:N} dest={destination.AgencyId:N}");
            }

            var sourceOwnerName = source?.OwningPlayerName ?? string.Empty;
            var destAgencyId = destination.AgencyId;
            var destOwnerName = destination.OwningPlayerName ?? string.Empty;

            // Step 1: Same-stamp short-circuit BEFORE acquiring locks. The
            // ReferenceEquals guards inside the migration helpers are
            // defense-in-depth backstops — DO NOT rely on them for the
            // operator-visible no-op log.
            if (sourceAgencyId == destAgencyId)
            {
                // Round-1 consumer-lens MUST FIX — result=noop key lets a GUI
                // launcher's regex match `result=(\w+)` cleanly for "did
                // anything change?" without substring-matching `no-op` text.
                LunaLog.Normal(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} result=noop " +
                    $"dest={destAgencyId:N} dest-owner='{destOwnerName}' " +
                    "(vessel already assigned to this agency)");
                return true;
            }

            // Step 2: Acquire dual locks in Guid.CompareTo order. When source
            // is null (Unassigned or orphaned), collapse to single-lock on
            // destination only — there's no source partition to read.
            //
            // Step 3-5 happen INSIDE the locks:
            //   * mutate vessel.OwningAgencyId = destAgencyId
            //   * call three per-router helpers (skip when source is null)
            //   * SaveAgency(destination) (always)
            //   * SaveAgency(source.AgencyId) (when source non-null)
            //
            // The dual-lock critical section is held across both SaveAgency
            // calls; the per-agency disk-flush cost is documented in
            // AgencyKolonyRouter.MigrateForVesselTransfer XML.
            KolonyMigrationResult kolonyResult = null;
            OrbitalMigrationResult orbitalResult = null;
            PlanetaryInspectionResult planetaryResult = null;

            // Track whether we actually performed a mutation under the lock.
            // The post-lock re-check (Round-1 integration-logic CONSIDER C1)
            // can short-circuit a concurrent second operator's call after the
            // first one already landed. False here means "no mutation, no
            // wire emit, no audit lines" — same observable shape as the
            // step-1 same-stamp short-circuit but logged distinctly.
            var mutated = false;

            RunUnderLockOrder(source, destination, () =>
            {
                // Step 2b (Round-1 integration-logic CONSIDER C1): post-lock
                // re-check. Two concurrent operators on the same vessel both
                // pass step 1, then serialize on the dual lock. The first
                // mutates; the second enters here with the stamp already
                // updated — short-circuit so we don't run an empty migration,
                // burn two SaveAgency disk flushes, and emit a duplicate
                // Visibility broadcast.
                if (vessel.OwningAgencyId == destAgencyId)
                    return;

                // Step 3: Mutate vessel.OwningAgencyId BEFORE calling migration
                // helpers. Post-mutation the 5.17a cross-agency rejection treats
                // destination as authoritative immediately, so the migration
                // helpers (which iterate sourceState's per-router dicts) cannot
                // race against a concurrent same-agency router upsert that would
                // re-claim the moved vessel — the router's check at line 146
                // sees the new stamp.
                //
                // [Round-1 general-lens MUST FIX #1] The vessel field write
                // MUST be wrapped in the per-vessel
                // <see cref="VesselDataUpdater.GetVesselLock"/> object — the
                // dual agency lock held by RunUnderLockOrder gates per-router
                // dicts, NOT vessel fields. The proto-ingest path at
                // VesselDataUpdater.cs:88 holds this same lock around its
                // existingStored read + auth-preserve write (S4 retro-review
                // precedent). Without the lock here, a concurrent proto-
                // ingest could read OwningAgencyId pre-mutation, run the
                // sticky-existing branch (line 134), and silently revert our
                // mutation back to source. The 5.17a guard's reads from
                // receive threads see whatever Fields.Update wrote last.
                lock (VesselDataUpdater.GetVesselLock(movedVesselId))
                {
                    vessel.OwningAgencyId = destAgencyId;
                }
                mutated = true;

                // Step 4: Call all three per-router helpers (skip when source
                // is null — Unassigned-source has no entries to migrate FROM
                // by construction).
                //
                // [Round-1 integration-logic MUST FIX #2 — POSTFIX-RACE NOTE]
                // A kolony/orbital/planetary postfix message from the source
                // owner can arrive on a network thread between our step-3 stamp
                // and the router's TryRoute entering its own per-agency lock.
                // That postfix's router will block on AgencySystem.GetAgencyLock
                // (held by us via RunUnderLockOrder). When the postfix unblocks
                // post-command, the router reads vessel.OwningAgencyId = destAgencyId,
                // sees the sender's agency (source's owner = aliceAgency), and
                // rejects with a Warning log line. The source owner's
                // in-flight research contribution silently vanishes from her
                // perspective — there is no client-visible diagnostic. Race
                // window is bounded by the dual-lock critical section duration.
                // Fixing this race structurally requires holding source's lock
                // across the entire command for an arbitrarily long window
                // (operator-driven), which trades a small race for a much
                // larger denial-of-service surface. Documented + accepted;
                // operators should /kick the source owner BEFORE
                // setvesselagency to fully close it.
                if (source != null)
                {
                    kolonyResult = AgencyKolonyRouter.MigrateForVesselTransfer(source, destination, movedVesselId);
                    orbitalResult = AgencyOrbitalRouter.MigrateForVesselTransfer(source, destination, movedVesselId);
                    planetaryResult = AgencyPlanetaryRouter.InspectAffectedEntriesForVesselTransfer(source, movedVesselId);
                }

                // Step 5: Persist BOTH agencies under the dual lock.
                //
                // [Round-1 integration-logic SHOULD FIX scenario 6 — CRASH-WINDOW]
                // Save DESTINATION FIRST (additive), then source (subtractive).
                // A crash between the two leaves the entry in BOTH agencies —
                // recoverable on restart by an operator re-running the command
                // (the same-stamp short-circuit fires for the destination's
                // side; the source-side migration removes the stale duplicate).
                // The opposite order (source first) would leave the entry in
                // NEITHER agency on a mid-pair crash — permanently lost.
                AgencySystem.SaveAgency(destAgencyId);
                if (source != null)
                    AgencySystem.SaveAgency(source.AgencyId);
            });

            if (!mutated)
            {
                // Concurrent-operator short-circuit fired at step 2b. Log at
                // Debug so monitoring scripts don't see noise from idempotent
                // re-runs; the step-1 path's Normal-level log already carries
                // the operator-visible no-op signal.
                LunaLog.Debug(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} no-op result=raced " +
                    $"(another command landed first; stamp already {destAgencyId:N})");
                return true;
            }

            // [Round-1 general-lens MUST FIX #2 — STEP 6/7 REORDER]
            // Step 7 BEFORE step 6: release stale locks FIRST, then RunBackup.
            // Lock release does not require disk flush; if a periodic backup
            // is mid-flight on the backup task thread, gating release-locks
            // behind RunBackup would extend the "source owner sees frozen
            // vessel" UX window by the duration of one backup pass. Lock
            // release closes the post-mutation soak-Finding-2 message-drop
            // window the moment the dual lock unlocks.
            //
            // [Round-1 integration-logic SHOULD FIX scenario 10 — STALE-RENAME]
            // ReleaseStaleVesselLocks now uniformly filters by holder-agency
            // mapping (not by source.OwningPlayerName) so a vessel that
            // accumulated locks before a prior /transferagency rename gets
            // those stale-handle locks released too. The Unassigned-source
            // branch's "ANY non-dest-agency holder" logic was always more
            // correct; we generalise it.
            var releasedCount = ReleaseStaleVesselLocks(movedVesselId, destAgencyId);

            // Step 6: BackupSystem.RunBackup AFTER lock release. Flushes the
            // mutated vessel.OwningAgencyId to disk. Wrapped in try/catch
            // mirroring DeleteAgencyCommand.cs:178-191 — a failed backup leaves
            // the in-memory state correct but disk vessels potentially stale;
            // log loudly and let the operator decide whether to /backup manually.
            try
            {
                BackupSystem.RunBackup();
            }
            catch (Exception e)
            {
                LunaLog.Error(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} BackupSystem.RunBackup failed after " +
                    $"reassign; in-memory state correct but disk vessel may still carry the old agency stamp " +
                    $"until next periodic flush. Manual /backup recommended. Exception: {e.Message}");
            }

            // Step 8: Wire emit ORDER — Visibility first, then source-removal
            // echoes, then destination-add echoes. Same channel (22), ReliableOrdered.
            //
            // (a) Visibility broadcast — tells every client (including source
            //     owner) that V belongs to destAgencyId now. Source owner's
            //     5.18b mirror updates BEFORE the removal echoes prune their
            //     per-router cache, so they don't briefly render "I own V but
            //     V's entries are gone."
            AgencySystemSender.BroadcastVisibilityChange(new List<VesselOwnershipChange>
            {
                new VesselOwnershipChange { VesselId = movedVesselId, NewOwningAgencyId = destAgencyId }
            });

            // (b) Source-owner removal echoes (kolony + orbital). Skipped when
            // source is null or the helper returned an empty result (vessel
            // had no per-router entries in source's partition). Resolve owner
            // client by name; offline source owners get nothing here and pick
            // up the missing entries via the standard handshake catchup path
            // on next connect (catchup ships state.KolonyEntries / OrbitalTransfers
            // post-migration, so the moved entries naturally absent).
            var sourceOwnerClient = string.IsNullOrEmpty(sourceOwnerName)
                ? null
                : ClientRetriever.GetClientByName(sourceOwnerName);
            if (sourceOwnerClient != null && source != null)
            {
                if (kolonyResult != null && kolonyResult.RemovedKeys.Count > 0)
                {
                    AgencySystemSender.SendKolonyStateToOwner(
                        sourceOwnerClient, source.AgencyId, entries: null, removedKeys: kolonyResult.RemovedKeys);
                }
                if (orbitalResult != null && orbitalResult.RemovedTransferGuids.Count > 0)
                {
                    AgencySystemSender.SendOrbitalStateToOwner(
                        sourceOwnerClient, source.AgencyId, entries: null, removedTransferGuids: orbitalResult.RemovedTransferGuids);
                }
            }

            // (c) Destination-owner add echoes (kolony + orbital). Resolve dest
            // owner client by name; offline dest owners get nothing here and
            // pick up the added entries via their next handshake catchup.
            var destOwnerClient = string.IsNullOrEmpty(destOwnerName)
                ? null
                : ClientRetriever.GetClientByName(destOwnerName);
            if (destOwnerClient != null)
            {
                if (kolonyResult != null && kolonyResult.AddedEntries.Count > 0)
                {
                    AgencySystemSender.SendKolonyStateToOwner(destOwnerClient, destAgencyId, kolonyResult.AddedEntries);
                }
                if (orbitalResult != null && orbitalResult.AddedEntries.Count > 0)
                {
                    AgencySystemSender.SendOrbitalStateToOwner(destOwnerClient, destAgencyId, orbitalResult.AddedEntries);
                }
            }

            // [Round-1 integration-logic MUST FIX scenario 7 — THIRD-AGENCY CROSS-REF]
            // Orbital transfers form a graph: agency C may hold a transfer
            // where OriginVesselId or DestinationVesselId points at our
            // movedVessel. The Migrate*ForVesselTransfer helpers ONLY scan
            // source. C's stranded reference stays untouched in C — Bob
            // will receive a delivery whose authoritative side is C, and
            // C's projector will continue to render an in-flight transfer
            // whose Destination now belongs to Bob's agency. The 5.17a
            // cross-agency guard on Deliver-prefix consumers will reject
            // the actual handover. Operator needs to know.
            var thirdAgencyOrphans = InspectThirdAgencyCrossReferences(
                movedVesselId, sourceAgencyId, destAgencyId);

            // Operator-visible summary log. Grammar mirrors /deleteagency +
            // /transferagency for the GUI launcher's (verb, id, key=value, count)
            // regex. The result=transferred key (Round-1 consumer-lens MUST FIX)
            // lets a GUI parser distinguish "real mutation happened" from the
            // same-stamp short-circuit log line which carries result=noop.
            //
            // GUI launcher post-command refresh: stdout summary line is the
            // signal for "command landed"; the GUI should then re-invoke
            // /listagencies or poll the /fork web endpoint for fresh per-agency
            // state. AgencyVisibilityMsgData + per-router echoes flow to
            // connected clients via Lidgren; the GUI itself is not a peer.
            var sourceLabel = sourceIsUnassigned
                ? "Unassigned"
                : (source != null ? $"{source.AgencyId:N}" : $"orphan:{sourceAgencyId:N}");
            var kolonyMoved = kolonyResult?.RemovedKeys.Count ?? 0;
            var orbitalMoved = orbitalResult?.RemovedTransferGuids.Count ?? 0;
            var orbitalKept = orbitalResult?.OriginOnlyKeptGuids.Count ?? 0;
            var planetaryAffected = planetaryResult?.AffectedKeys.Count ?? 0;
            LunaLog.Normal(
                $"[fix:per-agency-career] setvesselagency {movedVesselId:N} result=transferred source={sourceLabel} " +
                $"dest={destAgencyId:N} dest-owner='{destOwnerName}' kolony-moved={kolonyMoved} " +
                $"orbital-moved={orbitalMoved} orbital-origin-kept={orbitalKept} " +
                $"planetary-retained-in-source={planetaryAffected} third-agency-stranded={thirdAgencyOrphans} " +
                $"released-locks={releasedCount}");

            // Per-guid audit lines for the orbital Origin-only KEEP set
            // (pre-spec §4.e operator info log — Q1 KEEP retains the launch
            // obligation in source agency; operator wants the per-guid
            // detail for grep). Kept on [fix:MKS-R2] since these are
            // MKS-specific migration-audit lines, not operator-command-grain
            // lines a GUI launcher's per-agency-career filter would route.
            if (orbitalResult != null)
            {
                foreach (var keptGuid in orbitalResult.OriginOnlyKeptGuids)
                {
                    LunaLog.Normal(
                        $"[fix:MKS-R2] setvesselagency {movedVesselId:N} kept origin-transfer={keptGuid:N} in source agency");
                }
            }

            // Per-key audit lines for the planetary affected-keys set (Q2
            // NO-MIGRATE — entries stay in source as historical body-pool
            // contributions). One line per body+resource key.
            if (planetaryResult != null)
            {
                foreach (var key in planetaryResult.AffectedKeys)
                {
                    // Key shape: $"{bodyIndex}|{resourceName}".
                    LunaLog.Normal(
                        $"[fix:MKS-R2] setvesselagency {movedVesselId:N} planetary-retained-in-source key={key}");
                }
            }

            return true;
        }

        /// <summary>
        /// Runs <paramref name="action"/> under the correct lock-acquire order
        /// per pre-spec §4.e dual-lock contract. Source and destination agency
        /// locks are acquired in <see cref="Guid.CompareTo"/> order
        /// (lower-comparing AgencyId first). When <paramref name="source"/> is
        /// null (Unassigned-vessel reassignment), only the destination lock is
        /// acquired — there's no source AgencyState to read.
        /// </summary>
        private static void RunUnderLockOrder(AgencyState source, AgencyState destination, Action action)
        {
            if (source == null)
            {
                lock (AgencySystem.GetAgencyLock(destination.AgencyId))
                {
                    action();
                }
                return;
            }

            // Guid.CompareTo (byte-form). String-form ordering would disagree
            // with byte-form ordering for some Guids; .NET Guid.CompareTo is
            // the authoritative comparison.
            if (source.AgencyId.CompareTo(destination.AgencyId) < 0)
            {
                lock (AgencySystem.GetAgencyLock(source.AgencyId))
                {
                    lock (AgencySystem.GetAgencyLock(destination.AgencyId))
                    {
                        action();
                    }
                }
            }
            else
            {
                lock (AgencySystem.GetAgencyLock(destination.AgencyId))
                {
                    lock (AgencySystem.GetAgencyLock(source.AgencyId))
                    {
                        action();
                    }
                }
            }
        }

        /// <summary>
        /// Step 7 of the 9-step contract. Releases vessel-scoped locks
        /// (Control / Update / UnloadedUpdate) on the moved vessel that would
        /// otherwise produce a "frozen vessel" UX for the prior holder under
        /// the post-mutation 5.17a + soak-Finding-2 write-path guards.
        ///
        /// <para><b>Unified filter (Round-1 integration-logic SHOULD FIX
        /// scenario 10):</b> walks every authenticated player's locks and
        /// releases vessel-scoped locks on the moved vessel held by any
        /// player whose <see cref="AgencySystem.AgencyByPlayerName"/>
        /// mapping does NOT resolve to <paramref name="destAgencyId"/>. This
        /// is structurally more correct than a "filter by source.OwningPlayerName"
        /// branch because:
        /// <list type="bullet">
        ///   <item><b>Typical A→B:</b> only source's player has the lock; the
        ///         mapping check matches them and releases.</item>
        ///   <item><b>Spec §10 Q3 Unassigned bypass:</b> any agency may have
        ///         held a lock on a sentinel vessel; all non-dest holders get
        ///         released.</item>
        ///   <item><b>Orphan source:</b> the prior holder either has been
        ///         healed via <c>AgencySystem.cs:1335</c> boot path (mapping
        ///         removed) or still holds the stale orphan mapping; both
        ///         fall through the dest-mapping skip and get released.</item>
        ///   <item><b>Stale-rename pre-acquire (the scenario-10 hazard):</b>
        ///         Alice acquired a lock under her old player handle BEFORE
        ///         a <c>/transferagency</c> rename moved her agency to
        ///         Charlie. The <see cref="LockDefinition.PlayerName"/> on
        ///         the lock is still "alice". A filter-by-source.OwningPlayerName
        ///         using "charlie" would miss it. This unified walk reads
        ///         each lock's actual PlayerName and routes through
        ///         AgencyByPlayerName — Alice's stale handle maps to the
        ///         (now-different) agency that owned her at the rename, but
        ///         that mapping persists post-rename as "alice→aliceAgency"
        ///         (transferagency renames OwningPlayerName but does NOT
        ///         remove the prior name from AgencyByPlayerName until the
        ///         prior owner reconnects fresh). The lock release fires
        ///         correctly. Net effect: stale-rename pre-acquire locks
        ///         are released along with the rest.</item>
        /// </list></para>
        ///
        /// <para><b>Complexity</b> O(authenticated_clients × locks_per_client).
        /// For a 50-player server with ~100 locks each = 5000 iterations per
        /// command. Acceptable for an admin operation that fires at operator
        /// cadence.</para>
        /// </summary>
        private static int ReleaseStaleVesselLocks(Guid movedVesselId, Guid destAgencyId)
        {
            // Snapshot locks to release first — the LockStore mutates during
            // ReleaseAndSendLockReleaseMessage and we don't enumerate while
            // modifying.
            var toRelease = new List<LockDefinition>();
            var clientCache = new Dictionary<string, ClientStructure>(StringComparer.Ordinal);

            foreach (var client in ClientRetriever.GetAuthenticatedClients())
            {
                if (client == null || string.IsNullOrEmpty(client.PlayerName)) continue;

                // Skip the destination agency's player(s) — they're the new
                // owner and shouldn't be interrupted. The mapping check tolerates
                // missing entries (returns false → not destAgency → release).
                if (AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var holderAgency)
                    && holderAgency == destAgencyId)
                {
                    continue;
                }

                var locksHeld = LockSystem.LockQuery.GetAllPlayerLocks(client.PlayerName);
                foreach (var lockDef in locksHeld)
                {
                    if (!VesselScopedLockTypes.Contains(lockDef.Type)) continue;
                    if (lockDef.VesselId != movedVesselId) continue;
                    toRelease.Add(lockDef);
                    clientCache[client.PlayerName] = client;
                }
            }

            if (toRelease.Count == 0) return 0;

            foreach (var lockDef in toRelease)
            {
                // Round-1 consumer-lens SHOULD FIX — added vessel={...:N} key
                // so a (verb, vessel-id) regex across per-agency admin command
                // log lines matches this released-lock audit consistently with
                // /deleteagency + /transferagency.
                LunaLog.Normal(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} released-lock " +
                    $"vessel={lockDef.VesselId:N} player='{lockDef.PlayerName}' type={lockDef.Type}");

                clientCache.TryGetValue(lockDef.PlayerName, out var holderClient);
                LockSystemSender.ReleaseAndSendLockReleaseMessage(holderClient, lockDef);
            }
            return toRelease.Count;
        }

        /// <summary>
        /// [Round-1 integration-logic MUST FIX scenario 7 — THIRD-AGENCY
        /// CROSS-REFERENCE INSPECTION] Scans every agency OTHER than source
        /// + destination for orbital transfers whose
        /// <see cref="AgencyOrbitalTransferEntry.DestinationVesselId"/> or
        /// <see cref="AgencyOrbitalTransferEntry.OriginVesselId"/> points at
        /// the moved vessel. Emits one Warning per stranded reference so
        /// the operator can investigate (and ideally /kick the third agency's
        /// player + manually re-route the transfer via Universe-edit; no
        /// admin command exists today to do this in-band).
        ///
        /// <para>Returns the count of stranded entries for the operator
        /// summary line. Zero is the typical case; non-zero is the soak-
        /// pattern that pre-spec §4.e didn't anticipate — a graph topology
        /// where transfers from C to V get orphaned when V transfers to
        /// Bob, leaving C's projector continuing to render an in-flight
        /// transfer the destination side will cross-agency-reject on
        /// Deliver.</para>
        ///
        /// <para>Kolony + planetary partitions are NOT scanned because
        /// neither carries a cross-vessel reference: kolony entries are
        /// vessel-keyed (the moved vessel's entries are already migrated by
        /// the kolony helper); planetary entries are body-keyed and the
        /// OwningVesselId field is incidental record-keeping (the entries
        /// are NOT migrated by the planetary helper per Q2 NO-MIGRATE).
        /// Only orbital is the graph-shaped partition.</para>
        /// </summary>
        private static int InspectThirdAgencyCrossReferences(
            Guid movedVesselId, Guid sourceAgencyId, Guid destAgencyId)
        {
            var stranded = 0;
            foreach (var kvp in AgencySystem.Agencies)
            {
                var agencyId = kvp.Key;
                var agencyState = kvp.Value;
                if (agencyId == sourceAgencyId || agencyId == destAgencyId) continue;
                if (agencyState == null) continue;

                // Read under the agency lock — concurrent router upsert
                // mutates OrbitalTransfers entries; iteration without the
                // lock could trip InvalidOperationException.
                lock (AgencySystem.GetAgencyLock(agencyId))
                {
                    foreach (var t in agencyState.OrbitalTransfers)
                    {
                        var entry = t.Value;
                        if (entry == null) continue;
                        var matchesDest = entry.DestinationVesselId == movedVesselId;
                        var matchesOrigin = entry.OriginVesselId == movedVesselId;
                        if (!matchesDest && !matchesOrigin) continue;

                        var refType = matchesDest && matchesOrigin
                            ? "both-Origin-and-Destination"
                            : (matchesDest ? "Destination" : "Origin");
                        LunaLog.Warning(
                            $"[fix:MKS-R2] setvesselagency {movedVesselId:N} third-agency-stranded-transfer " +
                            $"agency={agencyId:N} transfer={entry.TransferGuid:N} role={refType}. " +
                            "Operator must manually reroute the transfer or /kick the third agency's owner " +
                            "(no admin command exists today to in-band edit a third agency's orbital partition).");
                        stranded++;
                    }
                }
            }
            return stranded;
        }
    }
}
