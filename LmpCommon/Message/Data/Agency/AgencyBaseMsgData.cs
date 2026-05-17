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

        /// <summary>
        /// Maximum byte length of any string field carried by an Agency*MsgData on the wire.
        /// Mirrors the server-side <c>AgencyMsgReader.MaxDisplayNameLength = 64</c> with
        /// generous headroom (UTF-8 expansion + future-field tolerance). Round-3 wire review:
        /// even on server-→-client subtypes (Handshake / CreateReply / State), the inbound
        /// direction is reachable via misrouted CliMsg, and an unbounded ReadString allocation
        /// from a malicious peer is a clear DoS amplification vector. Deserialize sites that
        /// read string fields MUST bounds-check against this cap before reading.
        /// </summary>
        internal const int MaxStringByteLength = 512;

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
