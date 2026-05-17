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
        /// Value 0 is the no-dedupe sentinel — the server's <see cref="Server.System.WarpRequestCache"/>
        /// ignores it and always falls through to the always-mint path. The defensive read below
        /// (zero when trailing 4 bytes are missing) historically supported pre-fix clients; after
        /// the protocol bump to 0.30.0 those peers cannot complete the handshake at all, so the
        /// defensive read now serves only replay-log / offline-tooling parsing of historical captures.
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
            // Bit-aligned remainder check: Lidgren's read position is bit-indexed (LengthBits / Position),
            // so the byte-aligned form `LengthBytes - PositionInBytes` could be off by up to 7 bits if
            // an upstream serializer ever wrote a sub-byte field before this one. None do today, but the
            // bit form is the unambiguous contract.
            RequestSeq = (lidgrenMsg.LengthBits - lidgrenMsg.Position) >= 32 ? lidgrenMsg.ReadUInt32() : 0u;
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + PlayerCreator.GetByteCount() + sizeof(int) + sizeof(double) + sizeof(uint);
        }
    }
}