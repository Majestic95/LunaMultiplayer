using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// Outbound side of the per-agency career wire (Stage 5.18a). The only
    /// client→server message currently defined is <see cref="AgencyCreateRequestMsgData"/>,
    /// which the Stage 5.18c create-window UI emits when the user customises their
    /// auto-registered agency's display name. Despite the "Create" name, this is a
    /// rename-on-connect — the server has already auto-registered the player's
    /// agency at handshake time and replies with the SAME id (see
    /// <see cref="AgencyCreateRequestMsgData"/> XML for the full contract).
    ///
    /// The other agency wire-types are server→client only (<see cref="AgencyHandshakeMsgData"/>,
    /// <see cref="AgencyStateMsgData"/>, <see cref="AgencyCreateReplyMsgData"/>,
    /// <see cref="AgencyContractMsgData"/>) and have no client-emitted counterpart;
    /// per-agency mid-session writes continue to flow through the existing
    /// <c>ShareProgress*MsgData</c> wire that the server's per-agency router
    /// intercepts. The 5.18b write-path patches and the 5.17e router echo path are
    /// the actual write-time mechanism — this sender exists purely for the
    /// rename surface.
    /// </summary>
    public class AgencyMessageSender : SubSystem<AgencySystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<AgencyCliMsg>(msg)));
        }

        /// <summary>
        /// Sends a rename request for the local player's auto-registered agency. The
        /// server replies with <see cref="AgencyCreateReplyMsgData"/> carrying
        /// <c>Success</c> + the canonical <c>DisplayName</c> (echoes the trimmed /
        /// accepted value on success, or the previous display name on failure). The
        /// 5.18c UI should accept the server's reply as canonical rather than
        /// optimistically setting the local state before the round-trip — server-side
        /// <c>AgencyMsgReader.ValidateDisplayName</c> may reject the input
        /// (whitespace-only, &gt;64 chars, contains <c>= { } \n \r</c> or
        /// <c>char.IsControl</c>) and we don't want a UI showing an invalid name.
        /// </summary>
        public void SendCreateRequest(string displayName)
        {
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyCreateRequestMsgData>();
            msgData.DisplayName = displayName ?? string.Empty;
            SendMessage(msgData);
        }
    }
}
