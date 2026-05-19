using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 3 ShareKolony — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.PlanetaryState"/> (slot 7). Same shape +
    /// trust posture as Slice B's <see cref="AgencyKolonyStateMsgData"/>; the
    /// difference is the entry type (<see cref="AgencyPlanetaryEntry"/>) and
    /// the partition key shape (body-and-resource, not vessel-and-body).
    /// <list type="bullet">
    ///   <item><b>Server → client (owner-only echo + connect-catch-up):</b>
    ///        emitted by <c>Server.System.Agency.AgencySystemSender.SendPlanetaryStateToOwner</c>
    ///        after the per-agency router upserts a batch, AND by
    ///        <c>Server.System.Agency.AgencySystemSender.SendPlanetaryCatchupTo</c>
    ///        wired into <see cref="LmpCommon.Message.Server.AgencySrvMsg"/>
    ///        channel 22 catch-up sequence in <c>HandshakeSystem</c> immediately
    ///        after the Slice B <see cref="AgencyKolonyStateMsgData"/> catch-up.
    ///        <see cref="AgencyId"/> carries the receiving client's agency.</item>
    ///   <item><b>Client → server (per-mutation emit from postfix):</b>
    ///        emitted by <c>LmpClient.Systems.Agency.AgencyPlanetarySender</c>
    ///        from the Harmony postfix on
    ///        <c>KolonyTools.PlanetaryLogistics.ModulePlanetaryLogistics.LevelResources</c>.
    ///        The server's <c>AgencyMsgReader.HandleMessage</c> +
    ///        <c>AgencyPlanetaryRouter.TryRoute</c> IGNORE the wire-supplied
    ///        <see cref="AgencyId"/> and derive the sender's agency from
    ///        <see cref="LmpCommon.Message.Server.AgencySrvMsg"/>'s authenticated
    ///        <c>ClientStructure.PlayerName</c> via
    ///        <c>AgencySystem.AgencyByPlayerName</c> — same trust posture as
    ///        Slice B's <see cref="AgencyKolonyStateMsgData"/> + the Stage 5.17d
    ///        <see cref="AgencyContractRouter"/>. Clients cannot spoof which
    ///        agency a mutation is attributed to.</item>
    /// </list>
    ///
    /// <para><b>Privacy rule (spec §10 Q1 — PrivateAgencyResources = true).</b>
    /// Planetary-warehouse balances are per-agency private under gate=on. The
    /// router only ever <c>SendToClient</c>s this to the agency owner; peers
    /// never receive another agency's per-agency planetary entries. Stage
    /// 5.18-series client mirror authors: defensive-discard any inbound whose
    /// <see cref="AgencyId"/> does not match the local player's assigned
    /// agency. (The server's owner-only path makes this defensive check
    /// unreachable in practice; the discard rule is defence-in-depth, same
    /// shape as <see cref="AgencyKolonyStateMsgData"/>.)</para>
    ///
    /// <para><b>Arrival conditions (consumer-facet documentation per
    /// [[reference-agency-wire-extension]] recipe step 7):</b></para>
    /// <list type="bullet">
    ///   <item><b>On connect / reconnect:</b> immediately after the Slice B
    ///        <see cref="AgencyKolonyStateMsgData"/> catch-up, the server fires
    ///        <c>AgencySystemSender.SendPlanetaryCatchupTo</c> with the OWNER's
    ///        persisted <c>AgencyState.PlanetaryEntries</c> dictionary. A
    ///        returning player thus receives their entire per-agency planetary
    ///        pool in one batch before gameplay starts; subsequent mid-session
    ///        mutations arrive incrementally. Catch-up fires UNCONDITIONALLY
    ///        under gate=on, including for an empty dict — the client mirror
    ///        needs the empty state to distinguish "no per-agency planetary
    ///        balances yet" from "unsynced".</item>
    ///   <item><b>On mid-session mutation:</b> after the client's Harmony
    ///        postfix on <c>ModulePlanetaryLogistics.LevelResources</c> fires
    ///        (every <c>CheckFrequency</c> seconds per warehouse part, default
    ///        12s, during a flow that actually mutates a tracked entry), the
    ///        client emits this message C→S via
    ///        <c>LmpClient.Systems.Agency.AgencyPlanetarySender.SendMutation</c>.
    ///        The server-side router intercepts, classifies per-entry
    ///        (cross-agency reject, Unassigned-sentinel bypass, vessel-not-in-store
    ///        skip), upserts into <c>AgencyState.PlanetaryEntries</c>, and echoes
    ///        this owner-only message back to the originator. NO peer relay —
    ///        peers learn of cross-agency planetary state only via the
    ///        projection-on-send path through <c>AgencyScenarioProjector</c>'s
    ///        <c>PlanetaryLogisticsScenario</c> case (Slice C), where each peer
    ///        receives ONLY their own agency's projected entries.</item>
    /// </list>
    ///
    /// <para><b>Client write path (5.18-series author note):</b> the client
    /// sends this MsgData type directly C→S — there is no
    /// <c>ShareProgress.Share*</c> path equivalent for planetary state. Phase 3
    /// introduces a brand-new client→server wire because the existing
    /// shared-scenario 30s SHA pass is not a per-mutation channel — the
    /// postfix needs its own send queue. Same shape as Slice B kolony / Slice
    /// D orbital.</para>
    ///
    /// <para><b>Orthogonal concerns:</b> planetary entries do NOT interact with
    /// stock <c>Funding.Instance</c> / <c>ResearchAndDevelopment.Instance</c> /
    /// <c>Reputation.Instance</c> — they are pure mass-resource flows (a
    /// warehouse vessel pumps Hydrates / Karbonite / etc. into a body's
    /// logistics pool; other vessels in the same body's pool can draw on it).
    /// The reward routing concern that Slice B documented (kolony entries
    /// flowing into stock currency via <c>ModuleColonyRewards.CheckRewards</c>)
    /// does NOT apply here. Phase 3 stores
    /// <see cref="AgencyPlanetaryEntry.StoredQuantity"/> as opaque per-agency
    /// state; no separate currency-routing hook needed.</para>
    ///
    /// <para><b>Wire shape.</b> Fields: <see cref="AgencyId"/> (Guid) +
    /// <see cref="EntryCount"/> (int) + <see cref="Entries"/>[0..EntryCount-1].
    /// Each <see cref="AgencyPlanetaryEntry"/> writes its 4 fields (1 Guid + 1
    /// int + 1 string + 1 double) in stable order via the entry's
    /// <see cref="AgencyPlanetaryEntry.Serialize"/>/<see cref="AgencyPlanetaryEntry.Deserialize"/>.
    /// No QuickLZ compression — entry payload is small and the per-message CPU
    /// cost is not worth it on the postfix hot path. <see cref="AgencyPlanetaryEntry.OwningVesselId"/>
    /// is the canonical Guid; the wire never carries the "D"-form string
    /// representation (matches Slice B's normalisation goal, but planetary's
    /// OwningVesselId field is already typed as Guid so no string-form drift is
    /// possible).</para>
    ///
    /// <para><b>Echo vs catch-up distinction.</b> The wire carries NO flag
    /// distinguishing a connect-time catch-up from a mid-session echo — both
    /// ride the same MsgData type. The client mirror (deferred to a
    /// 5.18-series follow-up) MUST be designed for idempotent
    /// upsert-by-key. The key is
    /// <c>$"{<see cref="AgencyPlanetaryEntry.BodyIndex"/>}|{<see cref="AgencyPlanetaryEntry.ResourceName"/>}"</c>
    /// — body-and-resource-keyed, distinct from Slice B's kolony key
    /// (vessel-and-body-keyed). A catch-up batch followed by an echo of an
    /// entry already in the catch-up must apply idempotently. Server-side
    /// router IS the canonical source; client's mirror is a pure projection.</para>
    ///
    /// <para><b>Client-mirror keying note (5.18-series author):</b> the wire
    /// CARRIES <see cref="AgencyPlanetaryEntry.OwningVesselId"/> per entry,
    /// but the canonical server-side dict key is
    /// <c>$"{BodyIndex}|{ResourceName}"</c> — the OwningVesselId is
    /// informational only (for UI labels like "last warehouse to
    /// contribute") and is NOT part of the partition. The client mirror
    /// SHOULD also key by <c>$"{BodyIndex}|{ResourceName}"</c> for upsert
    /// idempotency — otherwise a second vessel of the same agency pumping
    /// the same resource on the same body would produce two mirror entries
    /// where the server has one. This asymmetry is intentional (body-pool
    /// semantics, pre-spec §4.e) and DIFFERS from Slice B kolony (where
    /// VesselId IS part of the key).</para>
    ///
    /// <para><b>Cross-channel arrival ordering (5.18-series author):</b> on
    /// channel 22 (ReliableOrdered), the connect-time catchup batch arrives
    /// BEFORE any mid-session echo for the same agency. Mirror authors can
    /// apply each batch by key with last-write-wins semantics safely; no
    /// out-of-order arrival between catch-up and echo is structurally
    /// possible per Lidgren's per-channel guarantee. Same shape applies to
    /// the sibling Slice B <see cref="AgencyKolonyStateMsgData"/> on the
    /// same channel.</para>
    ///
    /// <para><b>Removal semantics — deferred to Slice E (MKS-aware
    /// extension to the existing 5.18d deleteagency cascade).</b> Slice C
    /// has no removal-echo wire because planetary entries do NOT migrate on
    /// <c>transferagency</c> per pre-spec §4.e — the only removal-producing
    /// path is the Slice E MKS-aware extension to the existing
    /// <c>deleteagency</c> command (which today removes the entire
    /// AgencyState file wholesale; the client mirror simply forgets the
    /// AgencyId, no per-entry removal echo needed). If Slice E later wants
    /// a partial-removal wire (e.g. <c>cleanplanetaryentries</c> admin
    /// command), the options are: (1) append
    /// <c>string[] RemovedKeys</c> at this MsgData's tail per the
    /// forward-compat clause below, with the key format
    /// <c>$"{bodyIndex}|{resourceName}"</c> — STRING-keyed for this slot
    /// (distinct from Slice D orbital's removal tail, which would be
    /// <c>Guid[] RemovedTransferGuids</c> per its Guid-keyed partition); or
    /// (2) carve out an <c>AgencyPlanetaryRemovalMsgData</c> at a new wire
    /// enum slot. The Slice E author SHOULD pick (1) — single class per
    /// mutation-surface (pre-spec §2.e). Slice C intentionally does NOT
    /// pre-define the field per the YAGNI rule from CLAUDE.md.
    /// <b>Note for Slice E:</b> the per-router migration policy is
    /// DIFFERENT here — planetary balances stay where they are when a
    /// vessel transfers agency, so the removal-echo scenario is exclusively
    /// admin-driven, NOT <c>transferagency</c> cascade. Slice B's kolony
    /// removal-echo doc text included a vessel-keyed migration scenario
    /// that does not apply here.</para>
    ///
    /// <para><b>Forward-compatibility.</b> No room for new fields without a
    /// protocol bump — the trailing read is a count-driven array. Future
    /// additions append at the message tail with a
    /// <c>lidgrenMsg.Position &lt; lidgrenMsg.LengthBits</c> end-of-message
    /// guard, matching the <c>VesselProtoMsgData.Reason</c> precedent.</para>
    /// </summary>
    public class AgencyPlanetaryStateMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyPlanetaryStateMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.PlanetaryState;

        /// <summary>
        /// On S→C (echo + catch-up): the receiving client's agency. On C→S
        /// (postfix mutation): the server IGNORES this field and derives the
        /// sender's agency authoritatively from
        /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. Documented
        /// in the type XML — clients cannot spoof attribution.
        /// </summary>
        public Guid AgencyId;
        public int EntryCount;
        public AgencyPlanetaryEntry[] Entries = new AgencyPlanetaryEntry[0];

        public override string ClassName { get; } = nameof(AgencyPlanetaryStateMsgData);

        /// <summary>
        /// Upper bound on <see cref="EntryCount"/> on the wire. A megabase under
        /// heavy MKS load — many warehouses across many bodies — can produce
        /// thousands of distinct (BodyIndex, ResourceName) records over time;
        /// 4096 leaves generous headroom while still preventing a malicious
        /// peer from forcing a multi-GB allocation by shipping
        /// <see cref="int.MaxValue"/>. Same DoS-amplification class round-2 wire
        /// review caught on <see cref="AgencyKolonyStateMsgData.MaxEntryCount"/>
        /// and <see cref="AgencyContractMsgData.MaxContractCount"/>.
        /// </summary>
        internal const int MaxEntryCount = 4096;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            // [Phase 3 Slice C / general-lens SHOULD FIX S4] Precondition
            // guard. A caller that set EntryCount > Entries.Length would
            // throw IndexOutOfRangeException MID-SERIALIZE — after AgencyId
            // and EntryCount bytes had already been written to channel 22.
            // The receive side would then read EntryCount * entry-bytes of
            // following-message garbage, desyncing the channel. Explicit
            // throw before any bytes hit the wire is the defensible
            // boundary. (Same precondition applies to AgencyKolonyStateMsgData
            // + AgencyContractMsgData; Slice C fixes its own MsgData; the
            // others are deferred to a uniform refresh.)
            if (EntryCount < 0)
                throw new System.IO.InvalidDataException(
                    $"AgencyPlanetaryState EntryCount must be non-negative: {EntryCount}");
            if (Entries == null || Entries.Length < EntryCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyPlanetaryState EntryCount {EntryCount} exceeds Entries.Length {(Entries?.Length ?? 0)}");

            GuidUtil.Serialize(AgencyId, lidgrenMsg);
            lidgrenMsg.Write(EntryCount);
            // Caller contract: Entries[0..EntryCount-1] must be non-null. Same
            // "trust the caller" convention used by AgencyHandshakeMsgData,
            // AgencyContractMsgData, and AgencyKolonyStateMsgData — a sender
            // that null-skipped while emitting the original count would desync
            // with a receiver that reads EntryCount entries unconditionally,
            // corrupting subsequent bytes on channel 22.
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
                    $"AgencyPlanetaryState EntryCount out of range: {EntryCount} (allowed 0..{MaxEntryCount})");

            if (Entries.Length < EntryCount)
                Entries = new AgencyPlanetaryEntry[EntryCount];

            for (var i = 0; i < EntryCount; i++)
            {
                if (Entries[i] == null)
                    Entries[i] = new AgencyPlanetaryEntry();

                Entries[i].Deserialize(lidgrenMsg);
            }
        }

        internal override int InternalGetMessageSize()
        {
            // [Phase 3 Slice C / general-lens SHOULD FIX S4] Same precondition
            // as InternalSerialize — wrong here would undersize the Lidgren
            // write buffer and corrupt the send. The Serialize call will throw
            // immediately after, but better to throw here too so the caller
            // sees the precondition violation at byte-count time.
            if (EntryCount < 0 || Entries == null || Entries.Length < EntryCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyPlanetaryState GetByteCount precondition: EntryCount={EntryCount} Entries.Length={(Entries?.Length ?? 0)}");

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
