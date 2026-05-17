using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.ShareProgress
{
    /// <summary>
    /// [fix:BUG-025] Server-to-client rejection of a duplicate R&D tech purchase.
    /// Server detected (via per-scenario lock + scenario lookup) that the tech node
    /// the sender just bought was already unlocked by another client. The sender
    /// must refund <see cref="RefundScience"/> to the local
    /// <c>ResearchAndDevelopment.Instance.Science</c> pool.
    ///
    /// One-way only: there is no client-to-server rejection. The corresponding
    /// entry lives in <c>ShareProgressSrvMsg.SubTypeDictionary</c>, not the
    /// client wrapper's, so a hostile (or buggy) client sending this subtype
    /// inbound deserializes to nothing on the server side.
    /// </summary>
    public class ShareProgressTechnologyRejectedMsgData : ShareProgressBaseMsgData
    {
        /// <inheritdoc />
        internal ShareProgressTechnologyRejectedMsgData() { }
        public override ShareProgressMessageType ShareProgressMessageType => ShareProgressMessageType.TechnologyRejected;

        /// <summary>KSP <c>RDTech.techID</c> of the rejected purchase.</summary>
        public string TechId;

        /// <summary>Science cost the sender claimed in their original message; refund this amount.</summary>
        public float RefundScience;

        public override string ClassName { get; } = nameof(ShareProgressTechnologyRejectedMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            lidgrenMsg.Write(TechId ?? string.Empty);
            lidgrenMsg.Write(RefundScience);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            TechId = lidgrenMsg.ReadString();
            RefundScience = lidgrenMsg.ReadFloat();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + (TechId ?? string.Empty).GetByteCount() + sizeof(float);
        }
    }
}
