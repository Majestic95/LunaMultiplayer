using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server.Base;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Server
{
    public class VesselSrvMsg : SrvMsgBase<VesselBaseMsgData>
    {
        /// <inheritdoc />
        internal VesselSrvMsg() { }

        /// <inheritdoc />
        public override string ClassName { get; } = nameof(VesselSrvMsg);

        /// <inheritdoc />
        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)VesselMessageType.Proto] = typeof(VesselProtoMsgData),
            [(ushort)VesselMessageType.Remove] = typeof(VesselRemoveMsgData),
            [(ushort)VesselMessageType.Position] = typeof(VesselPositionMsgData),
            [(ushort)VesselMessageType.Flightstate] = typeof(VesselFlightStateMsgData),
            [(ushort)VesselMessageType.Update] = typeof(VesselUpdateMsgData),
            [(ushort)VesselMessageType.Resource] = typeof(VesselResourceMsgData),
            [(ushort)VesselMessageType.PartSyncField] = typeof(VesselPartSyncFieldMsgData),
            [(ushort)VesselMessageType.PartSyncUiField] = typeof(VesselPartSyncUiFieldMsgData),
            [(ushort)VesselMessageType.PartSyncCall] = typeof(VesselPartSyncCallMsgData),
            [(ushort)VesselMessageType.ActionGroup] = typeof(VesselActionGroupMsgData),
            [(ushort)VesselMessageType.Fairing] = typeof(VesselFairingMsgData),
            [(ushort)VesselMessageType.Decouple] = typeof(VesselDecoupleMsgData),
            [(ushort)VesselMessageType.Couple] = typeof(VesselCoupleMsgData),
            [(ushort)VesselMessageType.Undock] = typeof(VesselUndockMsgData),
            [(ushort)VesselMessageType.Pinned] = typeof(VesselPinnedMsgData),
        };

        public override ServerMessageType MessageType => ServerMessageType.Vessel;
        protected override int DefaultChannel => IsUnreliableMessage() ? 0 : IsPinnedMessage() ? 14 : 8;
        public override NetDeliveryMethod NetDeliveryMethod => IsUnreliableMessage() ?
            NetDeliveryMethod.UnreliableSequenced : NetDeliveryMethod.ReliableOrdered;

        private bool IsUnreliableMessage()
        {
            return Data.SubType == (ushort)VesselMessageType.Position || Data.SubType == (ushort)VesselMessageType.Flightstate
                   || Data.SubType == (ushort)VesselMessageType.Update || Data.SubType == (ushort)VesselMessageType.Resource;
        }

        //BUG-010: pin must arrive at the client BEFORE the lock-release storm so the
        //downstream SetImmortalStateBasedOnLock flip is suppressed in time. Lidgren's
        //reliable-ordered guarantee is per-channel; LockSrvMsg rides channel 14, so the
        //Pinned subtype rides the same channel to share its ordering queue. All other
        //vessel-subsystem messages stay on the default vessel channel 8.
        private bool IsPinnedMessage() => Data.SubType == (ushort)VesselMessageType.Pinned;
    }
}