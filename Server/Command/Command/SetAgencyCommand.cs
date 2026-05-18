using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Command.Command.Base;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Globalization;

namespace Server.Command.Command
{
    /// <summary>
    /// Stage 5.18d slice (f). Per-agency scalar mutation admin command. Three
    /// subcommands map to the three scalar fields on <see cref="AgencyState"/>:
    /// <c>funds</c>, <c>science</c>, <c>reputation</c> (alias <c>rep</c>). Same
    /// shape as <see cref="BackupCommand"/>'s subcommand surface — operator types
    /// one verb + arguments, no interactive prompts, so the Stage 5.18+ GUI
    /// launcher can wrap the call through stdin without buffering.
    ///
    /// <para><b>Usage:</b> see <see cref="SetAgencyCommandParser.UsageBanner"/>.
    /// Token names (<c>agency-id</c>, <c>owner</c>) mirror the <c>/listagencies</c>
    /// output columns (<c>id=</c>, <c>owner=</c>) so an operator switching between
    /// the two surfaces doesn't relearn vocabulary.</para>
    ///
    /// <para><b>Gate-off / configured-but-inactive refusal.</b> Both states refuse
    /// loudly with the resolution paths inlined (so the operator doesn't need to
    /// scroll back to a boot warning that may have rolled out of buffer). Same
    /// framing as <see cref="ListAgenciesFormatter"/>'s disabled / inactive
    /// lines.</para>
    ///
    /// <para><b>Mutation flow.</b> Acquires <see cref="AgencySystem.GetAgencyLock"/>
    /// around the field write + <see cref="AgencySystem.SaveAgency"/> + the
    /// <see cref="AgencyStateMsgData"/> build (matches the
    /// <see cref="Server.Message.AgencyMsgReader.HandleCreateRequest"/> precedent
    /// for atomic mutation-then-persist-then-snapshot). The owner-echo message
    /// is sent OUTSIDE the lock; the snapshot inside the lock prevents a
    /// concurrent <c>AgencyCurrencyRouter</c> / <c>AgencyResearchRouter</c> write
    /// from clobbering the echoed scalars before the message factory captures
    /// them.</para>
    ///
    /// <para><b>Operator-vs-router overwrite semantics.</b> Operator writes do NOT
    /// compose with concurrent Share*Router deltas — the operator's value REPLACES
    /// the current scalar. A player's freshly-earned contract reward landing
    /// between <c>/listagencies</c> and <c>/setagency</c> will be lost. To minimise
    /// the window, run <c>/listagencies</c> immediately before <c>/setagency</c>;
    /// the success log line emits old→new so the operator can verify the read
    /// didn't go stale.</para>
    /// </summary>
    public class SetAgencyCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            // (1) Pure parse. No AgencySystem touch yet so a misuse produces a
            // usage hint without spamming the registry/lock surface.
            if (!SetAgencyCommandParser.TryParse(commandArgs, out var sub, out var token, out var value, out var parseError))
            {
                LunaLog.Error(parseError);
                LunaLog.Normal(SetAgencyCommandParser.UsageBanner);
                return false;
            }

            // (2) Gate-off refusal. Two distinct off states; both inline the
            // recovery paths so the operator doesn't need the boot warning.
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
            {
                LunaLog.Error(
                    "setagency: requires PerAgencyCareer=true. Under PerAgencyCareer=false the shared-agency career " +
                    "is authoritative — use /setfunds and /setscience (those commands work only under gate=off and " +
                    "refuse when PerAgencyCareer=true). Reputation has no shared-agency setter; edit Universe/Scenarios/ " +
                    "directly while the server is stopped if needed.");
                return false;
            }
            if (!AgencySystem.PerAgencyEnabled)
            {
                LunaLog.Error(
                    "setagency: requires GameMode=Career. PerAgencyCareer=true but GameMode is not Career — set " +
                    "GameMode=Career in Settings/GeneralSettings.xml to activate, or set PerAgencyCareer=false in " +
                    "Settings/GameplaySettings.xml to disable per-agency cleanly (may flip GameDifficulty to Custom " +
                    "— see CLAUDE.md Settings caveat).");
                return false;
            }

            // (3) Resolve the token. Distinct error for the empty-registry case
            // (upgrade-lens v1 UL-4) so operators on a fresh-per-agency universe
            // pre-first-connect see "no agencies yet" instead of "you typed it wrong."
            if (!AgencySystem.TryResolveAgencyToken(token, out var state))
            {
                if (AgencySystem.Agencies.IsEmpty)
                {
                    LunaLog.Error(
                        "setagency: no agencies are registered yet. An agency mints on the owning player's first " +
                        "connect under PerAgencyCareer=true. On an upgrade-in-place universe with " +
                        "AllowEnablePerAgencyOnExistingUniverse=true, wait for at least one player to connect, then " +
                        "retry. /listagencies confirms the current registry state.");
                }
                else
                {
                    LunaLog.Error(
                        $"setagency: agency token '{token}' does not match any registered agency. Pass either an " +
                        "agency id (run /listagencies to see ids) or the agency owner's REGISTRATION-time LMP handle " +
                        "— if the owning player reconnected under a different name, look up by id. Orphaned agency " +
                        "ids from boot warnings recover via /transferagency (slice e), not /setagency.");
                }
                return false;
            }

            // (4) Mutate under lock + persist + snapshot for the owner echo.
            // Capturing the post-mutate scalars + identity fields INSIDE the lock
            // prevents a concurrent Share*Router write from racing the message
            // build (server-systems-review v1 SS-2). Matches HandleCreateRequest's
            // capturedDisplayName pattern.
            double oldValue;
            double snapshotFunds, snapshotScience, snapshotReputation;
            string snapshotOwner, snapshotDisplay;
            Guid snapshotId;
            lock (AgencySystem.GetAgencyLock(state.AgencyId))
            {
                switch (sub)
                {
                    case SetAgencyCommandParser.Scalar.Funds:
                        oldValue = state.Funds;
                        state.Funds = value;
                        break;
                    case SetAgencyCommandParser.Scalar.Science:
                        oldValue = state.Science;
                        state.Science = value;
                        break;
                    case SetAgencyCommandParser.Scalar.Reputation:
                        oldValue = state.Reputation;
                        state.Reputation = value;
                        break;
                    default:
                        // Defensive — Scalar is internal so this is unreachable, but
                        // keeps the switch exhaustive.
                        oldValue = 0d;
                        break;
                }
                AgencySystem.SaveAgency(state.AgencyId);

                snapshotId = state.AgencyId;
                snapshotOwner = state.OwningPlayerName ?? string.Empty;
                snapshotDisplay = state.DisplayName ?? string.Empty;
                snapshotFunds = state.Funds;
                snapshotScience = state.Science;
                snapshotReputation = state.Reputation;
            }

            // (5) Build + send the owner echo using the in-lock snapshot. Cannot
            // call SendStateToOwner(state) directly — its read of state.Funds/etc.
            // is unlocked. Build the msg from the snapshot and route via the
            // existing ClientRetriever lookup.
            EchoStateToOwnerFromSnapshot(
                snapshotId, snapshotOwner, snapshotDisplay,
                snapshotFunds, snapshotScience, snapshotReputation);

            // (6) Operator-visible log on stdout (the Stage 5.18+ GUI launcher
            // parses this). InvariantCulture + "R" mirrors /listagencies's row
            // emit; old→new framing surfaces stale-read drift.
            var ownerLabel = string.IsNullOrEmpty(snapshotOwner) ? "(no owner)" : snapshotOwner;
            LunaLog.Normal(
                $"[fix:per-agency-career] setagency {sub.ToString().ToLowerInvariant()} for " +
                $"{snapshotId:N} (owner={ownerLabel}) old={oldValue.ToString("R", CultureInfo.InvariantCulture)} " +
                $"new={value.ToString("R", CultureInfo.InvariantCulture)}");
            return true;
        }

        /// <summary>
        /// Sends an <see cref="AgencyStateMsgData"/> to the owning client built
        /// from the in-lock snapshot of the agency's scalar fields. Distinct from
        /// <see cref="AgencySystemSender.SendStateToOwner"/> which reads from the
        /// live <c>AgencyState</c> outside the lock — that's the race window we're
        /// avoiding. No-op when the owner is offline; they pick up the new state
        /// on next handshake (HandshakeSystem reads the canonical store which the
        /// SaveAgency above persisted).
        /// </summary>
        private static void EchoStateToOwnerFromSnapshot(
            Guid agencyId, string owningPlayerName, string displayName,
            double funds, double science, double reputation)
        {
            if (string.IsNullOrEmpty(owningPlayerName)) return;
            var owner = ClientRetriever.GetClientByName(owningPlayerName);
            if (owner == null) return;

            var msg = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyStateMsgData>();
            msg.AgencyId = agencyId;
            msg.OwningPlayerName = owningPlayerName;
            msg.DisplayName = displayName;
            msg.Funds = funds;
            msg.Science = science;
            msg.Reputation = reputation;
            MessageQueuer.SendToClient<AgencySrvMsg>(owner, msg);
        }
    }
}
