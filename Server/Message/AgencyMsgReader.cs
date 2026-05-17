using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Log;
using Server.Message.Base;
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
            // Dual-mode gate. Even though the wire surface exists, the receive path is
            // a no-op when per-agency is off — the client should not have sent this in
            // the first place (it wouldn't have an AgencySystem to populate from), but
            // we don't crash if it does.
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
                return;

            var data = (AgencyBaseMsgData)message.Data;
            switch (data.AgencyMessageType)
            {
                case AgencyMessageType.CreateRequest:
                    HandleCreateRequest(client, (AgencyCreateRequestMsgData)data);
                    break;
                case AgencyMessageType.Handshake:
                case AgencyMessageType.CreateReply:
                case AgencyMessageType.State:
                    // Server-→-client subtypes that should not be inbound. Drop with a log
                    // so a misbehaving / malicious client can't drive the server into
                    // unexpected paths.
                    LunaLog.Warning($"[fix:per-agency-career] Received server-→-client subtype {data.AgencyMessageType} from {client.PlayerName}; dropping");
                    break;
                default:
                    LunaLog.Warning($"[fix:per-agency-career] Unknown AgencyMessageType {data.AgencyMessageType} from {client.PlayerName}; dropping");
                    break;
            }
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

            // Hold the per-agency lock per the SaveAgency locking contract (see
            // AgencySystem.GetAgencyLock doc). Stage 5.17b Share* writers will follow
            // the same pattern when mutating field values.
            lock (AgencySystem.GetAgencyLock(state.AgencyId))
            {
                state.DisplayName = msg.DisplayName;
            }
            AgencySystem.SaveAgency(state.AgencyId);

            // Confirm to the requester, then send their full state so their local UI
            // mirror picks up the new name. Other clients learn about the rename later
            // via the Stage 5.18c visibility surface — not in scope here.
            AgencySystemSender.SendCreateReplyTo(client, state.AgencyId, state.DisplayName, success: true, reason: string.Empty);
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
