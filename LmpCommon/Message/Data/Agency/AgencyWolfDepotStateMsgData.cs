using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.WolfDepotState"/> (slot 9).
    /// <list type="bullet">
    ///   <item><b>Server → client (owner-only echo + connect catch-up):</b>
    ///        emitted by <c>AgencySystemSender.SendWolfDepotStateToOwner</c>
    ///        after the per-agency router upserts a batch, AND by
    ///        <c>AgencySystemSender.SendWolfDepotCatchupTo</c> wired into
    ///        <c>HandshakeSystem</c>'s channel 22 catch-up sequence (Slice B).
    ///        <see cref="AgencyId"/> carries the receiving client's agency.</item>
    ///   <item><b>Client → server (per-mutation emit from postfix):</b>
    ///        emitted by <c>LmpClient.Systems.Agency.AgencyWolfDepotSender</c>
    ///        from Harmony postfixes on
    ///        <c>WOLF.ScenarioPersister.CreateDepot</c> /
    ///        <c>WOLF.Depot.Establish</c> / <c>Survey</c> /
    ///        <c>NegotiateProvider</c> / <c>NegotiateConsumer</c> (Slice B).
    ///        The server's <c>AgencyMsgReader</c> + <c>AgencyWolfDepotRouter.TryRoute</c>
    ///        IGNORE the wire-supplied <see cref="AgencyId"/> and derive the
    ///        sender's agency from <c>AgencySystem.AgencyByPlayerName</c>.
    ///        Clients cannot spoof attribution.</item>
    /// </list>
    ///
    /// <para><b>Slice A scope:</b> wire shape only. Router dispatch in
    /// <see cref="LmpCommon.Message.Server.AgencySrvMsg.SubTypeDictionary"/> +
    /// <see cref="LmpCommon.Message.Client.AgencyCliMsg.SubTypeDictionary"/>
    /// + <c>AgencyMessageType</c> enum slot 9. Server-side <c>AgencyMsgReader</c>
    /// dispatch + the router itself + sender + projector splice all land in
    /// Slice B.</para>
    ///
    /// <para><b>Privacy rule (spec §10 Q1 — PrivateAgencyResources = true).</b>
    /// WOLF depot state is per-agency private under gate=on. Same shape as
    /// <see cref="AgencyKolonyStateMsgData"/> + <see cref="AgencyContractMsgData"/>.</para>
    ///
    /// <para><b>Wire shape.</b> <see cref="AgencyId"/> (Guid) +
    /// <see cref="EntryCount"/> (int) + <see cref="Entries"/>[0..EntryCount-1]
    /// + forward-compat removal tail (<see cref="RemovedKeyCount"/> +
    /// <see cref="RemovedKeys"/>). Each entry writes via
    /// <see cref="AgencyWolfDepotEntry.Serialize"/>. No QuickLZ compression.</para>
    /// </summary>
    public class AgencyWolfDepotStateMsgData : AgencyBaseMsgData
    {
        internal AgencyWolfDepotStateMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.WolfDepotState;

        public Guid AgencyId;
        public int EntryCount;
        public AgencyWolfDepotEntry[] Entries = new AgencyWolfDepotEntry[0];

        public int RemovedKeyCount;
        public string[] RemovedKeys = new string[0];

        public override string ClassName { get; } = nameof(AgencyWolfDepotStateMsgData);

        /// <summary>
        /// Pre-spec §2.e cap. WOLF depot count per agency is realistically ~tens;
        /// 200 leaves generous headroom while preventing a malicious peer from
        /// forcing a large allocation. Same DoS-amplification class as
        /// <see cref="AgencyKolonyStateMsgData.MaxEntryCount"/>. Bump in Slice
        /// E if soak shows clipping on megabase cohorts.
        /// </summary>
        internal const int MaxEntryCount = 200;

        /// <summary>
        /// Depots aren't normally removed mid-session (no <c>RemoveDepot</c>
        /// method in WOLF's <c>ScenarioPersister</c>); 50 covers the rare
        /// admin-driven migration/cleanup path.
        /// </summary>
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
                    $"AgencyWolfDepotState RemovedKeyCount must be non-negative: {RemovedKeyCount}");
            if (RemovedKeys == null || RemovedKeys.Length < RemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfDepotState RemovedKeyCount {RemovedKeyCount} exceeds RemovedKeys.Length {(RemovedKeys?.Length ?? 0)}");
            if (RemovedKeyCount > MaxRemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfDepotState RemovedKeyCount {RemovedKeyCount} exceeds MaxRemovedKeyCount {MaxRemovedKeyCount} — caller must chunk before send.");
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
                    $"AgencyWolfDepotState EntryCount out of range: {EntryCount} (allowed 0..{MaxEntryCount})");

            if (Entries.Length < EntryCount)
                Entries = new AgencyWolfDepotEntry[EntryCount];

            for (var i = 0; i < EntryCount; i++)
            {
                if (Entries[i] == null)
                    Entries[i] = new AgencyWolfDepotEntry();
                Entries[i].Deserialize(lidgrenMsg);
            }

            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                RemovedKeyCount = lidgrenMsg.ReadInt32();
                if (RemovedKeyCount < 0 || RemovedKeyCount > MaxRemovedKeyCount)
                    throw new System.IO.InvalidDataException(
                        $"AgencyWolfDepotState RemovedKeyCount out of range: {RemovedKeyCount} (allowed 0..{MaxRemovedKeyCount})");
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
