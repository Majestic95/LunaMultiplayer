using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server → client. Acknowledges an <see cref="AgencyCreateRequestMsgData"/>. On
    /// success the server has applied the new DisplayName to the agency, persisted via
    /// <c>AgencySystem.SaveAgency</c>, and broadcast the update; on failure it returns
    /// the rejection reason and the previous DisplayName so the client can resync its
    /// local view.
    ///
    /// **Failure semantics.** <see cref="AgencyId"/> is <see cref="System.Guid.Empty"/>
    /// if and only if <see cref="Success"/> is false. Clients SHOULD check
    /// <see cref="Success"/> first; checking <c>AgencyId != Guid.Empty</c> is sound
    /// today (RegisterAgency mints via <c>Guid.NewGuid</c> which is collision-free
    /// versus Empty) but is an implicit contract. Reasons the server may reject:
    /// validation failure (empty / too long / illegal characters) or the gate-off
    /// case <c>"Per-agency career disabled on this server (PerAgencyCareer=false)"</c>
    /// (added round-5 to close the silent-no-reply UX gap for buggy clients shipping
    /// CreateRequest under gate=off).
    ///
    /// **Same-agency-id invariant.** On success, <see cref="AgencyId"/> equals the
    /// <see cref="AgencyHandshakeMsgData.AssignedAgencyId"/> previously sent to this
    /// client. CreateRequest is a rename-on-connect, not a mint — see the
    /// <see cref="AgencyCreateRequestMsgData"/> XML for the full contract.
    /// </summary>
    public class AgencyCreateReplyMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyCreateReplyMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CreateReply;

        public Guid AgencyId;
        public string DisplayName = string.Empty;
        public bool Success;
        public string Reason = string.Empty;

        public override string ClassName { get; } = nameof(AgencyCreateReplyMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            GuidUtil.Serialize(AgencyId, lidgrenMsg);
            lidgrenMsg.Write(DisplayName ?? string.Empty);
            lidgrenMsg.Write(Success);
            lidgrenMsg.Write(Reason ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            AgencyId = GuidUtil.Deserialize(lidgrenMsg);
            DisplayName = ReadBoundedString(lidgrenMsg, nameof(DisplayName));
            Success = lidgrenMsg.ReadBoolean();
            Reason = ReadBoundedString(lidgrenMsg, nameof(Reason));
        }

        /// <summary>
        /// Same bounded-read rationale as <c>AgencyStateMsgData.ReadBoundedString</c> — CreateReply
        /// is server-→-client but listed in the CliMsg dictionary for wire-symmetry, so a misrouted
        /// inbound is reachable and the deserialize allocation happens before the AgencyMsgReader
        /// log-drop fires. Cap each string at <see cref="MaxStringByteLength"/> to prevent allocation
        /// amplification.
        /// </summary>
        private static string ReadBoundedString(NetIncomingMessage lidgrenMsg, string fieldName)
        {
            var byteLength = (int)lidgrenMsg.ReadVariableUInt32();
            if (byteLength < 0 || byteLength > MaxStringByteLength)
                throw new System.IO.InvalidDataException(
                    $"AgencyCreateReply.{fieldName} byte length out of range: {byteLength} (allowed 0..{MaxStringByteLength})");
            if (byteLength == 0)
                return string.Empty;
            return System.Text.Encoding.UTF8.GetString(lidgrenMsg.ReadBytes(byteLength));
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize()
                + GuidUtil.ByteSize
                + DisplayName.GetByteCount()
                + sizeof(bool)
                + Reason.GetByteCount();
        }
    }
}
