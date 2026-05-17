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
            for (var i = 0; i < OtherAgencyCount; i++)
            {
                OtherAgencies[i].Serialize(lidgrenMsg);
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            AssignedAgencyId = GuidUtil.Deserialize(lidgrenMsg);
            OtherAgencyCount = lidgrenMsg.ReadInt32();
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
            var arraySize = 0;
            for (var i = 0; i < OtherAgencyCount; i++)
            {
                arraySize += OtherAgencies[i].GetByteCount();
            }

            return base.InternalGetMessageSize() + GuidUtil.ByteSize + sizeof(int) + arraySize;
        }
    }
}
