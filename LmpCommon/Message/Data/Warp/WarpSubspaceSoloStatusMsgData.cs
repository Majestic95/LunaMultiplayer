using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Warp
{
    /// <summary>
    /// Server-to-client notification that a subspace has changed solo-occupancy state. A subspace is
    /// considered "solo" when exactly one connected client is in it. Solo subspaces suppress the
    /// client's TimeSync catch-up — see BUG-001 (docs/research/02-analysis/bug-001-solo-subspace-catchup.md).
    /// Broadcast to all clients on each transition; clients maintain a local map of solo subspaces.
    /// </summary>
    public class WarpSubspaceSoloStatusMsgData : WarpBaseMsgData
    {
        /// <inheritdoc />
        internal WarpSubspaceSoloStatusMsgData() { }
        public override WarpMessageType WarpMessageType => WarpMessageType.SubspaceSoloStatus;

        public int SubspaceId;
        public bool IsSolo;

        public override string ClassName { get; } = nameof(WarpSubspaceSoloStatusMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(SubspaceId);
            lidgrenMsg.Write(IsSolo);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            SubspaceId = lidgrenMsg.ReadInt32();
            IsSolo = lidgrenMsg.ReadBoolean();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + sizeof(int) + sizeof(bool);
        }
    }
}
