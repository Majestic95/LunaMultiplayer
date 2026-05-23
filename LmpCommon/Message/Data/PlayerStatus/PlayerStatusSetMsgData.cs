using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.PlayerStatus
{
    public class PlayerStatusSetMsgData : PlayerStatusBaseMsgData
    {
        /// <inheritdoc />
        internal PlayerStatusSetMsgData() { }
        public override PlayerStatusMessageType PlayerStatusMessageType => PlayerStatusMessageType.Set;

        public PlayerStatusInfo PlayerStatus = new PlayerStatusInfo();

        /// <summary>
        /// Phase 1 of server-side-offload (docs/research/11-server-side-offload-spec.md
        /// §3.b/c). Tail-byte at message-terminal position; backward-read-compat with
        /// pre-Phase-1 peers via the established tail-bit-read pattern (mirror of
        /// <see cref="Settings.SettingsReplyMsgData.PerAgencyCareerEnabled"/>).
        ///
        /// Lives HERE, not inside <see cref="PlayerStatusInfo"/>, because PlayerStatusInfo
        /// is embedded as an unframed array inside PlayerStatusReplyMsgData — a tail-bit-
        /// read there reads INTO the next element's payload (caught by the upgrade-lens
        /// review of Phase 1's initial implementation). Set messages carry a single
        /// PlayerStatusInfo + Scene at the message tail; safe. Reply messages don't
        /// carry Scene at all (the joining client doesn't need to know other players'
        /// scenes — that's server-side filter state). Each peer's scene reaches the
        /// joining client via that peer's own next PlayerStatusSet broadcast.
        ///
        /// Pre-Phase-1 client doesn't ship the byte → server reads tail-or-default
        /// <see cref="ClientSceneType.Unknown"/> → MessageQueuer.ShouldRelayToScene
        /// treats Unknown as "relay always" → compat preserved.
        /// </summary>
        public ClientSceneType Scene = ClientSceneType.Unknown;

        public override string ClassName { get; } = nameof(PlayerStatusSetMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            PlayerStatus.Serialize(lidgrenMsg);
            //Phase 1 tail-byte at message-terminal position. New clients always write
            //it; pre-Phase-1 servers ignore the trailing byte (Lidgren's deserializer
            //stops at the last field it knows about).
            lidgrenMsg.Write((byte)Scene);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            PlayerStatus.Deserialize(lidgrenMsg);
            //Phase 1 tail-bit-read guard. Pre-Phase-1 client (no Scene byte on the
            //wire) defaults to Unknown. Position-in-bits-vs-length-in-bits is the
            //canonical guard (see SettingsReplyMsgData.cs:244 PerAgencyCareerEnabled).
            Scene = lidgrenMsg.Position < lidgrenMsg.LengthBits
                ? (ClientSceneType)lidgrenMsg.ReadByte()
                : ClientSceneType.Unknown;
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + PlayerStatus.GetByteCount() + sizeof(byte);
        }
    }
}
