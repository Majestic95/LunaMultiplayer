using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Base for the per-agency career wire surface (Stage 5). Subtypes are listed in
    /// <see cref="Types.AgencyMessageType"/>. The base intentionally carries no payload —
    /// agency messages are not vessel-bound and don't share a common header. Each
    /// concrete subtype writes its own fields.
    /// </summary>
    public abstract class AgencyBaseMsgData : MessageData
    {
        /// <inheritdoc />
        internal AgencyBaseMsgData() { }
        public override ushort SubType => (ushort)(int)AgencyMessageType;

        public virtual AgencyMessageType AgencyMessageType => throw new NotImplementedException();

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            //Nothing to implement here
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            //Nothing to implement here
        }

        internal override int InternalGetMessageSize()
        {
            return 0;
        }
    }
}
