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
    /// <para><b>Bidirectional contract (mirrors Slice B/C/D template):</b>
    /// <list type="bullet">
    ///   <item><b>Server → client (owner-only echo + connect catch-up).</b>
    ///         Echo emitted from <c>AgencySystemSender.SendWolfCrewRouteStateToOwner</c>
    ///         after every successful router upsert. Connect catch-up emitted
    ///         from <c>AgencySystemSender.SendWolfCrewRouteCatchupTo</c> at
    ///         handshake completion (last in the depots → routes → hoppers →
    ///         terminals → crew-routes catchup chain — CrewRoutes carry
    ///         origin+destination FK to depots, depots must arrive first).
    ///         REPLACE semantics on receive — the catchup batch is the
    ///         authoritative snapshot at handshake time; the client mirror
    ///         (when wired) clears the local CrewRoutes dict and rebuilds
    ///         from the batch.</item>
    ///   <item><b>Client → server (per-mutation emit from postfix).</b>
    ///         Emitted from <c>AgencyWolfCrewRouteSender.SendMutation</c>
    ///         when WOLF's <c>CrewRoute.Embark</c> / <c>Disembark</c> /
    ///         <c>Launch</c> / <c>CheckArrived</c> /
    ///         <c>ScenarioPersister.CreateCrewRoute</c> Harmony postfixes
    ///         fire under <c>PerAgencyCareerEnabled=true</c>. Server trust
    ///         posture: <see cref="AgencyId"/> on inbound is IGNORED — the
    ///         server derives the sender's authoritative agency from
    ///         <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>.
    ///         A C→S inbound that lies about AgencyId routes to the
    ///         player's actual agency, not the wire-claimed one.</item>
    /// </list></para>
    ///
    /// <para><b>Privacy rule (spec §10 Q1 PrivateAgencyResources=true).</b>
    /// Peers never receive another agency's crew-route batch. Cross-agency
    /// awareness leaks ONLY through the projector splice
    /// (<c>AgencyScenarioProjector.SpliceAgencyWolfState</c>) which projects
    /// per-target-client at scene-load time.</para>
    ///
    /// <para><b>Upsert key + REPLACE semantics.</b> Partition key is
    /// <see cref="AgencyWolfCrewRouteEntry.UniqueId"/> (Guid in
    /// <c>ToString("N")</c> form per WOLF's <c>CrewRoute.cs:90</c>). Server-
    /// side router does last-write-wins upsert. No per-field delta state
    /// machine — every emit carries the full route snapshot. The
    /// <see cref="RemovedKeys"/> tail is RESERVED for Slice F admin /
    /// migration paths (deleteagency cascade, transferagency MKS-aware
    /// companion) — WOLF has no normal-op <c>RemoveCrewRoute</c> API
    /// (verified s41 source walk against MKS SHA <c>ed0f6aa6</c> at
    /// <c>ScenarioPersister.cs:432-449</c> — only <c>RemoveHopper</c> +
    /// <c>RemoveTerminal</c> exist).</para>
    ///
    /// <para><b>No <c>RejectionReason</c> field</b> per pre-spec §2.b.v Option C
    /// (locked). Cross-agency kerbal rejections are silent server-side drops
    /// with a Warning log. The client UI flap window between rejection and
    /// the projector's next <c>SendScenarioModules</c> tick (~30s cadence)
    /// is the documented operator-visible artifact: the operator's local
    /// WOLF UI shows the cross-agency passenger as embarked until the
    /// projector overwrites; then the passenger silently vanishes from the
    /// route. Pre-spec §8.f acceptable desync. The deferred legitimate-
    /// client UX surface is a Slice F prefix on
    /// <c>WOLF_CrewTransferScenario.Launch</c> that suppresses the
    /// optimistic UI mutation at click time.</para>
    ///
    /// <para><b>Orthogonal concerns:</b>
    /// <list type="bullet">
    ///   <item><b>Mid-flight kerbal stranding</b> — passengers in
    ///         <c>RosterStatus.Missing</c> aboard a Boarding/Enroute
    ///         CrewRoute that's stripped via Slice F deleteagency cascade
    ///         (or a pre-0.31 upgrade strip) get permanently orphaned.
    ///         <c>AgencySystem.WarnAboutSharedWolfOnUpgrade</c> warns
    ///         operators at boot.</item>
    ///   <item><b>Admin /setvesselagency on an in-flight kerbal</b> — a NO-OP
    ///         for the CrewRoute's passenger record (the list is fixed at
    ///         <c>CreateCrewRoute</c> time per WOLF source contract). The
    ///         kerbal cannot be moved to another agency until the route
    ///         reaches Arrived and the passenger Disembarks.</item>
    /// </list></para>
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
