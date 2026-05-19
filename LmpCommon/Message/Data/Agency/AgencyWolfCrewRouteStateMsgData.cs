using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.WolfCrewRouteState"/> (slot 13). Same shape
    /// and privacy contract as <see cref="AgencyWolfDepotStateMsgData"/>, with
    /// the distinctive cross-agency kerbal authority gate enforced in
    /// <c>AgencyWolfCrewRouter.TryRoute</c> per pre-spec §2.b.v.
    ///
    /// <para><b>No <c>RejectionReason</c> field</b> per pre-spec §2.b.v Option C
    /// (locked). Cross-agency kerbal rejections are silent server-side drops
    /// with a Warning log; the client-side prefix on
    /// <c>WOLF_CrewTransferScenario.Launch</c> is the legitimate-client UX path
    /// (pre-spec §8.e). Modified-client desync is structurally acceptable per
    /// pre-spec §8.f.</para>
    ///
    /// <para><b>Slice A scope:</b> wire shape only. Slice E wires the router +
    /// projector splice + cross-agency kerbal authority gate; Slice E client-
    /// side postfixes on <c>CrewRoute.Embark</c> / <c>Disembark</c> /
    /// <c>Launch</c> / <c>CheckArrived</c> + prefix on
    /// <c>WOLF_CrewTransferScenario.Launch</c> emit this message.</para>
    ///
    /// <para><b>Wire shape.</b> <see cref="AgencyId"/> (Guid) +
    /// <see cref="EntryCount"/> (int) + <see cref="Entries"/>[0..EntryCount-1]
    /// + forward-compat removal tail (<see cref="RemovedKeyCount"/> +
    /// <see cref="RemovedKeys"/>). Each entry writes via
    /// <see cref="AgencyWolfCrewRouteEntry.Serialize"/> including the nested
    /// <see cref="AgencyWolfCrewRouteEntry.Passengers"/> list. No QuickLZ
    /// compression — entries are heavier than depots (nested passengers)
    /// but still bounded.</para>
    /// </summary>
    public class AgencyWolfCrewRouteStateMsgData : AgencyBaseMsgData
    {
        internal AgencyWolfCrewRouteStateMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.WolfCrewRouteState;

        public Guid AgencyId;
        public int EntryCount;
        public AgencyWolfCrewRouteEntry[] Entries = new AgencyWolfCrewRouteEntry[0];

        public int RemovedKeyCount;
        public string[] RemovedKeys = new string[0];

        public override string ClassName { get; } = nameof(AgencyWolfCrewRouteStateMsgData);

        /// <summary>
        /// Lower cap than the other 4 WOLF MsgData types (per pre-spec §2.e)
        /// because CrewRoute entries carry the nested
        /// <see cref="AgencyWolfCrewRouteEntry.Passengers"/> list and are
        /// heavier on the wire. Realistic CrewRoute count per agency is ~5-20;
        /// 100 leaves generous headroom.
        /// </summary>
        internal const int MaxEntryCount = 100;
        internal const int MaxRemovedKeyCount = 50;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            GuidUtil.Serialize(AgencyId, lidgrenMsg);
            lidgrenMsg.Write(EntryCount);
            for (var i = 0; i < EntryCount; i++)
            {
                Entries[i].Serialize(lidgrenMsg);
            }

            if (RemovedKeyCount < 0)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfCrewRouteState RemovedKeyCount must be non-negative: {RemovedKeyCount}");
            if (RemovedKeys == null || RemovedKeys.Length < RemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfCrewRouteState RemovedKeyCount {RemovedKeyCount} exceeds RemovedKeys.Length {(RemovedKeys?.Length ?? 0)}");
            if (RemovedKeyCount > MaxRemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfCrewRouteState RemovedKeyCount {RemovedKeyCount} exceeds MaxRemovedKeyCount {MaxRemovedKeyCount} — caller must chunk before send.");
            lidgrenMsg.Write(RemovedKeyCount);
            for (var i = 0; i < RemovedKeyCount; i++)
            {
                lidgrenMsg.Write(RemovedKeys[i] ?? string.Empty);
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            AgencyId = GuidUtil.Deserialize(lidgrenMsg);
            EntryCount = lidgrenMsg.ReadInt32();
            if (EntryCount < 0 || EntryCount > MaxEntryCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfCrewRouteState EntryCount out of range: {EntryCount} (allowed 0..{MaxEntryCount})");

            if (Entries.Length < EntryCount)
                Entries = new AgencyWolfCrewRouteEntry[EntryCount];

            for (var i = 0; i < EntryCount; i++)
            {
                if (Entries[i] == null)
                    Entries[i] = new AgencyWolfCrewRouteEntry();
                Entries[i].Deserialize(lidgrenMsg);
            }

            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                RemovedKeyCount = lidgrenMsg.ReadInt32();
                if (RemovedKeyCount < 0 || RemovedKeyCount > MaxRemovedKeyCount)
                    throw new System.IO.InvalidDataException(
                        $"AgencyWolfCrewRouteState RemovedKeyCount out of range: {RemovedKeyCount} (allowed 0..{MaxRemovedKeyCount})");
                if (RemovedKeys.Length < RemovedKeyCount)
                    RemovedKeys = new string[RemovedKeyCount];
                for (var i = 0; i < RemovedKeyCount; i++)
                {
                    RemovedKeys[i] = lidgrenMsg.ReadString();
                }
            }
            else
            {
                RemovedKeyCount = 0;
                RemovedKeys = new string[0];
            }
        }

        internal override int InternalGetMessageSize()
        {
            var arraySize = 0;
            for (var i = 0; i < EntryCount; i++)
            {
                arraySize += Entries[i].GetByteCount();
            }

            var removedTailSize = sizeof(int);
            for (var i = 0; i < RemovedKeyCount; i++)
            {
                removedTailSize += 5 + (RemovedKeys[i]?.Length ?? 0) * 4;
            }

            return base.InternalGetMessageSize() + GuidUtil.ByteSize + sizeof(int) + arraySize + removedTailSize;
        }
    }
}
