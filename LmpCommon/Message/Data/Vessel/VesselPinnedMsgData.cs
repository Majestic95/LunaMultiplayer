using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Vessel
{
    /// <summary>
    /// BUG-010: broadcast server → all remaining clients when a player disconnects, naming
    /// each vessel that was under the leaving player's locks. Recipients pin the vessel
    /// immortal until either the original pilot reconnects (re-acquires Control/Update)
    /// or another player explicitly takes the helm. See
    /// docs/research/02-analysis/bug-010-disconnect-vessel-handoff.md.
    /// </summary>
    public class VesselPinnedMsgData : VesselBaseMsgData
    {
        internal VesselPinnedMsgData() { }
        public override VesselMessageType VesselMessageType => VesselMessageType.Pinned;

        public string AbsentPlayerName;
        public string Reason;

        public override string ClassName { get; } = nameof(VesselPinnedMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            lidgrenMsg.Write(AbsentPlayerName ?? string.Empty);
            lidgrenMsg.Write(Reason ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            AbsentPlayerName = lidgrenMsg.ReadString();
            Reason = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize()
                + (AbsentPlayerName ?? string.Empty).GetByteCount()
                + (Reason ?? string.Empty).GetByteCount();
        }
    }
}
