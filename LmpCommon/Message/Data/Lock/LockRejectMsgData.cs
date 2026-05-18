using Lidgren.Network;
using LmpCommon.Locks;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Lock
{
    /// <summary>
    /// Server → client unicast. Stage 5.18d slice (c). Tells the originating
    /// client that their <see cref="LockAcquireMsgData"/> was REFUSED, along
    /// with the reason. Today the only reason emitted on the wire is
    /// <see cref="LockRejectReason.CrossAgency"/> (Stage 5.17a guard); other
    /// rejection paths in <c>LockSystem.AcquireLock</c> stay silent as they
    /// did pre-5.18d (cross-subspace past per BUG-005/006, existing-holder
    /// conflict, etc.).
    ///
    /// <para><b>ARRIVAL CONDITIONS (consumer-lens recipe step 7).</b> Arrives
    /// only as a server reply to a client-originated <see cref="LockAcquireMsgData"/>.
    /// Never broadcast; only the originating client receives it. The client's
    /// <c>LockMessageHandler</c> displays a <c>LunaScreenMsg</c> toast and
    /// (for cross-agency) annotates with the owning agency's display name
    /// when its <c>AgencyInfo</c> is known via <c>AgencySystem.OtherAgencies</c>.</para>
    ///
    /// <para><b>CLIENT WRITE PATH (recipe step 7).</b> NONE. Clients never
    /// originate this message; it's a server reply. The <see cref="LmpCommon.Message.Client.LockCliMsg"/>
    /// SubTypeDictionary lists it only for the BUG-010 wire-symmetry rule;
    /// an inbound Reject on the server side is log-dropped.</para>
    ///
    /// <para><b>ORTHOGONAL CONCERNS (recipe step 7).</b> The owning-agency
    /// identity surface is shared with Stage 5.18a's <see cref="AgencyHandshakeMsgData.OtherAgencies"/>
    /// snapshot; the client resolves <see cref="OwningAgencyId"/> against
    /// that cache for the display-name surfacing. No new ownership-state
    /// wire is introduced here. The Stage 5.18d slice (a)
    /// <c>AgencyVisibilityMsgData</c> is the in-session ownership-update
    /// path; this message is purely the per-lock-attempt reject feedback.</para>
    ///
    /// <para><b>Forward-compatibility — future appended fields require an
    /// end-of-message guard.</b> The current schema is Lock + byte + Guid;
    /// any future field added at the tail (e.g. a free-form reason string
    /// for unknown <see cref="LockRejectReason"/> values) MUST be read inside
    /// a <c>lidgrenMsg.Position &lt; lidgrenMsg.LengthBits</c> end-of-message
    /// guard in <see cref="InternalDeserialize"/>. Without the guard, older
    /// peers that lack the field over-read into the next message's bytes,
    /// silently corrupting channel-14 lock state. Same precedent as
    /// <c>VesselProtoMsgData.Reason</c>'s position-check pattern (CLAUDE.md
    /// Stack Notes session 6).</para>
    /// </summary>
    public class LockRejectMsgData : LockBaseMsgData
    {
        /// <inheritdoc />
        internal LockRejectMsgData() { }
        public override LockMessageType LockMessageType => LockMessageType.Reject;

        /// <summary>
        /// The <see cref="LockDefinition"/> the client tried to acquire; carried
        /// back unchanged so the client UI can correlate the reject with the
        /// originating action.
        /// </summary>
        public LockDefinition Lock = new LockDefinition();

        /// <summary>
        /// Wire-stable reject reason. Today the server only ever emits
        /// <see cref="LockRejectReason.CrossAgency"/>; the field is a byte
        /// so it round-trips losslessly via <see cref="byte"/> read/write.
        /// </summary>
        public LockRejectReason Reason;

        /// <summary>
        /// When <see cref="Reason"/> is <see cref="LockRejectReason.CrossAgency"/>,
        /// the <see cref="System.Agency.AgencyState.AgencyId"/> of the vessel's
        /// owning agency. The client looks it up in
        /// <c>AgencySystem.OtherAgencies</c> to render a friendly display
        /// name in the toast ("vessel belongs to Boeing Space Agency"). For
        /// other reasons (future expansion), the field is <see cref="Guid.Empty"/>.
        /// </summary>
        public Guid OwningAgencyId;

        public override string ClassName { get; } = nameof(LockRejectMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            Lock.Serialize(lidgrenMsg);
            lidgrenMsg.Write((byte)Reason);
            GuidUtil.Serialize(OwningAgencyId, lidgrenMsg);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            Lock.Deserialize(lidgrenMsg);
            Reason = (LockRejectReason)lidgrenMsg.ReadByte();
            OwningAgencyId = GuidUtil.Deserialize(lidgrenMsg);
        }

        internal override int InternalGetMessageSize() =>
            base.InternalGetMessageSize() + Lock.GetByteCount() + sizeof(byte) + GuidUtil.ByteSize;
    }
}
