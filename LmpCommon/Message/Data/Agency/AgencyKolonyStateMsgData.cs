using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 3 ShareKolony — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.KolonyState"/> (slot 6).
    /// <list type="bullet">
    ///   <item><b>Server → client (owner-only echo + connect-catch-up):</b>
    ///        emitted by <c>Server.System.Agency.AgencySystemSender.SendKolonyStateToOwner</c>
    ///        after the per-agency router upserts a batch, AND by
    ///        <c>Server.System.Agency.AgencySystemSender.SendKolonyCatchupTo</c>
    ///        wired into <see cref="LmpCommon.Message.Server.AgencySrvMsg"/>
    ///        channel 22 catch-up sequence in <c>HandshakeSystem</c> immediately
    ///        after the Stage 5.17d <c>AgencyContractMsgData</c> catch-up.
    ///        <see cref="AgencyId"/> carries the receiving client's agency.</item>
    ///   <item><b>Client → server (per-mutation emit from postfix):</b>
    ///        emitted by <c>LmpClient.Systems.Agency.AgencyKolonySender</c>
    ///        from the Harmony postfix on
    ///        <c>KolonyTools.KolonizationManager.TrackLogEntry</c>. The server's
    ///        <c>AgencyMsgReader.HandleMessage</c> + <c>AgencyKolonyRouter.TryRoute</c>
    ///        IGNORE the wire-supplied <see cref="AgencyId"/> and derive the
    ///        sender's agency from
    ///        <see cref="LmpCommon.Message.Server.AgencySrvMsg"/>'s authenticated
    ///        <c>ClientStructure.PlayerName</c> via
    ///        <c>AgencySystem.AgencyByPlayerName</c> — same trust posture as the
    ///        Stage 5.17d <c>AgencyContractRouter</c>. Clients cannot spoof which
    ///        agency a mutation is attributed to.</item>
    /// </list>
    ///
    /// <para><b>Privacy rule (spec §10 Q1 — PrivateAgencyResources = true).</b>
    /// Kolony research is per-agency private under gate=on. The router only
    /// ever <c>SendToClient</c>s this to the agency owner; peers never receive
    /// another agency's per-agency kolony entries. Stage 5.18-series client
    /// mirror authors: defensive-discard any inbound whose <see cref="AgencyId"/>
    /// does not match the local player's assigned agency. (The server's
    /// owner-only path makes this defensive check unreachable in practice; the
    /// discard rule is defence-in-depth, same shape as
    /// <see cref="AgencyContractMsgData"/>.)</para>
    ///
    /// <para><b>Arrival conditions (consumer-facet documentation per
    /// [[reference-agency-wire-extension]] recipe step 7):</b></para>
    /// <list type="bullet">
    ///   <item><b>On connect / reconnect:</b> immediately after the Stage 5.17d
    ///        <see cref="AgencyContractMsgData"/> catch-up, the server fires
    ///        <c>AgencySystemSender.SendKolonyCatchupTo</c> with the OWNER's
    ///        persisted <c>AgencyState.KolonyEntries</c> dictionary. A returning
    ///        player thus receives their entire per-agency kolony pool in one
    ///        batch before gameplay starts; subsequent mid-session mutations
    ///        arrive incrementally. Catch-up fires UNCONDITIONALLY under
    ///        gate=on, including for an empty dict — the client mirror needs the
    ///        empty state to distinguish "no per-agency kolony yet" from
    ///        "unsynced".</item>
    ///   <item><b>On mid-session mutation:</b> after the client's Harmony
    ///        postfix on <c>KolonyTools.KolonizationManager.TrackLogEntry</c>
    ///        fires (during MKS converter accrual, ColonyReward claim that
    ///        cascades back into TrackLogEntry, or operator-driven mutation),
    ///        the client emits this message C→S via
    ///        <c>LmpClient.Systems.Agency.AgencyKolonySender.SendMutation</c>.
    ///        The server-side router intercepts, classifies per-entry
    ///        (cross-agency reject, Unassigned-sentinel bypass, vessel-not-in-store
    ///        skip), upserts into <c>AgencyState.KolonyEntries</c>, and echoes
    ///        this owner-only message back to the originator. NO peer relay —
    ///        peers learn of cross-agency kolony state only via the
    ///        projection-on-send path through <c>AgencyScenarioProjector</c>'s
    ///        <c>KolonizationScenario</c> case (Slice B), where each peer
    ///        receives ONLY their own agency's projected entries.</item>
    /// </list>
    ///
    /// <para><b>Client write path (5.18-series author note):</b> the client
    /// sends this MsgData type directly C→S — there is no
    /// <c>ShareProgress.Share*</c> path equivalent for kolony, unlike the
    /// Stage 5.17d <see cref="AgencyContractMsgData"/> echo path (which relies
    /// on the existing <c>ShareProgressContractsMsgData</c> as the C→S
    /// channel). Phase 3 introduces a brand-new client→server wire for kolony
    /// mutations because the existing shared-scenario 30s SHA pass is not a
    /// per-mutation channel — the postfix needs its own send queue. Future
    /// MKS-routed surfaces (planetary in Slice C, orbital in Slice D) follow
    /// the same C→S pattern via their respective per-slot MsgData.</para>
    ///
    /// <para><b>Orthogonal concerns:</b> the entry-level <c>Science</c> /
    /// <c>Funds</c> / <c>Reputation</c> scalars on <see cref="AgencyKolonyEntry"/>
    /// are stored as opaque per-agency state by Phase 3. They only flow back
    /// into KSP's stock <c>Funding.Instance</c> /
    /// <c>ResearchAndDevelopment.Instance</c> / <c>Reputation.Instance</c>
    /// totals when the operator clicks "Check Kolony Rewards" on the
    /// <c>ModuleColonyRewards</c> part — at which point the band-1
    /// <c>AgencyCurrencyRouter</c> / <c>AgencyResearchRouter</c> intercept
    /// those stock-currency mutations server-side (Stage 5.17e). Phase 3 does
    /// NOT add a separate currency-routing hook for kolony entries; this
    /// message carries only the kolony-entry state-machine snapshot, not the
    /// reward funds themselves.</para>
    ///
    /// <para><b>Wire shape.</b> Fields: <see cref="AgencyId"/> (Guid) +
    /// <see cref="EntryCount"/> (int) + <see cref="Entries"/>[0..EntryCount-1].
    /// Each <see cref="AgencyKolonyEntry"/> writes its 13 fields (1 string + 1
    /// int + 9 doubles + 3 ints) in stable order via the entry's
    /// <see cref="AgencyKolonyEntry.Serialize"/>/<see cref="AgencyKolonyEntry.Deserialize"/>.
    /// No QuickLZ compression — entry fields don't compress well at this size
    /// and the per-message CPU cost is not worth it on the postfix hot path
    /// (pre-spec §11 Q6 cadence budget). All <see cref="AgencyKolonyEntry.VesselId"/>
    /// values on the wire are normalized to Guid "N" form (32 hex chars, no
    /// hyphens) — the router enforces this at ingest, and the client postfix
    /// emits in the same form, so dict-key dedup at the server AND any future
    /// client-side mirror cache uses a single canonical key shape.</para>
    ///
    /// <para><b>Echo vs catch-up distinction (consumer-lens Lens-2 MF3).</b> The
    /// wire carries NO flag distinguishing a connect-time catch-up from a
    /// mid-session echo — both ride the same MsgData type. The client mirror
    /// (deferred to a 5.18-series follow-up) MUST be designed for idempotent
    /// upsert-by-key. The key is
    /// <c>$"{<see cref="AgencyKolonyEntry.VesselId"/>:N}|{<see cref="AgencyKolonyEntry.BodyIndex"/>}"</c>
    /// — same shape the server uses for its <c>AgencyState.KolonyEntries</c>
    /// dict. A catch-up batch followed by an echo of an entry already in the
    /// catch-up must apply idempotently and produce the same end state. The
    /// server-side router IS the canonical source — the client's mirror is a
    /// pure projection. This matches the <see cref="AgencyContractMsgData"/>
    /// 5.18a contract (upsert-by-<c>ContractGuid</c>); the same shape, different
    /// key.</para>
    ///
    /// <para><b>Removal semantics — deferred to Slice E (consumer-lens Lens-3
    /// MF4).</b> Slice B has no removal-echo wire because the only routine that
    /// produces removals is Stage 5.18d-MKS-aware <c>transferagency</c> (Slice E
    /// scope). Pre-spec §4.e specifies the migration policy: vessel-keyed
    /// <c>KolonyEntries</c> migrate A→B with the vessel; the wire echo to A is
    /// a "removed-keys" signal, and the echo to B is an "added-entries"
    /// signal. Slice E's options for the removal wire (in preference order):
    /// (1) append <c>string[] RemovedKeys</c> at this MsgData's tail per the
    /// forward-compat clause below (protocol-additive; no new slot needed); or
    /// (2) carve out an <c>AgencyKolonyRemovalMsgData</c> at a new wire enum
    /// slot. The Slice E author SHOULD pick (1) — single class per
    /// mutation-surface (pre-spec §2.e). Slice B intentionally does NOT
    /// pre-define the field — the YAGNI rule from CLAUDE.md applies (don't
    /// design for hypothetical future requirements), and Slice E's migration
    /// policy may surface a richer shape that we'd otherwise lock in
    /// prematurely. Until Slice E ships, removals are NOT echoed; the only
    /// state-clearing path is server boot + <c>WarnAboutSharedKolonyOnUpgrade</c>
    /// + operator hand-edit, all out-of-band relative to this wire.</para>
    ///
    /// <para><b>Forward-compatibility.</b> No room for new fields without a
    /// protocol bump — the trailing read is a count-driven array. Future
    /// additions append at the message tail with a
    /// <c>lidgrenMsg.Position &lt; lidgrenMsg.LengthBits</c> end-of-message
    /// guard, matching the <c>VesselProtoMsgData.Reason</c> precedent.</para>
    /// </summary>
    public class AgencyKolonyStateMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyKolonyStateMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.KolonyState;

        /// <summary>
        /// On S→C (echo + catch-up): the receiving client's agency. On C→S
        /// (postfix mutation): the server IGNORES this field and derives the
        /// sender's agency authoritatively from
        /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. Documented
        /// in the type XML — clients cannot spoof attribution.
        /// </summary>
        public Guid AgencyId;
        public int EntryCount;
        public AgencyKolonyEntry[] Entries = new AgencyKolonyEntry[0];

        public override string ClassName { get; } = nameof(AgencyKolonyStateMsgData);

        /// <summary>
        /// Upper bound on <see cref="EntryCount"/> on the wire. A megabase under
        /// heavy MKS load can produce thousands of distinct (VesselId, BodyIndex)
        /// records over time; 4096 leaves generous headroom while still preventing
        /// a malicious peer from forcing a multi-GB allocation by shipping
        /// <see cref="int.MaxValue"/>. Same DoS-amplification class round-2 wire
        /// review caught on <see cref="AgencyHandshakeMsgData.MaxOtherAgencyCount"/>
        /// and <see cref="AgencyContractMsgData.MaxContractCount"/>.
        /// </summary>
        internal const int MaxEntryCount = 4096;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            GuidUtil.Serialize(AgencyId, lidgrenMsg);
            lidgrenMsg.Write(EntryCount);
            // Caller contract: Entries[0..EntryCount-1] must be non-null. Same
            // "trust the caller" convention used by AgencyHandshakeMsgData and
            // AgencyContractMsgData — a sender that null-skipped while emitting
            // the original count would desync with a receiver that reads
            // EntryCount entries unconditionally, corrupting subsequent bytes
            // on channel 22.
            for (var i = 0; i < EntryCount; i++)
            {
                Entries[i].Serialize(lidgrenMsg);
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            AgencyId = GuidUtil.Deserialize(lidgrenMsg);
            EntryCount = lidgrenMsg.ReadInt32();
            if (EntryCount < 0 || EntryCount > MaxEntryCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyKolonyState EntryCount out of range: {EntryCount} (allowed 0..{MaxEntryCount})");

            if (Entries.Length < EntryCount)
                Entries = new AgencyKolonyEntry[EntryCount];

            for (var i = 0; i < EntryCount; i++)
            {
                if (Entries[i] == null)
                    Entries[i] = new AgencyKolonyEntry();

                Entries[i].Deserialize(lidgrenMsg);
            }
        }

        internal override int InternalGetMessageSize()
        {
            // Caller contract (see InternalSerialize): entries 0..EntryCount-1 non-null.
            var arraySize = 0;
            for (var i = 0; i < EntryCount; i++)
            {
                arraySize += Entries[i].GetByteCount();
            }

            return base.InternalGetMessageSize() + GuidUtil.ByteSize + sizeof(int) + arraySize;
        }
    }
}
