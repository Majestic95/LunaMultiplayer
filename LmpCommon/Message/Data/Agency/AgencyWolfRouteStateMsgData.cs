using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.WolfRouteState"/> (slot 10).
    /// <list type="bullet">
    ///   <item><b>Server → client (owner-only echo + connect catch-up):</b>
    ///        emitted by <c>AgencySystemSender.SendWolfRouteStateToOwner</c>
    ///        after the per-agency router upserts a batch, AND by
    ///        <c>AgencySystemSender.SendWolfRouteCatchupTo</c> wired into
    ///        <c>HandshakeSystem</c>'s channel 22 catch-up sequence
    ///        immediately after the depot catchup (depots-then-routes
    ///        ordering mirrors WOLF's OnLoad invariant — Slice C).
    ///        <see cref="AgencyId"/> carries the receiving client's agency.</item>
    ///   <item><b>Client → server (per-mutation emit from postfix):</b>
    ///        emitted by <c>LmpClient.Systems.Agency.AgencyWolfRouteSender</c>
    ///        from Harmony postfixes on
    ///        <c>WOLF.ScenarioPersister.CreateRoute</c> +
    ///        <c>WOLF.Route.AddResource</c> + <c>RemoveResource</c>
    ///        (Slice C). The server's <c>AgencyMsgReader</c> +
    ///        <c>AgencyWolfRouteRouter.TryRoute</c> IGNORE the wire-supplied
    ///        <see cref="AgencyId"/> and derive the sender's agency from
    ///        <c>AgencySystem.AgencyByPlayerName</c>. Clients cannot spoof
    ///        attribution.</item>
    /// </list>
    ///
    /// <para><b>REPLACE semantics on receive.</b> Each arrival of this
    /// message — both per-mutation echo and catch-up — is idempotent: the
    /// receiver upserts the full per-entry snapshot under the composite
    /// key <c>$"{OriginBody}|{OriginBiome}|{DestinationBody}|{DestinationBiome}"</c>.
    /// A future client mirror landing the apply path should mirror this
    /// posture (no per-field delta state machine — last-write-wins). The
    /// optional <see cref="RemovedKeyCount"/> + <see cref="RemovedKeys"/>
    /// tail is the explicit-removal channel; reserved for admin / migration
    /// paths in Slice F (no normal-op route removal in WOLF — see
    /// <c>AgencyWolfRouteRouter</c> XML).</para>
    ///
    /// <para><b>Orthogonal concerns (NOT carried on this message).</b>
    /// Per-Route <c>_resources</c> (the in-flight allocation dict) IS
    /// carried in <see cref="AgencyWolfRouteEntry.Resources"/>. Depot-side
    /// <c>ResourceStreams</c> (the per-depot Incoming/Outgoing aggregation)
    /// flows separately through <see cref="AgencyWolfDepotStateMsgData"/>.
    /// A future client mirror MUST NOT cross-populate WOLF depot state
    /// from this message's Resources list — they're semantically
    /// distinct.</para>
    ///
    /// <para><b>Privacy rule (spec §10 Q1 — PrivateAgencyResources = true).</b>
    /// WOLF route state is per-agency private under gate=on. Same shape as
    /// <see cref="AgencyKolonyStateMsgData"/> + <see cref="AgencyWolfDepotStateMsgData"/>.</para>
    ///
    /// <para><b>Wire shape.</b> <see cref="AgencyId"/> (Guid) +
    /// <see cref="EntryCount"/> (int) + <see cref="Entries"/>[0..EntryCount-1]
    /// + forward-compat removal tail (<see cref="RemovedKeyCount"/> +
    /// <see cref="RemovedKeys"/>). Each entry writes via
    /// <see cref="AgencyWolfRouteEntry.Serialize"/>. No QuickLZ
    /// compression.</para>
    /// </summary>
    public class AgencyWolfRouteStateMsgData : AgencyBaseMsgData
    {
        internal AgencyWolfRouteStateMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.WolfRouteState;

        public Guid AgencyId;
        public int EntryCount;
        public AgencyWolfRouteEntry[] Entries = new AgencyWolfRouteEntry[0];

        public int RemovedKeyCount;
        public string[] RemovedKeys = new string[0];

        public override string ClassName { get; } = nameof(AgencyWolfRouteStateMsgData);

        internal const int MaxEntryCount = 200;
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
                    $"AgencyWolfRouteState RemovedKeyCount must be non-negative: {RemovedKeyCount}");
            if (RemovedKeys == null || RemovedKeys.Length < RemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfRouteState RemovedKeyCount {RemovedKeyCount} exceeds RemovedKeys.Length {(RemovedKeys?.Length ?? 0)}");
            if (RemovedKeyCount > MaxRemovedKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfRouteState RemovedKeyCount {RemovedKeyCount} exceeds MaxRemovedKeyCount {MaxRemovedKeyCount} — caller must chunk before send.");
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
                    $"AgencyWolfRouteState EntryCount out of range: {EntryCount} (allowed 0..{MaxEntryCount})");

            if (Entries.Length < EntryCount)
                Entries = new AgencyWolfRouteEntry[EntryCount];

            for (var i = 0; i < EntryCount; i++)
            {
                if (Entries[i] == null)
                    Entries[i] = new AgencyWolfRouteEntry();
                Entries[i].Deserialize(lidgrenMsg);
            }

            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                RemovedKeyCount = lidgrenMsg.ReadInt32();
                if (RemovedKeyCount < 0 || RemovedKeyCount > MaxRemovedKeyCount)
                    throw new System.IO.InvalidDataException(
                        $"AgencyWolfRouteState RemovedKeyCount out of range: {RemovedKeyCount} (allowed 0..{MaxRemovedKeyCount})");
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
