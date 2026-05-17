using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server → client. Carries the canonical state of ONE agency. Sent:
    /// - on player auth (the assigned agency's full state, so the client's local
    ///   mirror starts populated),
    /// - on any private-field mutation that the local player is allowed to observe.
    ///
    /// Other players never receive this for an agency that isn't theirs — funds /
    /// science / reputation are private per spec §10 Q1. Tracking-station labels
    /// (public name + owner) flow through <see cref="AgencyHandshakeMsgData"/> /
    /// future <c>AgencyVisibilityMsgData</c>, not through this private-state envelope.
    ///
    /// **Channel ordering (Stage 5.18a client author note).** Ships on Lidgren channel
    /// 22 (same as <see cref="AgencyHandshakeMsgData"/>). Lidgren's reliable-ordered
    /// guarantee applies WITHIN this channel, so when the server sends Handshake then
    /// State (as <c>HandshakeSystem</c> does on auth), the client MAY assume the
    /// Handshake has arrived by the time State is being processed and that
    /// <see cref="AgencyId"/> matches the prior Handshake's <c>AssignedAgencyId</c>.
    /// Defensive client code may still verify, but the wire contract guarantees order
    /// on this channel.
    ///
    /// **Defensive client filtering (Stage 5.18a hardening).** The Stage 5.18a client
    /// mirror SHOULD discard any inbound State whose <see cref="AgencyId"/> does not
    /// match the local player's assigned agency. The server never legitimately ships
    /// another agency's State to a non-owner (spec §10 Q1); a State arriving for a
    /// different agency indicates either a server bug or wire corruption — drop, don't
    /// cache.
    ///
    /// Field set today mirrors <c>Server/System/Agency/AgencyState.cs</c> scalar
    /// surface from Stage 5.14c. Tech tree / facilities / kerbals / contracts /
    /// strategies / world-firsts join when their underlying data model lands.
    /// Lidgren binary deserialization is strictly positional, so any future field
    /// addition needs either (a) a protocol-version bump, or (b) appending at the
    /// message tail with a <c>lidgrenMsg.Position &lt; lidgrenMsg.LengthBits</c>
    /// end-of-message-guarded read (the pattern used by `VesselProtoMsgData.Reason`
    /// for the Stage 4 Strategy-B port). The doc-claim of "additive forward-compatible"
    /// is only true under (b); raw appended fields without the position guard would
    /// over-read against an older deserializer.
    /// </summary>
    public class AgencyStateMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyStateMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.State;

        public Guid AgencyId;
        public string OwningPlayerName = string.Empty;
        public string DisplayName = string.Empty;
        public double Funds;
        public double Science;
        public double Reputation;

        public override string ClassName { get; } = nameof(AgencyStateMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            GuidUtil.Serialize(AgencyId, lidgrenMsg);
            lidgrenMsg.Write(OwningPlayerName ?? string.Empty);
            lidgrenMsg.Write(DisplayName ?? string.Empty);
            lidgrenMsg.Write(Funds);
            lidgrenMsg.Write(Science);
            lidgrenMsg.Write(Reputation);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            AgencyId = GuidUtil.Deserialize(lidgrenMsg);
            OwningPlayerName = ReadBoundedString(lidgrenMsg, nameof(OwningPlayerName));
            DisplayName = ReadBoundedString(lidgrenMsg, nameof(DisplayName));
            Funds = lidgrenMsg.ReadDouble();
            Science = lidgrenMsg.ReadDouble();
            Reputation = lidgrenMsg.ReadDouble();
        }

        /// <summary>
        /// Bounded ReadString wrapper. Lidgren peer-cap rules prevent oversized messages, but
        /// a single string field inside a small message can still be inflated to ~MTU size
        /// (~64 KB). State is a server-→-client subtype but its dictionary entry on the Cli
        /// side (for wire-symmetry) means a misrouted/malicious inbound is reachable —
        /// AgencyMsgReader log-drops the subtype but the deserialize allocation already
        /// happened. Round-3 wire review: cap each string at MaxStringByteLength (much
        /// larger than any legitimate value but small enough to make 100 of these per
        /// second uninteresting).
        /// </summary>
        private static string ReadBoundedString(NetIncomingMessage lidgrenMsg, string fieldName)
        {
            var byteLength = (int)lidgrenMsg.ReadVariableUInt32();
            if (byteLength < 0 || byteLength > MaxStringByteLength)
                throw new System.IO.InvalidDataException(
                    $"AgencyState.{fieldName} byte length out of range: {byteLength} (allowed 0..{MaxStringByteLength})");
            if (byteLength == 0)
                return string.Empty;
            return System.Text.Encoding.UTF8.GetString(lidgrenMsg.ReadBytes(byteLength));
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize()
                + GuidUtil.ByteSize
                + OwningPlayerName.GetByteCount()
                + DisplayName.GetByteCount()
                + sizeof(double) * 3;
        }
    }
}
