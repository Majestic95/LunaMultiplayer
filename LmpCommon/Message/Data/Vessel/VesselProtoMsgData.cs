using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Vessel
{
    public class VesselProtoMsgData : VesselBaseMsgData
    {
        internal VesselProtoMsgData() { }

        public int NumBytes;
        public byte[] Data = new byte[0];
        public bool ForceReload;

        /// <summary>
        /// Human-readable description of why this vessel update is being sent
        /// (e.g. "EVA construction: new vessel from detached part", "Part decoupled",
        /// "Maneuver nodes changed"). Purely informational; the receiving client surfaces
        /// it in the VesselSyncLog ARRIVED line so post-mortem grep can answer
        /// "why did the originating client send this proto?" without re-reading KSP.log.
        /// Written at the END of the payload so older 0.30.0 peers (which stop reading
        /// before this field) stay forward-compatible; null on incoming messages from
        /// pre-fix peers is treated the same as empty. Ported from upstream
        /// Release/0_29_2 commit 36d06c89 (Drew Banyai).
        /// </summary>
        public string Reason;

        public override VesselMessageType VesselMessageType => VesselMessageType.Proto;

        public override string ClassName { get; } = nameof(VesselProtoMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(ForceReload);
            Common.ThreadSafeCompress(this, ref Data, ref NumBytes);

            lidgrenMsg.Write(NumBytes);
            lidgrenMsg.Write(Data, 0, NumBytes);

            // Backwards-compatible field: must be written LAST so older 0.30.0 peers
            // that don't know about it simply stop reading before this byte range.
            lidgrenMsg.Write(Reason ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            ForceReload = lidgrenMsg.ReadBoolean();

            NumBytes = lidgrenMsg.ReadInt32();
            if (Data.Length < NumBytes)
                Data = new byte[NumBytes];

            lidgrenMsg.ReadBytes(Data, 0, NumBytes);

            Common.ThreadSafeDecompress(this, ref Data, NumBytes, out NumBytes);

            // Backwards-compatible read: older 0.30.0 peers don't send this trailing
            // field. Position-vs-length-bits check on the Lidgren reader is the
            // canonical way to detect a short payload from a legacy peer.
            Reason = lidgrenMsg.Position < lidgrenMsg.LengthBits ? lidgrenMsg.ReadString() : null;
        }

        internal override int InternalGetMessageSize()
        {
            // StringUtil.GetByteCount is a null-safe extension method (returns sizeof(int) on
            // null/empty) — explicit `?? string.Empty` makes that self-documenting.
            return base.InternalGetMessageSize() + sizeof(bool) + sizeof(int) + sizeof(byte) * NumBytes
                + (Reason ?? string.Empty).GetByteCount();
        }
    }
}
