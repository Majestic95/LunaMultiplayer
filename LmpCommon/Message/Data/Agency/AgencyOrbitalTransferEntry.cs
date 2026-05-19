using Lidgren.Network;
using LmpCommon.Message.Base;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 3 ShareKolony — per-agency orbital-logistics transfer record. Single
    /// class used both as the wire entry inside <see cref="AgencyOrbitalStateMsgData.Entries"/>
    /// AND as the value type for the server-side <c>AgencyState.OrbitalTransfers</c>
    /// dictionary. The 7-field shape mirrors the persistent fields of MKS'
    /// <c>KolonyTools.OrbitalLogisticsTransferRequest</c> at pinned SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\KolonyTools\OrbitalLogistics\OrbitalLogisticsTransferRequest.cs</c>);
    /// a future MKS field rename is the Phase 3 brittleness surface flagged in
    /// <c>docs/research/mks-lmp-compatibility-phase-3-prespec.md</c> §6 item 4.
    ///
    /// <para><b>Lives in LmpCommon, not Server.</b> The single-class-per-slot rule
    /// (pre-spec §2.e) requires the wire MsgData (LmpCommon) to reference the
    /// entry type directly, which forbids placing the entry in <c>Server.System.Agency</c>
    /// (Server depends on LmpCommon, not the other way). Slice A staged the file
    /// under <c>Server/System/Agency/</c> provisionally; Slice D moves it here as
    /// part of bringing the wire surface online — same MOVE pattern Slice B
    /// applied to <see cref="AgencyKolonyEntry"/> and Slice C applied to
    /// <see cref="AgencyPlanetaryEntry"/>. The <c>AgencyState.OrbitalTransfers</c>
    /// dictionary continues to use this type, just via the existing
    /// <c>using LmpCommon.Message.Data.Agency;</c> import in
    /// <c>Server/System/Agency/AgencyState.cs</c>.</para>
    ///
    /// <para><b>Partition key in <c>AgencyState.OrbitalTransfers</c>:</b>
    /// <see cref="TransferGuid"/>. Stable across the transfer's lifetime
    /// (Launched → Delivered / Failed / Returning / Cancelled). Distinct from
    /// Slice B kolony (vessel-and-body-keyed string) and Slice C planetary
    /// (body-and-resource-keyed string) — orbital is the only Phase 3 partition
    /// keyed directly by a Guid, no string composition needed.</para>
    ///
    /// <para><b><see cref="OriginVesselId"/> + <see cref="DestinationVesselId"/></b>
    /// are the canonical Guid form derived client-side via
    /// <c>OrbitalLogisticsTransferRequest.Origin/Destination</c> accessor then
    /// <c>vessel.id</c> (NOT <c>persistentId</c>) — see pre-spec §2.b.iii
    /// "Vessel-id derivation" for the resolution path. The wire never carries
    /// MKS' persistentId-string representation; the
    /// <c>ResolveDestinationVesselGuid</c> pure helper translates at the
    /// postfix boundary.</para>
    ///
    /// <para><b><see cref="Status"/></b> is an opaque int — the raw value of
    /// MKS' <c>KolonyTools.DeliveryStatus</c> enum
    /// (PreLaunch=0, Launched=1, Cancelled=2, Partial=3, Delivered=4,
    /// Failed=5, Returning=6 at pinned SHA <c>ed0f6aa6</c>). The server
    /// stores opaquely; the client maps to/from MKS' enum at the postfix +
    /// projector boundary. A future MKS reorder would break wire
    /// compatibility — the brittleness is flagged in pre-spec §6.</para>
    ///
    /// <para><b><see cref="PayloadBytes"/></b> carries the original MKS transfer's
    /// persistent serialization (the result of
    /// <c>OrbitalLogisticsTransferRequest.Save</c>'s TRANSFER ConfigNode) opaque
    /// to LMP. The projector splices these bytes back into the outgoing
    /// <c>ScenarioOrbitalLogistics</c> blob so KSP-side
    /// <c>OnLoad</c>/<c>OnSave</c> round-trip cleanly. Per pre-spec §3.c:
    /// defensive-copy on store (<c>Buffer.BlockCopy</c>) to avoid alias-mutation
    /// hazards from re-arrivals or operator hand-edits — Slice D's entry is the
    /// FIRST Phase 3 entry with a mutable byte-array field, so the
    /// defensive-copy concern applies here uniquely.</para>
    ///
    /// <para><b>Persisted form (in <c>{guid}.txt</c>):</b> one <c>TRANSFER</c>
    /// sub-node per entry under the parent <c>ORBITAL_TRANSFERS</c> node, shipped
    /// in Slice A. PayloadBytes is Base64-encoded decompressed form (matches the
    /// 5.17d Contracts pattern — operators diffing two AgencyState files see
    /// readable payloads; compression is a wire-only concern). Persistence shape
    /// lives in <c>Server/System/Agency/AgencyState.cs</c> Slice A's
    /// <c>ToConfigNode</c> / <c>FromConfigNode</c> blocks at lines 469-496 /
    /// 875-918; this class only owns the in-memory + wire-serialization shape.
    /// The three Guids are persisted as <c>"N"</c> form (32 hex chars, no
    /// hyphens) for round-trip consistency with the agency-file naming
    /// convention.</para>
    ///
    /// <para><b>Wire-serialization contract.</b> <see cref="Serialize"/> writes
    /// TransferGuid (16 bytes via <see cref="GuidUtil"/>) + OriginVesselId (16) +
    /// DestinationVesselId (16) + Status (int) + StartTime (double) + Duration
    /// (double) + NumBytes (int) + PayloadBytes[0..NumBytes-1] in stable field
    /// order. <see cref="Deserialize"/> reads in the same order.
    /// <see cref="GetByteCount"/> upper-bounds the Lidgren write buffer. No
    /// per-entry compression — PayloadBytes is already the decompressed form;
    /// callers can pre-compress via QuickLZ if a future profiling pass shows it
    /// pays off (same deferral pattern as <see cref="AgencyContractMsgData"/>'s
    /// ContractInfo).</para>
    /// </summary>
    public class AgencyOrbitalTransferEntry
    {
        /// <summary>
        /// Pinned symbolic names for MKS' <c>KolonyTools.DeliveryStatus</c> enum
        /// values at SHA <c>ed0f6aa6</c>. Pre-spec §6 brittleness item: a future
        /// MKS enum reorder breaks wire compatibility — these constants give a
        /// single edit-once site if that happens, and let Slice D routers /
        /// projector splices / Deliver-prefix decision helper reference symbolic
        /// names instead of magic ints.
        /// </summary>
        public const int StatusPreLaunch = 0;
        public const int StatusLaunched = 1;
        public const int StatusCancelled = 2;
        public const int StatusPartial = 3;
        public const int StatusDelivered = 4;
        public const int StatusFailed = 5;
        public const int StatusReturning = 6;

        public Guid TransferGuid { get; set; }
        public Guid OriginVesselId { get; set; }
        public Guid DestinationVesselId { get; set; }
        public int Status { get; set; }
        public double StartTime { get; set; }
        public double Duration { get; set; }
        public byte[] PayloadBytes { get; set; } = Array.Empty<byte>();
        public int NumBytes { get; set; }

        /// <summary>
        /// Upper bound on <see cref="NumBytes"/> on the wire. A pending-transfer
        /// list is bounded by MKS' UI (an operator can't queue thousands per
        /// vessel) and each transfer's PayloadBytes is a few hundred bytes of
        /// resource-request ConfigNode — 64 KiB is generously above the worst
        /// observed payload. Prevents a malicious peer from forcing a multi-GB
        /// allocation by shipping <see cref="int.MaxValue"/>.
        /// </summary>
        public const int MaxPayloadBytes = 65536;

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            GuidUtil.Serialize(TransferGuid, lidgrenMsg);
            GuidUtil.Serialize(OriginVesselId, lidgrenMsg);
            GuidUtil.Serialize(DestinationVesselId, lidgrenMsg);
            lidgrenMsg.Write(Status);
            lidgrenMsg.Write(StartTime);
            lidgrenMsg.Write(Duration);

            // PayloadBytes/NumBytes contract matches AgencyContractEntry's
            // (5.17d ContractInfo): NumBytes can be < PayloadBytes.Length if
            // the buffer is over-allocated; clamp on write so the receiver
            // reads exactly what we wrote. A NumBytes that exceeds the array
            // is a server-side construction bug — clamp to array length
            // rather than throw mid-serialize so we don't desync channel 22.
            var payload = PayloadBytes ?? Array.Empty<byte>();
            var len = Math.Min(NumBytes, payload.Length);
            if (len < 0) len = 0;
            lidgrenMsg.Write(len);
            if (len > 0)
                lidgrenMsg.Write(payload, 0, len);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            TransferGuid = GuidUtil.Deserialize(lidgrenMsg);
            OriginVesselId = GuidUtil.Deserialize(lidgrenMsg);
            DestinationVesselId = GuidUtil.Deserialize(lidgrenMsg);
            Status = lidgrenMsg.ReadInt32();
            StartTime = lidgrenMsg.ReadDouble();
            Duration = lidgrenMsg.ReadDouble();

            var len = lidgrenMsg.ReadInt32();
            if (len < 0 || len > MaxPayloadBytes)
                throw new System.IO.InvalidDataException(
                    $"AgencyOrbitalTransferEntry NumBytes out of range: {len} (allowed 0..{MaxPayloadBytes})");

            NumBytes = len;
            PayloadBytes = len > 0 ? lidgrenMsg.ReadBytes(len) : Array.Empty<byte>();
        }

        public int GetByteCount()
        {
            // Upper bound on serialized bytes. Three Guids + Status + StartTime +
            // Duration + NumBytes prefix + PayloadBytes body.
            var len = Math.Max(0, Math.Min(NumBytes, (PayloadBytes?.Length ?? 0)));
            return GuidUtil.ByteSize * 3      // Three Guids
                + sizeof(int)                  // Status
                + sizeof(double) * 2           // StartTime + Duration
                + sizeof(int)                  // NumBytes length prefix
                + len;                         // Payload body
        }
    }
}
