using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.WolfHopperState"/> (slot 11).
    /// <list type="bullet">
    ///   <item><b>Server → client (owner-only echo + connect catch-up):</b>
    ///        emitted by <c>AgencySystemSender.SendWolfHopperStateToOwner</c>
    ///        after the per-agency router upserts a batch, AND by
    ///        <c>AgencySystemSender.SendWolfHopperCatchupTo</c> wired into
    ///        <c>HandshakeSystem</c>'s channel 22 catch-up sequence
    ///        immediately after the route catchup (depots → routes →
    ///        hoppers → terminals ordering mirrors WOLF's OnLoad invariant
    ///        — Slice D). <see cref="AgencyId"/> carries the receiving
    ///        client's agency.</item>
    ///   <item><b>Client → server (per-mutation emit from postfix):</b>
    ///        emitted by <c>LmpClient.Systems.Agency.AgencyWolfHopperSender</c>
    ///        from Harmony postfixes on
    ///        <c>WOLF.ScenarioPersister.CreateHopper</c> +
    ///        <c>RemoveHopper</c> (Slice D). The server's
    ///        <c>AgencyMsgReader</c> + <c>AgencyWolfHopperRouter.TryRoute</c>
    ///        IGNORE the wire-supplied <see cref="AgencyId"/> and derive
    ///        the sender's agency from <c>AgencySystem.AgencyByPlayerName</c>.
    ///        Clients cannot spoof attribution.</item>
    /// </list>
    ///
    /// <para><b>Key form preservation</b> (pre-spec §2.f.v). Hopper id is a
    /// Guid in <c>ToString()</c> form WITH hyphens per WOLF's
    /// <c>HopperMetadata.cs:18</c>. Distinct from
    /// <see cref="AgencyWolfTerminalStateMsgData"/>'s "N" form (no hyphens)
    /// — do NOT normalize at any boundary; both sides preserve the verbatim
    /// wire string.</para>
    ///
    /// <para><b>REPLACE semantics on receive.</b> Each arrival of this
    /// message — both per-mutation echo and catch-up — is idempotent: the
    /// receiver upserts the full per-entry snapshot under the
    /// <see cref="AgencyWolfHopperEntry.Id"/> key. A future client mirror
    /// landing the apply path should mirror this posture (no per-field
    /// delta state machine — last-write-wins).</para>
    ///
    /// <para><b>RemovedKeys is non-trivial</b> (vs Routes' admin-only tail).
    /// WOLF's <c>ScenarioPersister.RemoveHopper(string id)</c> at
    /// <c>ScenarioPersister.cs:432-440</c> is a normal-operation API — the
    /// WOLF UI recipe-change flow calls Remove+Create as a pair when the
    /// operator picks a different recipe. The
    /// <see cref="ScenarioPersister_RemoveHopperPostfix"/>-equivalent client
    /// path ships the removed Id in the <see cref="RemovedKeys"/> tail. A
    /// future client mirror MUST handle the RemovedKeys tail (drop by Id),
    /// not just the Entries upsert.</para>
    ///
    /// <para><b>Orthogonal concerns (NOT carried on this message).</b>
    /// Hopper recipe is the only mutable per-hopper field WOLF carries
    /// across save/load (per <c>HopperMetadata.OnSave</c> at
    /// <c>HopperMetadata.cs:37-49</c>) — encoded as a flat
    /// <c>"resource,qty,resource,qty,..."</c> string mirroring WOLF's
    /// persistence format directly. The hopper's Depot reference is
    /// recovered indirectly at WOLF OnLoad time via Body+Biome depot
    /// lookup; this MsgData carries Body+Biome but NOT a depot foreign-
    /// key — the FK is enforced server-side at projection time (see
    /// <c>AgencyScenarioProjector.SpliceAgencyWolfState</c>'s Hopper FK
    /// sweep against the per-agency depot pool).</para>
    ///
    /// <para><b>Privacy rule (spec §10 Q1 — PrivateAgencyResources = true).</b>
    /// WOLF hopper state is per-agency private under gate=on. Same shape as
    /// <see cref="AgencyKolonyStateMsgData"/> + <see cref="AgencyWolfDepotStateMsgData"/>.</para>
    ///
    /// <para><b>Wire shape.</b> <see cref="AgencyId"/> (Guid) +
    /// <see cref="EntryCount"/> (int) + <see cref="Entries"/>[0..EntryCount-1]
    /// + forward-compat removal tail (<see cref="RemovedKeyCount"/> +
    /// <see cref="RemovedKeys"/>). Each entry writes via
    /// <see cref="AgencyWolfHopperEntry.Serialize"/>.</para>
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
