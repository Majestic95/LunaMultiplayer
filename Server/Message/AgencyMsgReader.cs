using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Message.Base;
using Server.Server;
using Server.Settings.Structures;
using Server.System.Agency;
using System;

namespace Server.Message
{
    /// <summary>
    /// Server-side dispatcher for <see cref="AgencyCliMsg"/> subtypes
    /// (<see cref="AgencyMessageType"/>). Wired into
    /// <see cref="Server.Server.MessageReceiver.HandlerDictionary"/> under
    /// <see cref="LmpCommon.Enums.ClientMessageType.Agency"/>.
    ///
    /// Stage 5.15c scope is the CreateRequest handler only. The other three subtypes
    /// (Handshake / CreateReply / State) are server-→-client and listed in
    /// <see cref="AgencyCliMsg.SubTypeDictionary"/> only so the wire-symmetry rule
    /// holds — they should not arrive here in practice; if they do, log and drop
    /// rather than crash.
    /// </summary>
    public class AgencyMsgReader : ReaderBase
    {
        /// <summary>
        /// Display names are stored alongside player handles. The 15-char player-name cap
        /// is too tight for "Cool Space Co" style labels; the existing 64-char cap on
        /// most metadata fields (e.g. server name) is a reasonable upper bound here too.
        /// Long names balloon the AgencyHandshakeMsgData broadcast surface — every
        /// connected client receives every other agency's display name in the on-connect
        /// handshake — so the cap is operator-protective, not just aesthetic.
        /// </summary>
        internal const int MaxDisplayNameLength = 64;

        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var data = (AgencyBaseMsgData)message.Data;

            // Dual-mode gate. With per-agency career inactive (gate off OR non-Career game
            // mode — Stage 5.17e-1 Career-only product decision, spec §10 Q-Mode), the
            // agency wire surface is intentionally silent (spec §11). A well-behaved 0.31.0
            // client knows the active state via SettingsMsgData (Stage 5.17e-2 wire field)
            // and would never send a CreateRequest in this mode. But: a buggy / mid-upgrade
            // client that ships CreateRequest under gate-off would hang waiting for a reply
            // that never comes — silent timeout is the worst error UX. So for CreateRequest
            // specifically, ship a single targeted Success=false reply so the client can
            // surface the misconfiguration in its UI. This is the one intentional deviation
            // from dual-mode silence; it is unicast (no broadcast / no other-client leak)
            // and only triggers when a non-conforming client speaks first. Round-5
            // consumer-lens review.
            if (!AgencySystem.PerAgencyEnabled)
            {
                if (data.AgencyMessageType == AgencyMessageType.CreateRequest)
                {
                    var request = (AgencyCreateRequestMsgData)data;
                    SendGateOffRejection(client, request.DisplayName ?? string.Empty);
                }
                // Other subtypes (Handshake / CreateReply / State arriving inbound) are
                // server-→-client and a buggy client sending them already broke protocol;
                // we don't owe them a reply.
                return;
            }

            switch (data.AgencyMessageType)
            {
                case AgencyMessageType.CreateRequest:
                    HandleCreateRequest(client, (AgencyCreateRequestMsgData)data);
                    break;
                case AgencyMessageType.Handshake:
                case AgencyMessageType.CreateReply:
                case AgencyMessageType.State:
                case AgencyMessageType.Contract:
                case AgencyMessageType.Visibility:
                    // Server-→-client subtypes that should not be inbound. Drop with a log
                    // so a misbehaving / malicious client can't drive the server into
                    // unexpected paths. (Stage 5.17d appended Contract; Stage 5.18d
                    // appended Visibility — broadcast S→C ownership push for
                    // transferagency / deleteagency. Clients NEVER originate ownership
                    // transitions; admin commands are operator-only via the server console.)
                    LunaLog.Warning($"[fix:per-agency-career] Received server-→-client subtype {data.AgencyMessageType} from {client.PlayerName}; dropping");
                    break;
                default:
                    LunaLog.Warning($"[fix:per-agency-career] Unknown AgencyMessageType {data.AgencyMessageType} from {client.PlayerName}; dropping");
                    break;
            }
        }

        /// <summary>
        /// Targeted "gate is off" reply for a buggy client that shipped CreateRequest
        /// despite PerAgencyCareer=false. Built inline rather than going through
        /// <see cref="AgencySystemSender.SendCreateReplyTo"/> (which is gated and would no-op)
        /// because this is intentionally the one path that crosses the dual-mode-silence
        /// boundary — only on a unicast reply to a non-conforming peer, never on broadcast.
        /// </summary>
        private static void SendGateOffRejection(ClientStructure client, string attemptedDisplayName)
        {
            var reply = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyCreateReplyMsgData>();
            reply.AgencyId = Guid.Empty;
            reply.DisplayName = attemptedDisplayName;
            reply.Success = false;
            reply.Reason = "Per-agency career is not active on this server (requires PerAgencyCareer=true and GameMode=Career)";
            MessageQueuer.SendToClient<AgencySrvMsg>(client, reply);

            LunaLog.Warning($"[fix:per-agency-career] {client.PlayerName} sent CreateRequest with gate off; replying with Success=false");
        }

        private static void HandleCreateRequest(ClientStructure client, AgencyCreateRequestMsgData msg)
        {
            if (!ValidateDisplayName(msg.DisplayName, out var rejectionReason))
            {
                AgencySystemSender.SendCreateReplyTo(client, Guid.Empty, msg.DisplayName ?? string.Empty, success: false, reason: rejectionReason);
                return;
            }

            // RegisterAgency is idempotent — if the player already has an agency (the
            // common case, since HandshakeSystem auto-registers on auth), this returns
            // their existing AgencyState. We then update DisplayName in place and persist.
            var state = AgencySystem.RegisterAgency(client.PlayerName);
            if (state == null)
            {
                // PerAgencyCareer is off — but the early-return at the top of HandleMessage
                // should have prevented us getting here. Defensive log only.
                LunaLog.Warning($"[fix:per-agency-career] RegisterAgency returned null for {client.PlayerName} despite gate being on; dropping CreateRequest");
                return;
            }

            // Hold the per-agency lock across the entire mutate → persist → reply →
            // state-send sequence so that:
            //   (a) SaveAgency serialises a consistent DisplayName (existing contract),
            //   (b) the CreateReply.DisplayName value matches the persisted value (no
            //       racing CreateRequests for the same agency can have one's persisted
            //       value reported in the other's reply — round-2 wire review),
            //   (c) the State broadcast carries the same DisplayName as the reply.
            // Stage 5.17b Share* writers will follow the same pattern for field mutations.
            string capturedDisplayName;
            lock (AgencySystem.GetAgencyLock(state.AgencyId))
            {
                state.DisplayName = msg.DisplayName;
                AgencySystem.SaveAgency(state.AgencyId);
                capturedDisplayName = state.DisplayName;
            }

            // Confirm to the requester, then send their full state so their local UI
            // mirror picks up the new name. Other clients learn about the rename later
            // via the Stage 5.18c visibility surface — not in scope here.
            AgencySystemSender.SendCreateReplyTo(client, state.AgencyId, capturedDisplayName, success: true, reason: string.Empty);
            AgencySystemSender.SendStateTo(client, state);
        }

        // internal for ServerTest visibility — this is pure-data validation worth
        // unit-pinning independently of the full HandleMessage path (the wire-side
        // integration is exercised end-to-end in Stage 5.16a's MockClient harness).
        internal static bool ValidateDisplayName(string displayName, out string reason)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                reason = "Display name must not be empty or whitespace";
                return false;
            }

            if (displayName.Length > MaxDisplayNameLength)
            {
                reason = $"Display name exceeds {MaxDisplayNameLength}-character limit";
                return false;
            }

            // ConfigNode-format hostile characters. AgencyState persists through
            // LunaConfigNode key = value lines; bare '=' / '{' / '}' / newline would
            // either corrupt the on-disk format or land an unloadable file.
            for (var i = 0; i < displayName.Length; i++)
            {
                var c = displayName[i];
                if (c == '=' || c == '{' || c == '}' || c == '\n' || c == '\r' || char.IsControl(c))
                {
                    reason = "Display name contains illegal characters (=, {, }, newlines, control characters)";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }
    }
}
