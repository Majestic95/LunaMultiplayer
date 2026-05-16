using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Warp
{
    public class WarpNewSubspaceMsgData : WarpBaseMsgData
    {
        /// <inheritdoc />
        internal WarpNewSubspaceMsgData() { }
        public override WarpMessageType WarpMessageType => WarpMessageType.NewSubspace;

        public string PlayerCreator;
        public int SubspaceKey;
        public double ServerTimeDifference;

        /// <summary>
        /// Client-assigned identifier for this request. The server uses (PlayerCreator, RequestSeq)
        /// to dedupe retried subspace-creation requests so a stuck client cannot mint orphan subspaces.
        /// Pre-fix clients send 0 (sentinel — "do not dedupe"); the server falls through to the legacy
        /// always-mint path. Deserialize is defensive so a pre-fix payload missing the trailing 4 bytes
        /// still parses cleanly.
        /// </summary>
        public uint RequestSeq;

        public override string ClassName { get; } = nameof(WarpNewSubspaceMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(PlayerCreator);
            lidgrenMsg.Write(SubspaceKey);
            lidgrenMsg.Write(ServerTimeDifference);
            lidgrenMsg.Write(RequestSeq);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            PlayerCreator = lidgrenMsg.ReadString();
            SubspaceKey = lidgrenMsg.ReadInt32();
            ServerTimeDifference = lidgrenMsg.ReadDouble();
            RequestSeq = lidgrenMsg.LengthBytes - lidgrenMsg.PositionInBytes >= sizeof(uint) ? lidgrenMsg.ReadUInt32() : 0u;
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + PlayerCreator.GetByteCount() + sizeof(int) + sizeof(double) + sizeof(uint);
        }
    }
}