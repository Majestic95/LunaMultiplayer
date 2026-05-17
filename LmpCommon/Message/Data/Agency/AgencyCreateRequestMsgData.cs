using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Client → server. Sent if the client wants to override the auto-registered
    /// display name (default = <c>"{PlayerName} Space Agency"</c>) with something
    /// custom on first connect. Server validates and responds with
    /// <see cref="AgencyCreateReplyMsgData"/>.
    ///
    /// Subsequent renames (post-creation) will use a dedicated rename message in
    /// a later stage; this one is the create-or-rename-on-handshake path only.
    /// </summary>
    public class AgencyCreateRequestMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyCreateRequestMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CreateRequest;

        public string DisplayName = string.Empty;

        public override string ClassName { get; } = nameof(AgencyCreateRequestMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(DisplayName ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            DisplayName = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + DisplayName.GetByteCount();
        }
    }
}
