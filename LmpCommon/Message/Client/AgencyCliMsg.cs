using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Client.Base;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Client
{
    /// <summary>
    /// Client → server wire container for per-agency career messages (Stage 5).
    /// <see cref="SubTypeDictionary"/> mirrors <see cref="LmpCommon.Message.Server.AgencySrvMsg"/>
    /// — keep them in lockstep (BUG-010 wire-symmetry rule). Even subtypes that are
    /// nominally server-→-client only (Handshake, CreateReply, State) are listed here
    /// so the Lidgren deserializer doesn't reject a misrouted message; the message
    /// validator in the server handler decides whether the direction is legal.
    ///
    /// Channel 21 — one slot above the highest existing client channel
    /// (ShareProgressCliMsg at 20). Per-direction independent of the server side
    /// (which uses 22). Picking a fresh channel keeps Agency traffic out of the
    /// head-of-line ordering of Screenshot (channel 19) and ShareProgress.
    /// </summary>
    public class AgencyCliMsg : CliMsgBase<AgencyBaseMsgData>
    {
        /// <inheritdoc />
        internal AgencyCliMsg() { }

        /// <inheritdoc />
        public override string ClassName { get; } = nameof(AgencyCliMsg);

        /// <inheritdoc />
        /// <remarks>
        /// **Wire-protocol invariant — never reorder or renumber existing entries.** The
        /// ushort keys are the on-wire subtype bytes; a reordering would silently break
        /// every connected 0.31.0 server. New entries APPEND to the end of
        /// <see cref="AgencyMessageType"/> + this dictionary. Must stay in lockstep with
        /// <see cref="LmpCommon.Message.Server.AgencySrvMsg"/> per the BUG-010 wire-
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
            // [Phase 3 Slice B] MUST stay in lockstep with AgencySrvMsg.cs — BUG-010 wire-symmetry rule.
            [(ushort)AgencyMessageType.KolonyState] = typeof(AgencyKolonyStateMsgData),
            // [Phase 3 Slice C] MUST stay in lockstep with AgencySrvMsg.cs — BUG-010 wire-symmetry rule.
            [(ushort)AgencyMessageType.PlanetaryState] = typeof(AgencyPlanetaryStateMsgData),
            // [Phase 3 Slice D] MUST stay in lockstep with AgencySrvMsg.cs — BUG-010 wire-symmetry rule.
            [(ushort)AgencyMessageType.OrbitalState] = typeof(AgencyOrbitalStateMsgData),
        };

        public override ClientMessageType MessageType => ClientMessageType.Agency;

        protected override int DefaultChannel => 21;

        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableOrdered;
    }
}
