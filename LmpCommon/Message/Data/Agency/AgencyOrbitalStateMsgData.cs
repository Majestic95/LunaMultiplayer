using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 3 ShareKolony — single shared MsgData used both directions on
    /// <see cref="AgencyMessageType.OrbitalState"/> (slot 8). Same shape + trust
    /// posture as Slice B's <see cref="AgencyKolonyStateMsgData"/> and Slice C's
    /// <see cref="AgencyPlanetaryStateMsgData"/>; the differences are the entry
    /// type (<see cref="AgencyOrbitalTransferEntry"/>, includes a mutable
    /// <c>byte[] PayloadBytes</c>) and the partition key shape (<c>Guid
    /// TransferGuid</c>, not a string composite).
    /// <list type="bullet">
    ///   <item><b>Server → client (owner-only echo + connect-catch-up):</b>
    ///        emitted by <c>Server.System.Agency.AgencySystemSender.SendOrbitalStateToOwner</c>
    ///        after the per-agency router upserts a batch, AND by
    ///        <c>Server.System.Agency.AgencySystemSender.SendOrbitalCatchupTo</c>
    ///        wired into <see cref="LmpCommon.Message.Server.AgencySrvMsg"/>
    ///        channel 22 catch-up sequence in <c>HandshakeSystem</c> immediately
    ///        after the Slice C <see cref="AgencyPlanetaryStateMsgData"/> catch-up.
    ///        <see cref="AgencyId"/> carries the receiving client's agency.</item>
    ///   <item><b>Client → server (per-mutation emit from state-machine postfixes):</b>
    ///        emitted by <c>LmpClient.Systems.Agency.AgencyOrbitalSender</c>
    ///        from the Harmony postfixes on
    ///        <c>KolonyTools.OrbitalLogisticsTransferRequest.DoFinalLaunchTasks</c>
    ///        (Launched), <c>Abort</c> (Returning), and a postfix on
    ///        <c>ScenarioOrbitalLogistics.ProcessTransfers</c>'s
    ///        post-Deliver branch where Status transitions to a terminal value
    ///        (Delivered / Partial / Failed / Cancelled). The server's
    ///        <c>AgencyMsgReader.HandleMessage</c> + <c>AgencyOrbitalRouter.TryRoute</c>
    ///        IGNORE the wire-supplied <see cref="AgencyId"/> and derive the
    ///        sender's agency from <see cref="LmpCommon.Message.Server.AgencySrvMsg"/>'s
    ///        authenticated <c>ClientStructure.PlayerName</c> via
    ///        <c>AgencySystem.AgencyByPlayerName</c> — same trust posture as
    ///        Slice B/C and the Stage 5.17d <c>AgencyContractRouter</c>. Clients
    ///        cannot spoof which agency a mutation is attributed to.</item>
    /// </list>
    ///
    /// <para><b>Privacy rule (spec §10 Q1 — PrivateAgencyResources = true).</b>
    /// Orbital transfer queues are per-agency private under gate=on. The router
    /// only ever <c>SendToClient</c>s this to the agency owner; peers never
    /// receive another agency's per-agency transfers. Stage 5.18-series client
    /// mirror authors: defensive-discard any inbound whose
    /// <see cref="AgencyId"/> does not match the local player's assigned
    /// agency. (The server's owner-only path makes this defensive check
    /// unreachable in practice; the discard rule is defence-in-depth, same
    /// shape as <see cref="AgencyKolonyStateMsgData"/>.)</para>
    ///
    /// <para><b>Arrival conditions (consumer-facet documentation per
    /// [[reference-agency-wire-extension]] recipe step 7):</b></para>
    /// <list type="bullet">
    ///   <item><b>On connect / reconnect:</b> immediately after the Slice C
    ///        <see cref="AgencyPlanetaryStateMsgData"/> catch-up, the server
    ///        fires <c>AgencySystemSender.SendOrbitalCatchupTo</c> with the
    ///        OWNER's persisted <c>AgencyState.OrbitalTransfers</c> dictionary.
    ///        A returning player whose orbital transfer queue persisted across
    ///        a server restart receives them in one batch before any
    ///        per-frame <c>ScenarioOrbitalLogistics.Update</c> cycle. Catch-up
    ///        fires UNCONDITIONALLY under gate=on, including for an empty dict
    ///        — the client mirror needs the empty state to distinguish "no
    ///        per-agency transfers yet" from "unsynced".</item>
    ///   <item><b>On mid-session mutation:</b> after the client's Harmony
    ///        postfixes on the transfer state-machine fire (DoFinalLaunchTasks /
    ///        Abort / terminal Status writes inside Deliver), the client emits
    ///        this message C→S via
    ///        <c>LmpClient.Systems.Agency.AgencyOrbitalSender.SendMutation</c>.
    ///        The server-side router intercepts, classifies per-entry
    ///        (cross-agency reject by destination vessel, Unassigned-sentinel
    ///        bypass, destination-vessel-not-in-store DROP), upserts into
    ///        <c>AgencyState.OrbitalTransfers</c>, and echoes this owner-only
    ///        message back to the originator. NO peer relay — peers learn of
    ///        cross-agency orbital state only via the projection-on-send path
    ///        through <c>AgencyScenarioProjector</c>'s
    ///        <c>ScenarioOrbitalLogistics</c> case (Slice D), where each peer
    ///        receives ONLY their own agency's projected entries.</item>
    /// </list>
    ///
    /// <para><b>Client write path (5.18-series author note):</b> the client
    /// sends this MsgData type directly C→S — there is no
    /// <c>ShareProgress.Share*</c> path equivalent for orbital state. Phase 3
    /// introduces a brand-new client→server wire because the existing
    /// shared-scenario 30s SHA pass is not a per-mutation channel — the
    /// state-machine postfixes need their own send queue. Same shape as
    /// Slice B kolony / Slice C planetary.</para>
    ///
    /// <para><b>Orthogonal concerns — Deliver-gate authority lives elsewhere.</b>
    /// The single-executor-per-transfer authority that closes the per-frame
    /// double-spend (pre-spec §1.c, §2.d) is enforced by a CLIENT-SIDE Harmony
    /// PREFIX on <c>OrbitalLogisticsTransferRequest.Deliver</c>
    /// (<c>LmpClient.Harmony.OrbitalLogisticsTransferRequest_DeliverPrefix</c>)
    /// — NOT by this wire. The prefix mutates <c>Status=Failed +
    /// StatusMessage</c> BEFORE returning false on the skipping peers (critical
    /// — a naive prefix-skip leaves the inner <c>while</c> at
    /// <c>ScenarioOrbitalLogistics.cs:194</c> yielding forever, hanging
    /// ProcessTransfers and accumulating coroutine stacks). The prefix is
    /// GATE-STATE-INDEPENDENT — it runs under both PerAgencyCareer=true and
    /// PerAgencyCareer=false (strict improvement under gate=off). This wire
    /// only carries STATE-SNAPSHOT data for routing + projection + reconnect
    /// catch-up. Resource mutations themselves propagate through standard
    /// <c>VesselResourceMsgData</c> — server-side
    /// <c>VesselMsgReader.RejectIfCrossAgencyWrite</c> (5.17a write-path
    /// counterpart, soak Finding-2) already blocks cross-agency relay so a
    /// bypassed-prefix client cannot leak resources across agencies.</para>
    ///
    /// <para><b>Wire shape (consumer-lens SF1).</b> Fields:
    /// <see cref="AgencyId"/> (Guid) + <see cref="EntryCount"/> (int) +
    /// <see cref="Entries"/>[0..EntryCount-1]. Each
    /// <see cref="AgencyOrbitalTransferEntry"/> carries 7 LOGICAL FIELDS
    /// (TransferGuid, OriginVesselId, DestinationVesselId, Status, StartTime,
    /// Duration, PayloadBytes) serialized as 8 WIRE ATOMS (the 7 fields
    /// plus a NumBytes length-prefix for the variable-length PayloadBytes
    /// tail). The distinction matters for wire-test byte-count assertions:
    /// pin against <see cref="AgencyOrbitalTransferEntry.GetByteCount"/>'s
    /// computed upper bound, not against a manual 7-field arithmetic.
    /// Field order is stable per
    /// <see cref="AgencyOrbitalTransferEntry.Serialize"/> /
    /// <see cref="AgencyOrbitalTransferEntry.Deserialize"/>. No QuickLZ
    /// compression at the message level — PayloadBytes per entry is already
    /// the decompressed TRANSFER ConfigNode form. If a future profiling pass
    /// shows the wire is bottlenecked, a single QuickLZ pass on the
    /// concatenated payload buffer is the natural follow-up (same shape as
    /// <see cref="AgencyContractMsgData"/>'s ContractInfo precedent —
    /// deferred until measured).</para>
    ///
    /// <para><b>Echo vs catch-up distinction.</b> The wire carries NO flag
    /// distinguishing a connect-time catch-up from a mid-session echo — both
    /// ride the same MsgData type. The client mirror (deferred to a
    /// 5.18-series follow-up) MUST be designed for idempotent
    /// upsert-by-key. The key is
    /// <see cref="AgencyOrbitalTransferEntry.TransferGuid"/> — direct
    /// Guid-keyed, distinct from Slice B (vessel-and-body-keyed string) and
    /// Slice C (body-and-resource-keyed string). A catch-up batch followed by
    /// an echo of an entry already in the catch-up must apply idempotently.
    /// Server-side router IS the canonical source; client's mirror is a pure
    /// projection.</para>
    ///
    /// <para><b>Cross-channel arrival ordering (5.18-series author):</b> on
    /// channel 22 (ReliableOrdered), the connect-time catchup batch arrives
    /// BEFORE any mid-session echo for the same agency. Mirror authors can
    /// apply each batch by TransferGuid with last-write-wins semantics safely;
    /// no out-of-order arrival between catch-up and echo is structurally
    /// possible per Lidgren's per-channel guarantee. Same shape applies to
    /// the sibling Slice B/C MsgData on the same channel.</para>
    ///
    /// <para><b>Catchup batches >MaxEntryCount are CHUNKED.</b>
    /// <c>SendOrbitalCatchupTo</c> splits a snapshot larger than
    /// <see cref="MaxEntryCount"/> into multiple consecutive messages on
    /// channel 22; Lidgren's per-channel ReliableOrdered guarantee
    /// preserves apply order across chunks. The mirror author MUST be
    /// prepared to receive N consecutive AgencyOrbitalStateMsgData on
    /// connect (each chunk carries its own EntryCount). Idempotent-upsert-
    /// by-TransferGuid handles the multi-chunk case naturally — no
    /// explicit "this is chunk i of N" framing is on the wire.</para>
    ///
    /// <para><b>Empty-batch asymmetry between sender paths
    /// (consumer-lens MF4).</b> Cross-reference for the 5.18-series mirror
    /// author + the Slice D-2 sender author:</para>
    /// <list type="bullet">
    ///   <item><c>AgencySystemSender.SendOrbitalStateToOwner</c> — used for
    ///        owner-only echo from <see cref="AgencyOrbitalStateMsgData"/>
    ///        upserts. <b>Early-returns on empty/null batch.</b> The router
    ///        only calls it when <c>accepted.Count > 0</c>, so an inbound
    ///        with all-rejected entries produces NO echo at all (no empty
    ///        message either).</item>
    ///   <item><c>AgencySystemSender.SendOrbitalCatchupTo</c> — used for
    ///        connect-time catchup from
    ///        <c>AgencyState.OrbitalTransfers</c>. <b>Fires unconditionally
    ///        under gate=on, including for an empty dict — ships one
    ///        zero-entry message.</b> The mirror needs the empty state to
    ///        distinguish "no per-agency transfers yet" from "unsynced /
    ///        catchup not yet received."</item>
    /// </list>
    /// <para>Slice D-2 author writing <c>AgencyOrbitalSender</c> should
    /// FOLLOW the SendOrbitalStateToOwner shape — early-return on empty
    /// batch — because the postfix never has anything to emit when empty.
    /// The asymmetry is by design + intentional, not a bug.</para>
    ///
    /// <para><b>Removal — two distinct mechanisms (consumer-lens MUST FIX
    /// MF2).</b> Orbital removal flows on two different paths; Slice E
    /// authors must not conflate them:</para>
    /// <list type="bullet">
    ///   <item><b>Read-side removal-via-projection is ALREADY complete in Slice D-1.</b>
    ///        <see cref="LmpCommon.Message.Data.Agency.AgencyOrbitalStateMsgData"/>'s
    ///        sibling projector splice on <c>ScenarioOrbitalLogistics</c>
    ///        (<c>AgencyScenarioProjector.SpliceAgencyOrbitalTransfers</c>)
    ///        strips ALL shared TRANSFER children unconditionally and
    ///        replaces with the per-agency set on every projection. A
    ///        transfer removed from <c>AgencyState.OrbitalTransfers</c>
    ///        simply disappears from the next scene-load's projected
    ///        scenario. No wire echo needed for the read-side privacy
    ///        contract.</item>
    ///   <item><b>Wire-side per-entry removal echo is deferred to Slice E
    ///        — only needed for admin-cascade scenarios.</b> If Slice E
    ///        ships a <c>deleteagency</c> cascade or
    ///        <c>cleanorbitaltransfers</c> admin command that needs to
    ///        push removals to a connected client's in-memory mirror MID-
    ///        SESSION (before the next scene-load picks up the projector
    ///        re-strip), a wire echo is needed. The recommended shape per
    ///        pre-spec §2.e is appending <c>Guid[] RemovedTransferGuids</c>
    ///        at this MsgData's tail with the forward-compat clause below
    ///        — <b>Guid-keyed removal tail</b>, distinct from Slice B/C
    ///        kolony's string-keyed precedent (which would be
    ///        <c>$"{vesselId:N}|{bodyIndex}"</c>) and planetary's
    ///        (<c>$"{bodyIndex}|{resourceName}"</c>). Slice D intentionally
    ///        does NOT pre-define the field per the YAGNI rule from
    ///        CLAUDE.md — wait for the actual Slice E consumer to define it.</item>
    /// </list>
    ///
    /// <para><b>Note for Slice E migration:</b> the per-router transferagency
    /// policy here is the most complex of the three — both move-and-keep
    /// (Origin matches moved vessel → stay in source agency) and move-with-
    /// vessel (Destination matches moved vessel → migrate to destination
    /// agency) cases apply per pre-spec §4.e. For self-transfer (both
    /// Origin and Destination equal the moved vessel) prefer the
    /// Destination-match path (deliverer authority).</para>
    ///
    /// <para><b>Forward-compatibility.</b> No room for new fields without a
    /// protocol bump — the trailing read is a count-driven array. Future
    /// additions append at the message tail with a
    /// <c>lidgrenMsg.Position &lt; lidgrenMsg.LengthBits</c> end-of-message
    /// guard, matching the <c>VesselProtoMsgData.Reason</c> precedent.</para>
    /// </summary>
    public class AgencyOrbitalStateMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyOrbitalStateMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.OrbitalState;

        /// <summary>
        /// On S→C (echo + catch-up): the receiving client's agency. On C→S
        /// (postfix mutation): the server IGNORES this field and derives the
        /// sender's agency authoritatively from
        /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. Documented
        /// in the type XML — clients cannot spoof attribution.
        /// </summary>
        public Guid AgencyId;
        public int EntryCount;
        public AgencyOrbitalTransferEntry[] Entries = new AgencyOrbitalTransferEntry[0];

        public override string ClassName { get; } = nameof(AgencyOrbitalStateMsgData);

        /// <summary>
        /// Upper bound on <see cref="EntryCount"/> on the wire. Orbital transfers
        /// are higher per-unit cost than kolony / planetary entries (each carries
        /// a ConfigNode-format <c>PayloadBytes</c> of a few hundred bytes), so
        /// the bound is tighter than Slice B/C's 4096. A megabase with multiple
        /// simultaneous orbital transfers across a long-lived save can produce
        /// hundreds of expired transfers over time but rarely thousands;
        /// 1024 leaves comfortable headroom without enabling a multi-MB DoS
        /// allocation. Same DoS-amplification class round-2 wire review caught
        /// on <see cref="AgencyContractMsgData.MaxContractCount"/>.
        /// </summary>
        public const int MaxEntryCount = 1024;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            // Precondition guards — same shape as
            // AgencyPlanetaryStateMsgData. EntryCount > Entries.Length would
            // throw IndexOutOfRangeException MID-SERIALIZE — after AgencyId +
            // EntryCount bytes had already been written to channel 22.
            // The receive side would then read EntryCount * entry-bytes of
            // following-message garbage, desyncing the channel. Explicit
            // throw before any bytes hit the wire is the defensible boundary.
            if (EntryCount < 0)
                throw new System.IO.InvalidDataException(
                    $"AgencyOrbitalState EntryCount must be non-negative: {EntryCount}");
            if (Entries == null || Entries.Length < EntryCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyOrbitalState EntryCount {EntryCount} exceeds Entries.Length {(Entries?.Length ?? 0)}");
            // [General-lens SF6] Symmetric send-side MaxEntryCount guard. Without
            // it, a sender that mistakenly produces EntryCount=5000 would silently
            // wire-emit; the RECEIVER (line 226-228) would throw InvalidDataException
            // asymmetrically — the producer never sees the error. Catching here means
            // a misconfigured catchup / migration caller fails fast on the emit side
            // with a clear diagnostic, matching the same boundary the receive side
            // enforces. Caller responsible for chunking large batches per the
            // SendOrbitalCatchupTo precedent.
            if (EntryCount > MaxEntryCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyOrbitalState EntryCount {EntryCount} exceeds MaxEntryCount {MaxEntryCount} — caller must chunk before send.");

            GuidUtil.Serialize(AgencyId, lidgrenMsg);
            lidgrenMsg.Write(EntryCount);
            // Caller contract: Entries[0..EntryCount-1] must be non-null. Same
            // "trust the caller" convention as Slice B/C — a sender that
            // null-skipped while emitting the original count would desync with
            // a receiver that reads EntryCount entries unconditionally,
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
                    $"AgencyOrbitalState EntryCount out of range: {EntryCount} (allowed 0..{MaxEntryCount})");

            if (Entries.Length < EntryCount)
                Entries = new AgencyOrbitalTransferEntry[EntryCount];

            for (var i = 0; i < EntryCount; i++)
            {
                if (Entries[i] == null)
                    Entries[i] = new AgencyOrbitalTransferEntry();

                Entries[i].Deserialize(lidgrenMsg);
            }
        }

        internal override int InternalGetMessageSize()
        {
            // Same precondition as InternalSerialize — wrong here would
            // undersize the Lidgren write buffer and corrupt the send.
            if (EntryCount < 0 || Entries == null || Entries.Length < EntryCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyOrbitalState GetByteCount precondition: EntryCount={EntryCount} Entries.Length={(Entries?.Length ?? 0)}");

            var arraySize = 0;
            for (var i = 0; i < EntryCount; i++)
            {
                arraySize += Entries[i].GetByteCount();
            }

            return base.InternalGetMessageSize() + GuidUtil.ByteSize + sizeof(int) + arraySize;
        }
    }
}
