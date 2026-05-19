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
    /// <para><b>Removal tail (Phase 3 Slice E-1 — forward-compat only under
    /// the Q2 NO-MIGRATE policy).</b> Mid-session per-entry removal pushes
    /// ride a forward-compat tail appended after the <see cref="Entries"/>
    /// array: <see cref="RemovedPlanetaryKeyCount"/> +
    /// <see cref="RemovedPlanetaryKeys"/> (each key is a
    /// <c>$"{bodyIndex}|{resourceName}"</c> string matching the server-side
    /// <c>AgencyState.PlanetaryEntries</c> dict-key shape). The tail is read
    /// under a <c>lidgrenMsg.Position &lt; lidgrenMsg.LengthBits</c> guard so
    /// a pre-Slice-E-1 sender's tail-less message deserialises cleanly to an
    /// empty removed-keys list. <b>No producer in Slice E-1 emits to this
    /// tail.</b> Operator session 29 confirmed Q2 NO-MIGRATE per pre-spec
    /// §4.e: a vessel's transferagency does NOT migrate planetary entries
    /// (body-keyed entries are body-pool contributions, not vessel state),
    /// so the per-router migration helper for planetary is a documented
    /// no-op + info-log path, never an echo. The tail field exists strictly
    /// for SYMMETRY with the Kolony + Orbital tails and to leave room for a
    /// future <c>cleanplanetaryentries</c> admin command (out-of-scope for
    /// Phase 3) without a second protocol bump. Tail bytes on the wire are
    /// always 5 bytes (the VarInt-length-0 count) when the tail is present
    /// but unused — negligible footprint.</para>
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

        /// <summary>
        /// Phase 3 Slice E-1. Count of populated entries in
        /// <see cref="RemovedPlanetaryKeys"/>. Forward-compat only under the
        /// operator-confirmed Q2 NO-MIGRATE policy — see the type XML
        /// "Removal tail" section. Mirrors the
        /// <see cref="EntryCount"/> + <see cref="Entries"/> pairing on the
        /// existing forward tail. Read under the
        /// <c>lidgrenMsg.Position &lt; lidgrenMsg.LengthBits</c> guard, so a
        /// pre-Slice-E-1 sender's tail-less message round-trips to 0.
        /// </summary>
        public int RemovedPlanetaryKeyCount;

        /// <summary>
        /// Phase 3 Slice E-1. Forward-compat tail appended after
        /// <see cref="Entries"/>. Each key is the canonical
        /// <c>$"{bodyIndex}|{resourceName}"</c> dict-key shape matching the
        /// server-side <c>AgencyState.PlanetaryEntries</c> partition. No
        /// producer in Slice E-1 emits to this tail (Q2 NO-MIGRATE keeps
        /// planetary entries in their source agency on vessel transfer);
        /// the field exists for symmetry with Kolony + Orbital tails and to
        /// leave room for a future <c>cleanplanetaryentries</c> admin
        /// command without a second protocol bump.
        /// </summary>
        public string[] RemovedPlanetaryKeys = new string[0];

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

        /// <summary>
        /// Phase 3 Slice E-1. Upper bound on
        /// <see cref="RemovedPlanetaryKeyCount"/> on the wire. Mirrors
        /// <see cref="MaxEntryCount"/> for cap-symmetry with the forward tail
        /// (per [[feedback-wire-msgdata-chunking-caps]] — asymmetric caps
        /// invite send-side bugs the receive side asymmetrically catches).
        /// </summary>
        internal const int MaxRemovedPlanetaryKeyCount = 4096;

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

            // [Phase 3 Slice E-1] Removal tail. Same shape as the kolony
            // sibling. Emitted unconditionally — a pre-Slice-E-1 receiver
            // discards trailing bytes per Lidgren's per-channel message-frame
            // semantics. No producer emits a non-empty tail in Slice E-1
            // (Q2 NO-MIGRATE), but the tail field exists for symmetry.
            if (RemovedPlanetaryKeyCount < 0)
                throw new System.IO.InvalidDataException(
                    $"AgencyPlanetaryState RemovedPlanetaryKeyCount must be non-negative: {RemovedPlanetaryKeyCount}");
            if (RemovedPlanetaryKeys == null || RemovedPlanetaryKeys.Length < RemovedPlanetaryKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyPlanetaryState RemovedPlanetaryKeyCount {RemovedPlanetaryKeyCount} exceeds RemovedPlanetaryKeys.Length {(RemovedPlanetaryKeys?.Length ?? 0)}");
            if (RemovedPlanetaryKeyCount > MaxRemovedPlanetaryKeyCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyPlanetaryState RemovedPlanetaryKeyCount {RemovedPlanetaryKeyCount} exceeds MaxRemovedPlanetaryKeyCount {MaxRemovedPlanetaryKeyCount} — caller must chunk before send.");
            lidgrenMsg.Write(RemovedPlanetaryKeyCount);
            for (var i = 0; i < RemovedPlanetaryKeyCount; i++)
            {
                lidgrenMsg.Write(RemovedPlanetaryKeys[i] ?? string.Empty);
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

            // [Phase 3 Slice E-1] Forward-compat tail read. Pre-Slice-E-1
            // senders end the message at the Entries loop; the Position guard
            // catches that case and defaults RemovedPlanetaryKeys to empty.
            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                RemovedPlanetaryKeyCount = lidgrenMsg.ReadInt32();
                if (RemovedPlanetaryKeyCount < 0 || RemovedPlanetaryKeyCount > MaxRemovedPlanetaryKeyCount)
                    throw new System.IO.InvalidDataException(
                        $"AgencyPlanetaryState RemovedPlanetaryKeyCount out of range: {RemovedPlanetaryKeyCount} (allowed 0..{MaxRemovedPlanetaryKeyCount})");
                if (RemovedPlanetaryKeys.Length < RemovedPlanetaryKeyCount)
                    RemovedPlanetaryKeys = new string[RemovedPlanetaryKeyCount];
                for (var i = 0; i < RemovedPlanetaryKeyCount; i++)
                {
                    RemovedPlanetaryKeys[i] = lidgrenMsg.ReadString();
                }
            }
            else
            {
                RemovedPlanetaryKeyCount = 0;
                RemovedPlanetaryKeys = new string[0];
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

            // [Phase 3 Slice E-1] Removal tail size. Mirrors the kolony
            // sibling. The minimum overhead when the tail is empty is the
            // VarInt-length-0 count (5-byte upper bound) — accepted as
            // negligible.
            var removedTailSize = sizeof(int);
            for (var i = 0; i < RemovedPlanetaryKeyCount; i++)
            {
                removedTailSize += 5 + (RemovedPlanetaryKeys[i]?.Length ?? 0) * 4;
            }

            return base.InternalGetMessageSize() + GuidUtil.ByteSize + sizeof(int) + arraySize + removedTailSize;
        }
    }
}
