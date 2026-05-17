using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server → client. Acknowledges an <see cref="AgencyCreateRequestMsgData"/>. On
    /// success the server has applied the new DisplayName to the agency, persisted via
    /// <c>AgencySystem.SaveAgency</c>, and broadcast the update; on failure it returns
    /// the rejection reason and the previous DisplayName so the client can resync its
    /// local view.
    /// </summary>
    public class AgencyCreateReplyMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyCreateReplyMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CreateReply;

        public Guid AgencyId;
        public string DisplayName = string.Empty;
        public bool Success;
        public string Reason = string.Empty;

        public override string ClassName { get; } = nameof(AgencyCreateReplyMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            GuidUtil.Serialize(AgencyId, lidgrenMsg);
            lidgrenMsg.Write(DisplayName ?? string.Empty);
            lidgrenMsg.Write(Success);
            lidgrenMsg.Write(Reason ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            AgencyId = GuidUtil.Deserialize(lidgrenMsg);
            DisplayName = lidgrenMsg.ReadString();
            Success = lidgrenMsg.ReadBoolean();
            Reason = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize()
                + GuidUtil.ByteSize
                + DisplayName.GetByteCount()
                + sizeof(bool)
                + Reason.GetByteCount();
        }
    }
}
