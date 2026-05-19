using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.WolfHopperState"/> (slot 11). Same shape and
    /// privacy contract as <see cref="AgencyWolfDepotStateMsgData"/>.
    /// <para><b>Slice A scope:</b> wire shape only. Slice D wires the router +
    /// projector splice; Slice D client-side postfix on
    /// <c>WOLF.ScenarioPersister.CreateHopper</c> + <c>RemoveHopper</c> emits
    /// this message (the <see cref="RemovedKeys"/> tail is non-trivially used
    /// here because <c>RemoveHopper</c> is a normal-operation WOLF API at
    /// <c>ScenarioPersister.cs:432-440</c>).</para>
    /// </summary>
    public class AgencyWolfHopperStateMsgData : AgencyBaseMsgData
    {
        internal AgencyWolfHopperStateMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.WolfHopperState;

        public Guid AgencyId;
        public int EntryCount;
        public AgencyWolfHopperEntry[] Entries = new AgencyWolfHopperEntry[0];

        public int RemovedKeyCount;
        public string[] RemovedKeys = new string[0];

        public override string ClassName { get; } = nameof(AgencyWolfHopperStateMsgData);

        internal const int MaxEntryCount = 200;
        /// <summary>Symmetric with <see cref="MaxEntryCount"/>: hoppers can be created + removed at parity during normal MKS gameplay (WOLF UI's recipe-change flow calls Remove + Create).</summary>
        internal const int MaxRemovedKeyCount = 200;

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
                    $"AgencyWolfHopperState RemovedKeyCount must be non-negative: {RemovedKeyCount}");
            if (RemovedKeys == null || RemovedKeys.Length < RemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfHopperState RemovedKeyCount {RemovedKeyCount} exceeds RemovedKeys.Length {(RemovedKeys?.Length ?? 0)}");
            if (RemovedKeyCount > MaxRemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfHopperState RemovedKeyCount {RemovedKeyCount} exceeds MaxRemovedKeyCount {MaxRemovedKeyCount} — caller must chunk before send.");
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
                    $"AgencyWolfHopperState EntryCount out of range: {EntryCount} (allowed 0..{MaxEntryCount})");

            if (Entries.Length < EntryCount)
                Entries = new AgencyWolfHopperEntry[EntryCount];

            for (var i = 0; i < EntryCount; i++)
            {
                if (Entries[i] == null)
                    Entries[i] = new AgencyWolfHopperEntry();
                Entries[i].Deserialize(lidgrenMsg);
            }

            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                RemovedKeyCount = lidgrenMsg.ReadInt32();
                if (RemovedKeyCount < 0 || RemovedKeyCount > MaxRemovedKeyCount)
                    throw new System.IO.InvalidDataException(
                        $"AgencyWolfHopperState RemovedKeyCount out of range: {RemovedKeyCount} (allowed 0..{MaxRemovedKeyCount})");
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
