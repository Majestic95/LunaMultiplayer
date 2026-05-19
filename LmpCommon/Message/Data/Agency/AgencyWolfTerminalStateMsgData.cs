using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.WolfTerminalState"/> (slot 12). Same shape
    /// and privacy contract as <see cref="AgencyWolfDepotStateMsgData"/>.
    /// <para><b>Slice A scope:</b> wire shape only. Slice D wires the router +
    /// projector splice; Slice D client-side postfix on
    /// <c>WOLF.ScenarioPersister.CreateTerminal</c> + <c>RemoveTerminal</c>
    /// emits this message (same Remove-is-a-normal-op pattern as Hopper).</para>
    /// </summary>
    public class AgencyWolfTerminalStateMsgData : AgencyBaseMsgData
    {
        internal AgencyWolfTerminalStateMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.WolfTerminalState;

        public Guid AgencyId;
        public int EntryCount;
        public AgencyWolfTerminalEntry[] Entries = new AgencyWolfTerminalEntry[0];

        public int RemovedKeyCount;
        public string[] RemovedKeys = new string[0];

        public override string ClassName { get; } = nameof(AgencyWolfTerminalStateMsgData);

        internal const int MaxEntryCount = 200;
        /// <summary>Symmetric with <see cref="MaxEntryCount"/>: terminals can be created + removed at parity during normal MKS gameplay.</summary>
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
                    $"AgencyWolfTerminalState RemovedKeyCount must be non-negative: {RemovedKeyCount}");
            if (RemovedKeys == null || RemovedKeys.Length < RemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfTerminalState RemovedKeyCount {RemovedKeyCount} exceeds RemovedKeys.Length {(RemovedKeys?.Length ?? 0)}");
            if (RemovedKeyCount > MaxRemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfTerminalState RemovedKeyCount {RemovedKeyCount} exceeds MaxRemovedKeyCount {MaxRemovedKeyCount} — caller must chunk before send.");
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
                    $"AgencyWolfTerminalState EntryCount out of range: {EntryCount} (allowed 0..{MaxEntryCount})");

            if (Entries.Length < EntryCount)
                Entries = new AgencyWolfTerminalEntry[EntryCount];

            for (var i = 0; i < EntryCount; i++)
            {
                if (Entries[i] == null)
                    Entries[i] = new AgencyWolfTerminalEntry();
                Entries[i].Deserialize(lidgrenMsg);
            }

            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                RemovedKeyCount = lidgrenMsg.ReadInt32();
                if (RemovedKeyCount < 0 || RemovedKeyCount > MaxRemovedKeyCount)
                    throw new System.IO.InvalidDataException(
                        $"AgencyWolfTerminalState RemovedKeyCount out of range: {RemovedKeyCount} (allowed 0..{MaxRemovedKeyCount})");
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
