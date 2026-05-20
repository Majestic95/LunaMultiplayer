using LmpCommon.Locks;
using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Command.Command.Base;
using Server.Log;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Command.Command
{
    /// <summary>
    /// Stage 5.18d slice (g). Admin command that DELETES an entire
    /// <see cref="AgencyState"/>. Removes the in-memory registry entry, the
    /// <see cref="AgencySystem.AgencyByPlayerName"/> mapping, and the
    /// on-disk <c>Universe/Agencies/{guid}.txt</c> + <c>.bak</c>. Vessels
    /// previously stamped with this agency's id are demoted to the Unassigned
    /// sentinel (<see cref="Guid.Empty"/>, spec §10 Q3) and the change is
    /// broadcast via <see cref="AgencyVisibilityMsgData"/> so peer clients
    /// update their 5.18b/c registries + UI labels.
    ///
    /// <para><b>Destructive — irreversible.</b> The AgencyState file carries
    /// per-agency contracts, tech tree, science subjects, purchased parts,
    /// experimental parts, strategies, achievements, facility levels, and the
    /// scalar career trio (funds / science / reputation). All of those are
    /// LOST when the file is deleted. There is no undo. The
    /// <c>--confirm</c> flag (see <see cref="DeleteAgencyCommandParser"/>) is
    /// the required destructive opt-in; without it the command refuses and
    /// prints the usage banner.</para>
    ///
    /// <para><b>Prior owner reconnects to a fresh agency.</b> On their next
    /// connect, <see cref="AgencySystem.RegisterAgency"/> sees no mapping in
    /// <see cref="AgencySystem.AgencyByPlayerName"/> and mints a fresh
    /// <see cref="AgencyState"/> seeded from
    /// <see cref="GameplaySettingsDefinition.StartingFunds"/> /
    /// <see cref="GameplaySettingsDefinition.StartingScience"/> /
    /// <see cref="GameplaySettingsDefinition.StartingReputation"/>. The
    /// demoted vessels stay Unassigned until an operator runs (a future)
    /// <c>/setagency</c>-with-vessel-reassign command or operates on them
    /// individually — there is no mechanism today to re-bind Unassigned
    /// vessels to a fresh agency in bulk.</para>
    ///
    /// <para><b>Connected-prior-owner handling.</b> Same shape as slice (e)
    /// <see cref="TransferAgencyCommand"/>: their <c>LocalAgencyId</c> stays
    /// bound to the now-deleted agency until reconnect; server-side they're
    /// agency-less and gain the 5.17a "requester has no agency mapping"
    /// bypass quirk. The command logs a Warning when the prior owner is
    /// online; operators should <c>/kick</c> for a clean cutover.</para>
    /// </summary>
    public class DeleteAgencyCommand : SimpleCommand
    {
        private static readonly HashSet<LockType> VesselScopedLockTypes = new HashSet<LockType>
        {
            LockType.Control,
            LockType.Update,
            LockType.UnloadedUpdate,
        };

        public override bool Execute(string commandArgs)
        {
            if (!DeleteAgencyCommandParser.TryParse(commandArgs, out var sourceToken, out var confirmed, out var parseError))
            {
                LunaLog.Error(parseError);
                LunaLog.Normal(DeleteAgencyCommandParser.UsageBanner);
                return false;
            }

            // Gate refusal (same shape as /setagency + /transferagency).
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
            {
                LunaLog.Error(
                    "deleteagency: requires PerAgencyCareer=true. Under PerAgencyCareer=false there are no " +
                    "per-agency career states to delete.");
                return false;
            }
            if (!AgencySystem.PerAgencyEnabled)
            {
                LunaLog.Error(
                    "deleteagency: requires GameMode=Career. PerAgencyCareer=true but GameMode is not Career — set " +
                    "GameMode=Career in Settings/GeneralSettings.xml to activate, or set PerAgencyCareer=false in " +
                    "Settings/GameplaySettings.xml to disable per-agency cleanly (may flip GameDifficulty to Custom " +
                    "— see CLAUDE.md Settings caveat).");
                return false;
            }

            // Source resolve.
            if (!AgencySystem.TryResolveAgencyToken(sourceToken, out var source))
            {
                if (AgencySystem.Agencies.IsEmpty)
                {
                    LunaLog.Error(
                        "deleteagency: no agencies are registered. Nothing to delete.");
                }
                else
                {
                    LunaLog.Error(
                        $"deleteagency: agency token '{sourceToken}' does not match any registered agency. " +
                        "Pass either an agency id (run /listagencies) or the agency owner's REGISTRATION-time " +
                        "LMP handle. Orphaned agency ids from boot warnings have no in-memory AgencyState; the " +
                        "operator workflow is to delete Universe/Agencies/{guid}.txt(.bak) directly while the " +
                        "server is stopped.");
                }
                return false;
            }

            // --confirm gate. The command is destructive; require explicit
            // opt-in. Without --confirm, print the usage banner so the operator
            // sees the destructive-action language + the correct invocation.
            if (!confirmed)
            {
                LunaLog.Error(
                    $"deleteagency: refusing without --confirm. This is destructive (per-agency career state for " +
                    $"agency {source.AgencyId:N} owner='{source.OwningPlayerName}' display='{source.DisplayName}' " +
                    "will be PERMANENTLY removed). Re-run with --confirm to proceed.");
                LunaLog.Normal(DeleteAgencyCommandParser.UsageBanner);
                return false;
            }

            // Snapshot identity fields BEFORE mutation — TryDeleteAgency wipes
            // the AgencyState's registry binding so post-mutation reads might
            // surface stale or empty values.
            var agencyId = source.AgencyId;
            var oldOwnerName = source.OwningPlayerName ?? string.Empty;
            var displayName = source.DisplayName ?? string.Empty;

            // Snapshot the prior owner's vessel-scoped locks on this agency's
            // vessels BEFORE the demote — once vessels are demoted to Empty
            // the OwningAgencyId == agencyId filter no longer applies.
            // TryDeleteAgency returns the demoted vessel id set; we use that
            // here for the lock-release filter so the in-memory mutation is
            // strictly the helper's responsibility.
            var oldOwnerClient = string.IsNullOrEmpty(oldOwnerName) ? null : ClientRetriever.GetClientByName(oldOwnerName);

            // [Phase 4 Slice F — WOLF cascade] Restore in-flight CrewRoute
            // passengers (RosterStatus.Missing + respawn-MaxValue per WOLF's
            // Launch path) back to the AC pool BEFORE TryDeleteAgency removes
            // the agency record. Without this, kerbals stuck mid-flight when
            // their agency is wholesale-deleted have no in-band recovery path
            // — the route's CrewRoute record vanishes with the agency, and
            // the Disembark client-side code that would normally flip them
            // back to Available can never run. The cascade is per-kerbal-
            // isolated (a missing or malformed kerbal file doesn't abort
            // siblings) and emits its own [fix:WOLF-R4] log lines per
            // restored / failed kerbal. Outer try/catch ensures any cascade
            // exception still lets TryDeleteAgency proceed — otherwise we'd
            // leak an orphan agency file with no in-memory registry record.
            //
            // Multi-lens-review SHOULD FIX (upgrade/integration): initialise
            // to a non-null empty CascadeResult so the summary emission below
            // ALWAYS runs (even on cascade-throw). Without this the
            // `wolfCascade != null` guard would skip the audit summary on the
            // exact path where the operator most needs the "cascade ran?"
            // signal. Failure of the cascade itself adds CascadeFailed=true
            // to the summary tail (see emit block further down) and emits
            // the Error line.
            //
            // The cascade runs OUTSIDE the agency lock (FileHandler's
            // per-path lock handles kerbal-file concurrency); see
            // AgencyWolfMigration class XML for the lock-domain contract +
            // the narrow operator-/kick-first race window.
            var wolfCascade = new AgencyWolfMigration.CascadeResult();
            var wolfCascadeFailed = false;
            try
            {
                wolfCascade = AgencyWolfMigration.CascadeOnDelete(source);
            }
            catch (Exception e)
            {
                wolfCascadeFailed = true;
                LunaLog.Error(
                    $"[fix:WOLF-R4] deleteagency {agencyId:N} WOLF cascade failed: {e.GetType().Name}: " +
                    $"{e.Message}. Proceeding with agency-record deletion; mid-flight kerbals may " +
                    "require manual recovery (edit Universe/Kerbals/{name}.txt — set " +
                    "'state = Available' + 'ToD = 0').");
            }

            if (!AgencySystem.TryDeleteAgency(source, out var demotedVesselIds, out var failureReason))
            {
                LunaLog.Error($"deleteagency: {failureReason}");
                return false;
            }

            // Broadcast AgencyVisibilityMsgData with the demote entries. The
            // server-side canonical store is already updated; the broadcast
            // tells peer clients to mirror the change in their 5.18b vessel-
            // ownership registries via ForceRecordOwnership (which bypasses
            // the 5.18b preservation rule for exactly this authoritative
            // demote-to-Empty case).
            if (demotedVesselIds.Count > 0)
            {
                var changes = demotedVesselIds
                    .Select(vid => new VesselOwnershipChange { VesselId = vid, NewOwningAgencyId = Guid.Empty })
                    .ToList();
                AgencySystemSender.BroadcastVisibilityChange(changes);
            }
            else
            {
                // Empty-universe audit (consumer-lens v1 S1). A GUI launcher
                // correlating "broadcast emitted" with "vessels affected"
                // needs an explicit "no broadcast" signal; the success line's
                // demoted=0 token carries the count but doesn't audit the
                // absent broadcast.
                LunaLog.Normal(
                    $"[fix:per-agency-career] deleteagency {agencyId:N} no vessels stamped — visibility broadcast skipped");
            }

            // Narrow the disk-flush window (server-systems-review v1 SS-3 +
            // upgrade-lens v1 UL-S1). Without this, vessel.OwningAgencyId
            // mutations live only in memory until the next periodic
            // BackupSystem.RunBackup (~30s default). A crash in that window
            // re-loads vessels from disk with the deleted agency's id stamp,
            // and the AgencyState file is gone — vessels resurface as orphans
            // via WarnAboutOrphanedVessels with the agency's .bak also
            // deleted, the operator has no clean recovery. Calling RunBackup
            // here costs one disk flush per /deleteagency and closes the
            // operator-initiated crash window. Same pattern as
            // CleanContractsCommand's explicit flush.
            if (demotedVesselIds.Count > 0)
            {
                try
                {
                    BackupSystem.RunBackup();
                }
                catch (Exception e)
                {
                    LunaLog.Error(
                        $"[fix:per-agency-career] deleteagency {agencyId:N} BackupSystem.RunBackup failed " +
                        $"after demote; in-memory state correct but disk vessels may still carry the deleted " +
                        $"agency stamp until next periodic flush. Manual /backup recommended. Exception: {e.Message}");
                }
            }

            // Release the prior owner's vessel-scoped locks on the demoted
            // vessels. Filter by lock.VesselId in demotedVesselIds (post-
            // mutation the vessels are Unassigned; the transferagency
            // OwningAgencyId-match filter doesn't apply here).
            var releasedCount = ReleaseOldOwnerLocksOnDemotedVessels(oldOwnerName, demotedVesselIds, oldOwnerClient, agencyId);

            // Operator-visible log. Grammar matches slice (e) /transferagency
            // (verb, id, key=value pairs, count). The DisplayName is included
            // because the agency record itself is gone — operators reading
            // the log later have no other way to recover what they deleted.
            LunaLog.Normal(
                $"[fix:per-agency-career] deleteagency {agencyId:N} owner='{oldOwnerName}' display='{displayName}' " +
                $"demoted={demotedVesselIds.Count} released={releasedCount}");

            // Per-demoted-vessel audit log. Same shape as transferagency's
            // per-lock lines (consumer-lens v1 CL-3 carried over).
            foreach (var vesselId in demotedVesselIds)
            {
                LunaLog.Normal(
                    $"[fix:per-agency-career] deleteagency {agencyId:N} demoted vessel={vesselId:N} to Unassigned");
            }

            // [Phase 4 Slice F] WOLF-cascade summary. Per-restored / per-
            // failed kerbal log lines are emitted inside AgencyWolfMigration
            // (each tagged with the agencyId so a GUI launcher's
            // `[fix:WOLF-R4] deleteagency {agencyId:N}` grep gathers them
            // alongside the summary line). This line is the deterministic
            // "cascade ran?" anchor — always emitted, including under the
            // cascade-throw path (wolfCascade was initialised to an empty
            // CascadeResult above; cascade-failed=true distinguishes the
            // exception path from a happy zero-work cascade). The
            // failed-kerbals count carries the failure signal; the per-name
            // detail lives in the per-kerbal Warning lines (with manual-
            // recovery path inlined) so we don't re-emit it as an aggregate
            // here — operators grep by agencyId.
            LunaLog.Normal(
                $"[fix:WOLF-R4] deleteagency {agencyId:N} wolf-cascade in-flight-routes={wolfCascade.InFlightRoutesScanned} " +
                $"restored-kerbals={wolfCascade.RestoredKerbalCount} failed-kerbals={wolfCascade.FailedKerbalNames.Count} " +
                $"cascade-failed={wolfCascadeFailed}");

            // Connected-prior-owner Warning + /kick recommendation. Mirrors
            // slice (e) /transferagency's connected-old-owner Warning + adds the
            // post-delete Share* leak window the upgrade-lens v1 UL-S2 flagged.
            // Under gate=on after the delete, the prior owner's Share*Funds/
            // Science/Reputation mutations look up AgencyByPlayerName[oldOwner]
            // → miss → the router returns false → the legacy shared-scenario
            // relay broadcasts the mutation to every connected client. That's
            // a cross-agency leak. The /kick recommendation is the
            // operational mitigation; a real fix lives in
            // AgencyCurrencyRouter's fail-closed-under-gate-on branch (a
            // follow-up slice in the band-1 router family).
            if (oldOwnerClient != null)
            {
                LunaLog.Warning(
                    $"[fix:per-agency-career] deleteagency {agencyId:N} WARNING: prior owner '{oldOwnerName}' is " +
                    "currently online. (1) Their client retains stale LocalAgencyId pointing at the now-deleted " +
                    "agency. (2) Their Share*Funds/Science/Reputation mutations now fall through the per-agency " +
                    "router (no AgencyState to route to) to the LEGACY SHARED-SCENARIO relay — broadcasting to " +
                    "every other client under gate=on. This is a temporary cross-agency leak window. /kick " +
                    oldOwnerName + " IMMEDIATELY to close it; on reconnect their handshake mints a fresh agency " +
                    "(seeded from GameplaySettings.Starting*) and the router resumes routing per-agency.");
            }

            return true;
        }

        private static int ReleaseOldOwnerLocksOnDemotedVessels(
            string oldOwnerName,
            List<Guid> demotedVesselIds,
            ClientStructure oldOwnerClient,
            Guid sourceAgencyIdForLog)
        {
            if (string.IsNullOrEmpty(oldOwnerName) || demotedVesselIds == null || demotedVesselIds.Count == 0)
                return 0;

            var demotedSet = new HashSet<Guid>(demotedVesselIds);
            var locksHeld = LockSystem.LockQuery.GetAllPlayerLocks(oldOwnerName);
            var toRelease = locksHeld
                .Where(l => VesselScopedLockTypes.Contains(l.Type))
                .Where(l => demotedSet.Contains(l.VesselId))
                .ToList();

            foreach (var lockDef in toRelease)
            {
                // Consumer-lens v1 M1: include the agency id token so this
                // line's grammar matches slice (e) /transferagency's released-
                // lock line. A GUI launcher's `(verb, agencyId)` regex routes
                // this line back to the (now-deleted) agency in its audit
                // panel.
                LunaLog.Normal(
                    $"[fix:per-agency-career] deleteagency {sourceAgencyIdForLog:N} released-lock vessel={lockDef.VesselId:N} type={lockDef.Type}");
                LockSystemSender.ReleaseAndSendLockReleaseMessage(oldOwnerClient, lockDef);
            }
            return toRelease.Count;
        }
    }
}
