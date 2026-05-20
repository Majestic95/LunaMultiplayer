using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.WolfTerminalState"/> (slot 12).
    /// <list type="bullet">
    ///   <item><b>Server → client (owner-only echo + connect catch-up):</b>
    ///        emitted by <c>AgencySystemSender.SendWolfTerminalStateToOwner</c>
    ///        after the per-agency router upserts a batch, AND by
    ///        <c>AgencySystemSender.SendWolfTerminalCatchupTo</c> wired into
    ///        <c>HandshakeSystem</c>'s channel 22 catch-up sequence
    ///        immediately after the hopper catchup (depots → routes →
    ///        hoppers → terminals — Slice D). Terminals do NOT depend on
    ///        depots in WOLF's OnLoad, so their last-in-chain position is
    ///        for invariant uniformity rather than load-bearing ordering.
    ///        <see cref="AgencyId"/> carries the receiving client's
    ///        agency.</item>
    ///   <item><b>Client → server (per-mutation emit from postfix):</b>
    ///        emitted by <c>LmpClient.Systems.Agency.AgencyWolfTerminalSender</c>
    ///        from Harmony postfixes on
    ///        <c>WOLF.ScenarioPersister.CreateTerminal</c> +
    ///        <c>RemoveTerminal</c> (Slice D). The server's
    ///        <c>AgencyMsgReader</c> + <c>AgencyWolfTerminalRouter.TryRoute</c>
    ///        IGNORE the wire-supplied <see cref="AgencyId"/> and derive
    ///        the sender's agency from <c>AgencySystem.AgencyByPlayerName</c>.
    ///        Clients cannot spoof attribution.</item>
    /// </list>
    ///
    /// <para><b>Key form preservation</b> (pre-spec §2.f.vi). Terminal id
    /// is a Guid in <c>ToString("N")</c> form WITHOUT hyphens per WOLF's
    /// <c>TerminalMetadata.cs:15</c>. Distinct from
    /// <see cref="AgencyWolfHopperStateMsgData"/>'s with-hyphens form —
    /// do NOT normalize at any boundary.</para>
    ///
    /// <para><b>REPLACE semantics on receive.</b> Each arrival of this
    /// message — both per-mutation echo and catch-up — is idempotent: the
    /// receiver upserts the full per-entry snapshot under the
    /// <see cref="AgencyWolfTerminalEntry.Id"/> key. A future client mirror
    /// landing the apply path should mirror this posture (last-write-wins).</para>
    ///
    /// <para><b>RemovedKeys is non-trivial</b>. WOLF's
    /// <c>ScenarioPersister.RemoveTerminal(string id)</c> at
    /// <c>ScenarioPersister.cs:442-449</c> is a normal-operation API — the
    /// WOLF UI removes a terminal when the operator decommissions it. The
    /// <see cref="ScenarioPersister_RemoveTerminalPostfix"/>-equivalent
    /// client path ships the removed Id in the <see cref="RemovedKeys"/>
    /// tail. A future client mirror MUST handle the RemovedKeys tail.</para>
    ///
    /// <para><b>NO depot FK</b> (unlike Hopper / Route / future CrewRoute).
    /// <c>TerminalMetadata.OnLoad</c> at <c>ScenarioPersister.cs:343-353</c>
    /// loads terminals directly via <c>TerminalMetadata.OnLoad</c> without
    /// a depot lookup; <see cref="AgencyWolfTerminalEntry"/> carries Body +
    /// Biome explicitly per <c>TerminalMetadata.cs:9-29</c>. The projector
    /// does NOT FK-sweep terminals against the depot pool.</para>
    ///
    /// <para><b>Privacy rule (spec §10 Q1 — PrivateAgencyResources = true).</b>
    /// WOLF terminal state is per-agency private under gate=on.</para>
    ///
    /// <para><b>Wire shape.</b> <see cref="AgencyId"/> (Guid) +
    /// <see cref="EntryCount"/> (int) + <see cref="Entries"/>[0..EntryCount-1]
    /// + forward-compat removal tail (<see cref="RemovedKeyCount"/> +
    /// <see cref="RemovedKeys"/>).</para>
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
