using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Server.Base;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Server
{
    /// <summary>
    /// Server → client wire container for per-agency career messages (Stage 5).
    /// Subtypes are listed in <see cref="AgencyMessageType"/>; this dictionary MUST be
    /// kept in lockstep with <see cref="LmpCommon.Message.Client.AgencyCliMsg.SubTypeDictionary"/>
    /// — same wire-symmetry gotcha that bit BUG-010 (an unknown subtype causes
    /// <c>MessageBase.GetMessageData</c> to throw and the receiver silently drops the
    /// payload). Add entries here AND on the Cli side together.
    ///
    /// Channel 22 — picked one slot above the highest existing server channel
    /// (ShareProgressSrvMsg at 21). Per-direction channel space is independent of the
    /// client side (which uses 21). Picking a fresh channel keeps Agency traffic out
    /// of the head-of-line ordering of unrelated systems (Facility, Screenshot).
    /// ReliableOrdered: registration messages must arrive in order (CreateReply must
    /// not overtake the implied handshake-time state).
    /// </summary>
    public class AgencySrvMsg : SrvMsgBase<AgencyBaseMsgData>
    {
        /// <inheritdoc />
        internal AgencySrvMsg() { }

        /// <inheritdoc />
        public override string ClassName { get; } = nameof(AgencySrvMsg);

        /// <inheritdoc />
        /// <remarks>
        /// **Wire-protocol invariant — never reorder or renumber existing entries.** The
        /// ushort keys are the on-wire subtype bytes; a reordering would silently break
        /// every connected 0.31.0 client. New entries APPEND to the end of
        /// <see cref="AgencyMessageType"/> + this dictionary. Must stay in lockstep with
        /// <see cref="LmpCommon.Message.Client.AgencyCliMsg"/> per the BUG-010 wire-
        /// symmetry rule — missing-on-one-side causes silent receive-drop.
        /// </remarks>
        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)AgencyMessageType.Handshake] = typeof(AgencyHandshakeMsgData),
            [(ushort)AgencyMessageType.CreateRequest] = typeof(AgencyCreateRequestMsgData),
            [(ushort)AgencyMessageType.CreateReply] = typeof(AgencyCreateReplyMsgData),
            [(ushort)AgencyMessageType.State] = typeof(AgencyStateMsgData),
            [(ushort)AgencyMessageType.Contract] = typeof(AgencyContractMsgData),
            [(ushort)AgencyMessageType.Visibility] = typeof(AgencyVisibilityMsgData),
        };

        public override ServerMessageType MessageType => ServerMessageType.Agency;

        protected override int DefaultChannel => 22;

        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableOrdered;
    }
}
