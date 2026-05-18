using LmpCommon.Locks;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Command.Command.Base;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Command.Command
{
    /// <summary>
    /// Stage 5.18d slice (e). Admin command that renames the owner of an existing
    /// per-agency career to a different LMP player handle. The agency's
    /// <see cref="System.Agency.AgencyState.AgencyId"/> is preserved; vessels keep
    /// their <c>lmpOwningAgency</c> stamp; only the player handle attached to the
    /// agency changes. The vessel-level X→Y reassignment shape documented in
    /// <see cref="LmpCommon.Message.Data.Agency.AgencyVisibilityMsgData"/> is
    /// implemented by the Stage 5.18d slice (g) <c>/deleteagency</c> cascade, not
    /// here (sign-off 2026-05-18).
    ///
    /// <para><b>Usage:</b> see <see cref="TransferAgencyCommandParser.UsageBanner"/>.</para>
    ///
    /// <para><b>Mutation flow:</b>
    /// <list type="number">
    ///   <item>Parse + gate checks + name validation (length-cap matches
    ///         <see cref="GeneralSettingsDefinition.MaxUsernameLength"/>).</item>
    ///   <item>Resolve source token via <see cref="AgencySystem.TryResolveAgencyToken"/>.</item>
    ///   <item>Same-name short-circuit emits a no-op log line and returns — avoids
    ///         churning lock-release / echo for a re-issued idempotent command.</item>
    ///   <item>Atomic rename via <see cref="AgencySystem.TryRenameAgencyOwner"/> —
    ///         that helper handles the dual name-lock + agency-lock acquire order
    ///         + collision re-check + persistence-before-index discipline.</item>
    ///   <item>Release the old owner's vessel-scoped locks (Control / Update /
    ///         UnloadedUpdate) for vessels stamped with this agency's id. The
    ///         old owner is no longer the agency's owner and shouldn't hold
    ///         in-flight authority over these vessels. Each released lock emits
    ///         a per-lock log line for audit.</item>
    ///   <item>Re-anchor the new owner's client identity by sending an
    ///         <see cref="AgencyHandshakeMsgData"/> ahead of the
    ///         <see cref="AgencyStateMsgData"/> echo. Without the Handshake the
    ///         5.18a <c>HandleState</c> defensive filter (<see cref="LmpClient.Systems.Agency.AgencyMembership.IsForLocalAgency"/>)
    ///         would drop the State message when the new owner's
    ///         <c>LocalAgencyId</c> doesn't yet match this agency's id (Empty
    ///         when they were agency-less; a different id when they already had
    ///         an agency that was concurrently transferred out from under them).
    ///         The Handshake first sets <c>LocalAgencyId</c>; the State then
    ///         passes the filter and populates display/owner/scalars.</item>
    ///   <item>Operator-visible stdout log: one summary line + per-lock audit
    ///         lines + DisplayName hint + renamed-prior-owner-mint hint +
    ///         (when applicable) Warning when the old owner is still online.</item>
    /// </list></para>
    ///
    /// <para><b>Unconnected new owner is supported.</b> The new player handle does
    /// NOT need to be currently connected or even previously registered. The
    /// rename writes <see cref="AgencyByPlayerName"/>[newOwnerName] = agencyId;
    /// on first connect under that handle, <see cref="AgencySystem.RegisterAgency"/>
    /// hits the existing-mapping branch and returns the renamed agency. Operator
    /// workflow "pre-promote Bob before Bob joins" works cleanly.</para>
    ///
    /// <para><b>Vessel ownership is NOT broadcast.</b> No
    /// <see cref="AgencyVisibilityMsgData"/> push is emitted because vessel
    /// <c>OwningAgencyId</c> values don't change in this command — the agency's
    /// id stays the same. Peers' <see cref="LmpClient.Systems.Agency.AgencySystem.VesselOwnership"/>
    /// registries stay correct without intervention. Peer-side
    /// <see cref="LmpClient.Systems.Agency.AgencySystem.OtherAgencies"/> caches
    /// (which carry <c>OwningPlayerName</c>) DO go stale until peer reconnect;
    /// this is a known limitation. A future polish slice may add an
    /// <c>AgencyInfoUpdateMsgData</c> peer-broadcast if soak feedback demands it.</para>
    ///
    /// <para><b>Connected-old-owner handling.</b> If the old owner is currently
    /// connected, their client's <c>LocalAgencyId</c> stays bound to the now-
    /// transferred agency until they reconnect; their local UI still shows
    /// "their" agency until rejoin. Server-side cross-agency guards (Stage 5.17a)
    /// treat them as agency-less under the post-transfer
    /// <see cref="AgencyByPlayerName"/> state, so their bypass case
    /// ("requester has no agency mapping") permits some operations that the
    /// guard normally rejects. This is a known 5.17a design quirk; slice (h)
    /// economy guards revisit it. The command logs a Warning when the old owner
    /// is online so operators can <c>/kick</c> for a clean cutover.</para>
    ///
    /// <para><b>Renamed prior owner reconnects.</b> The prior owner reconnects
    /// after the rename with no <see cref="AgencyByPlayerName"/> mapping; their
    /// handshake mints a FRESH agency seeded from
    /// <see cref="GameplaySettingsDefinition.StartingFunds"/> /
    /// <see cref="GameplaySettingsDefinition.StartingScience"/> /
    /// <see cref="GameplaySettingsDefinition.StartingReputation"/>. They do NOT
    /// inherit any state from the renamed agency. Operators rebalancing for
    /// this UX should use <c>/setagency</c> on the fresh agency post-mint.</para>
    /// </summary>
    public class TransferAgencyCommand : SimpleCommand
    {
        // Vessel-scoped lock types that should be released from the old owner
        // post-rename. Non-vessel-scoped types (Spectator / AsteroidComet /
        // Contract / Kerbal) are skipped — they don't relate to vessel ownership
        // and shouldn't be churned by an owner-rename. Operators wanting a
        // clean "owner gone, release all" cutover should /kick the old owner
        // first (which calls LockSystem.ReleasePlayerLocks for everything).
        private static readonly HashSet<LockType> VesselScopedLockTypes = new HashSet<LockType>
        {
            LockType.Control,
            LockType.Update,
            LockType.UnloadedUpdate,
        };

        public override bool Execute(string commandArgs)
        {
            if (!TransferAgencyCommandParser.TryParse(commandArgs, out var sourceToken, out var newOwnerName, out var parseError))
            {
                LunaLog.Error(parseError);
                LunaLog.Normal(TransferAgencyCommandParser.UsageBanner);
                return false;
            }

            // Gate refusal (mirrors /setagency slice f).
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
            {
                LunaLog.Error(
                    "transferagency: requires PerAgencyCareer=true. Under PerAgencyCareer=false there are no " +
                    "per-agency career states to transfer.");
                return false;
            }
            if (!AgencySystem.PerAgencyEnabled)
            {
                LunaLog.Error(
                    "transferagency: requires GameMode=Career. PerAgencyCareer=true but GameMode is not Career — set " +
                    "GameMode=Career in Settings/GeneralSettings.xml to activate, or set PerAgencyCareer=false in " +
                    "Settings/GameplaySettings.xml to disable per-agency cleanly (may flip GameDifficulty to Custom " +
                    "— see CLAUDE.md Settings caveat).");
                return false;
            }

            // Name-cap validation. The handshake validator enforces this on connecting
            // clients via HandshakeSystemValidator; we mirror it here so an operator
            // can't pre-seed an over-cap name that would later fail to reconnect.
            var maxLen = GeneralSettings.SettingsStore.MaxUsernameLength;
            if (newOwnerName.Length > maxLen)
            {
                LunaLog.Error($"transferagency: new player name '{newOwnerName}' exceeds the {maxLen}-character cap.");
                return false;
            }

            // Source resolve.
            if (!AgencySystem.TryResolveAgencyToken(sourceToken, out var source))
            {
                if (AgencySystem.Agencies.IsEmpty)
                {
                    LunaLog.Error(
                        "transferagency: no agencies are registered yet. An agency mints on the owning player's " +
                        "first connect under PerAgencyCareer=true.");
                }
                else
                {
                    LunaLog.Error(
                        $"transferagency: agency token '{sourceToken}' does not match any registered agency. " +
                        "Pass either an agency id (run /listagencies) or the agency owner's REGISTRATION-time " +
                        "LMP handle. Orphaned agency ids from boot warnings cannot be transferred (no " +
                        "AgencyState exists); the operator workflow is restore Universe/Agencies/{guid}.txt(.bak), " +
                        "or accept the loss and let owning players mint fresh agencies on reconnect.");
                }
                return false;
            }

            var oldOwnerName = source.OwningPlayerName ?? string.Empty;
            var sourceAgencyId = source.AgencyId;

            // Same-name short-circuit (server-systems-review v1 SS-1 + consumer-lens
            // v1 CL-4). Idempotent re-runs of the command from a script or GUI must
            // NOT release the (still-current) owner's vessel-scoped locks or churn
            // an unnecessary AgencyStateMsgData echo. Emit a distinct no-op log
            // line so the GUI launcher can distinguish "nothing changed" from
            // "transfer landed but moved 0 locks."
            if (string.Equals(oldOwnerName, newOwnerName, StringComparison.Ordinal))
            {
                LunaLog.Normal(
                    $"[fix:per-agency-career] transferagency {sourceAgencyId:N} no-op (owner already '{oldOwnerName}')");
                return true;
            }

            // Atomic rename. TryRenameAgencyOwner enforces the dual-name-lock +
            // agency-lock acquire order + collision re-check.
            if (!AgencySystem.TryRenameAgencyOwner(source, newOwnerName, out var failureReason))
            {
                LunaLog.Error($"transferagency: {failureReason}");
                return false;
            }

            // Snapshot DisplayName for the operator-visible hint at the end. Read
            // after the rename (no concurrency hazard on display-name field; only
            // OwningPlayerName mutated). Held for the hint emission below.
            var displayNameSnapshot = source.DisplayName ?? string.Empty;

            // Release the old owner's vessel-scoped locks for vessels stamped with
            // this agency. Each released lock emits its own audit log line so the
            // GUI / operator can see exactly which vessels lost authority.
            var oldOwnerClient = string.IsNullOrEmpty(oldOwnerName) ? null : ClientRetriever.GetClientByName(oldOwnerName);
            var releasedCount = ReleaseOldOwnerVesselLocks(oldOwnerName, sourceAgencyId, oldOwnerClient);

            // Re-anchor the new owner's client identity. The Handshake sets
            // LocalAgencyId so the subsequent State message passes
            // AgencyMembership.IsForLocalAgency. Without this two-step sequence,
            // HandleState would silently drop the State message when LocalAgencyId
            // is Empty (new owner never auto-registered) or differs from this
            // agency's id (consumer-lens v1 MUST FIX). Both messages target the
            // new-owner client; offline new owners get nothing here and pick up
            // the state via the standard handshake path on next connect.
            var newOwnerClient = ClientRetriever.GetClientByName(newOwnerName);
            if (newOwnerClient != null)
            {
                AgencySystemSender.SendHandshakeTo(newOwnerClient, sourceAgencyId);
                EchoStateToClient(newOwnerClient, source);
            }

            // Operator-visible logging. Grammar matches slice (f) setagency for
            // GUI-parse consistency: <verb> <id> field=value ... released=N.
            // Each subsequent hint is its own tagged line so a GUI consumer can
            // route them to the right surface (audit panel vs status banner).
            var oldLabel = string.IsNullOrEmpty(oldOwnerName) ? string.Empty : oldOwnerName;
            LunaLog.Normal(
                $"[fix:per-agency-career] transferagency {sourceAgencyId:N} owner old='{oldLabel}' new='{newOwnerName}' released={releasedCount}");

            // DisplayName-unchanged hint (upgrade-lens v1 S1). TryRenameAgencyOwner
            // only mutates OwningPlayerName; the display name stays whatever
            // CreateRequest had last set (or the auto-registered default
            // "{oldOwnerName} Space Agency"). Operators discover the labelling
            // mismatch via vessel UI; this log surfaces it pre-emptively.
            LunaLog.Normal(
                $"[fix:per-agency-career] transferagency {sourceAgencyId:N} note display='{displayNameSnapshot}' unchanged " +
                $"(the new owner can rename via the in-game Agency window or by reconnecting and re-sending CreateRequest)");

            // Renamed-prior-owner mint hint (upgrade-lens v1 S2). The prior owner's
            // reconnect mints a fresh agency seeded from StartingFunds/Science/
            // Reputation; they do NOT inherit any career state from the renamed
            // agency. Operators rebalancing for this can run /setagency on the
            // fresh agency post-mint.
            if (!string.IsNullOrEmpty(oldOwnerName))
            {
                LunaLog.Normal(
                    $"[fix:per-agency-career] transferagency {sourceAgencyId:N} note prior owner '{oldOwnerName}' " +
                    "will mint a fresh agency on next reconnect (seeded from GameplaySettings StartingFunds/Science/" +
                    "Reputation — no career state is inherited from this renamed agency)");
            }

            // Connected-old-owner Warning (server-systems-review v1 SS-2 + upgrade-
            // lens v1 C1). Until the prior owner reconnects, they retain a stale
            // LocalAgencyId on the client side AND gain the 5.17a "requester has
            // no agency mapping" bypass server-side. Recommend /kick for clean
            // cutover. Promoted to LunaLog.Warning so the GUI launcher's warning
            // surface picks it up.
            if (oldOwnerClient != null)
            {
                LunaLog.Warning(
                    $"[fix:per-agency-career] transferagency {sourceAgencyId:N} WARNING: prior owner '{oldOwnerName}' " +
                    "is currently online; their client retains a stale LocalAgencyId until reconnect and server-side " +
                    "they are momentarily agency-less (Stage 5.17a 'requester has no agency mapping' bypass quirk — " +
                    "slice h economy guards revisit). For clean cutover run /kick " + oldOwnerName + " before this " +
                    "command, or after to force their reconnect.");
            }

            return true;
        }

        private static int ReleaseOldOwnerVesselLocks(string oldOwnerName, Guid sourceAgencyId, ClientStructure oldOwnerClient)
        {
            if (string.IsNullOrEmpty(oldOwnerName)) return 0;

            // Snapshot the locks to release first — the underlying LockStore
            // dictionary mutates during ReleaseAndSendLockReleaseMessage and we
            // don't want to enumerate it while modifying it.
            var locksHeld = LockSystem.LockQuery.GetAllPlayerLocks(oldOwnerName);
            var toRelease = locksHeld
                .Where(l => VesselScopedLockTypes.Contains(l.Type))
                .Where(l => l.VesselId != Guid.Empty)
                .Where(l => VesselStoreSystem.CurrentVessels.TryGetValue(l.VesselId, out var v)
                    && v.OwningAgencyId == sourceAgencyId)
                .ToList();

            foreach (var lockDef in toRelease)
            {
                // Per-lock audit log so the GUI / operator can see exactly which
                // vessels lost authority (consumer-lens v1 CL-3). Volume is
                // typically small (0-3 locks per transfer); the audit value
                // outweighs the log noise.
                LunaLog.Normal(
                    $"[fix:per-agency-career] transferagency {sourceAgencyId:N} released-lock " +
                    $"vessel={lockDef.VesselId:N} type={lockDef.Type}");

                LockSystemSender.ReleaseAndSendLockReleaseMessage(oldOwnerClient, lockDef);
            }
            return toRelease.Count;
        }

        private static void EchoStateToClient(ClientStructure client, AgencyState state)
        {
            var msg = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyStateMsgData>();
            msg.AgencyId = state.AgencyId;
            msg.OwningPlayerName = state.OwningPlayerName ?? string.Empty;
            msg.DisplayName = state.DisplayName ?? string.Empty;
            msg.Funds = state.Funds;
            msg.Science = state.Science;
            msg.Reputation = state.Reputation;
            MessageQueuer.SendToClient<AgencySrvMsg>(client, msg);
        }
    }
}
