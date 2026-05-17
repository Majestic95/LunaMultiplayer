using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server → client, sent immediately after the LMP handshake reply when per-agency
    /// career mode is active. Tells the client (a) which agency they were auto-registered
    /// or re-bound to via <c>AgencySystem.OnPlayerAuthenticated</c>, and (b) a public
    /// summary of every other agency on the server so the tracking-station UI (Stage
    /// 5.18c) can label other-player vessels.
    ///
    /// Only public fields leak across agencies — no funds / science / reputation in the
    /// summary entries, per spec §10 Q1 (<c>PrivateAgencyResources = true</c>).
    ///
    /// **Cross-channel ordering (Stage 5.18a client author note).** This message ships on
    /// Lidgren channel 22 alongside <see cref="AgencyStateMsgData"/>; the LMP handshake
    /// reply ships on channel 1, <see cref="LmpCommon.Message.Data.PlayerConnection.PlayerConnectionJoinMsgData"/>
    /// ships on channel 17. Lidgren's reliable-ordered guarantee is PER-CHANNEL only —
    /// cross-channel arrival order is undefined. A client handler for this message MUST
    /// NOT depend on state populated by handlers on channels 1 or 17 (no peer list, no
    /// post-LMP-handshake transition state). The local player's identity is the value the
    /// client itself sent in its own <c>HandshakeRequest</c>, available locally — not the
    /// inbound <c>PlayerConnectionJoin</c>.
    ///
    /// **OtherAgencies is a one-shot snapshot** at this player's connect time. Agencies
    /// that come online AFTER will NOT be delivered via this message; Stage 5.18c's
    /// <c>AgencyVisibilityMsgData</c> will fill that gap. Until then, vessels owned by
    /// late-joining agencies render with an unknown-agency label on already-connected
    /// clients — expected behaviour.
    /// </summary>
    public class AgencyHandshakeMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyHandshakeMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.Handshake;

        public Guid AssignedAgencyId;
        public int OtherAgencyCount;
        public AgencyInfo[] OtherAgencies = new AgencyInfo[0];

        public override string ClassName { get; } = nameof(AgencyHandshakeMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            GuidUtil.Serialize(AssignedAgencyId, lidgrenMsg);
            lidgrenMsg.Write(OtherAgencyCount);
            // Caller contract: OtherAgencies[0..OtherAgencyCount-1] must be non-null.
            // No null-skip here — a sender that skipped nulls while emitting the original
            // count would desync with the receiver (which reads OtherAgencyCount entries
            // unconditionally), corrupting subsequent bytes on channel 22. Matches the
            // "trust the caller" convention used by every other *MsgData in this folder;
            // production caller AgencySystemSender.SendHandshakeTo builds the array via
            // others.ToArray() so nulls are not reachable from production code. Round-4
            // review reverted a Round-3 null-skip that produced this wire desync.
            for (var i = 0; i < OtherAgencyCount; i++)
            {
                OtherAgencies[i].Serialize(lidgrenMsg);
            }
        }

        /// <summary>
        /// Upper bound on <see cref="OtherAgencyCount"/> on the wire. Realistic agency counts
        /// are bounded by <c>GeneralSettings.MaxPlayers</c> (typically &lt;=16, hard cap &lt;=128
        /// historically) so 1024 is generous headroom while still preventing an attacker
        /// from forcing an OOM allocation: an unbounded <c>int</c> from the wire lets a
        /// malicious peer ship <c>int.MaxValue</c> and force a 16 GB array allocation.
        /// Round-2 wire review caught this DoS vector.
        /// </summary>
        internal const int MaxOtherAgencyCount = 1024;

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            AssignedAgencyId = GuidUtil.Deserialize(lidgrenMsg);
            OtherAgencyCount = lidgrenMsg.ReadInt32();
            if (OtherAgencyCount < 0 || OtherAgencyCount > MaxOtherAgencyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyHandshake OtherAgencyCount out of range: {OtherAgencyCount} (allowed 0..{MaxOtherAgencyCount})");

            if (OtherAgencies.Length < OtherAgencyCount)
                OtherAgencies = new AgencyInfo[OtherAgencyCount];

            for (var i = 0; i < OtherAgencyCount; i++)
            {
                if (OtherAgencies[i] == null)
                    OtherAgencies[i] = new AgencyInfo();

                OtherAgencies[i].Deserialize(lidgrenMsg);
            }
        }

        internal override int InternalGetMessageSize()
        {
            // Caller contract (see InternalSerialize): OtherAgencies[0..OtherAgencyCount-1]
            // must be non-null. Symmetric with Serialize — if either method tolerated nulls
            // unilaterally, sender/receiver byte counts would diverge and corrupt the channel.
            var arraySize = 0;
            for (var i = 0; i < OtherAgencyCount; i++)
            {
                arraySize += OtherAgencies[i].GetByteCount();
            }

            return base.InternalGetMessageSize() + GuidUtil.ByteSize + sizeof(int) + arraySize;
        }
    }
}
