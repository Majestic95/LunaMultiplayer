using System;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 3 ShareKolony — per-agency orbital-logistics transfer record.
    /// Lives inside <see cref="AgencyState.OrbitalTransfers"/> AND is the
    /// wire entry type inside <c>AgencyOrbitalStateMsgData.Entries</c>
    /// (single class used both ways per the Phase 3 pre-spec §2.e
    /// single-class-per-slot default).
    ///
    /// **Partition key in <see cref="AgencyState.OrbitalTransfers"/>:**
    /// <see cref="TransferGuid"/>. Stable across the transfer's
    /// lifetime (Launched → Delivered / Failed / Returning / Cancelled).
    ///
    /// **<see cref="OriginVesselId"/> + <see cref="DestinationVesselId"/>**
    /// are the canonical Guid form derived client-side via
    /// <c>OrbitalLogisticsTransferRequest.Origin/Destination</c> accessor
    /// then <c>vessel.id</c> (NOT <c>persistentId</c>) — see pre-spec
    /// §2.b.iii "Vessel-id derivation" for the resolution path.
    ///
    /// **<see cref="Status"/>** is an opaque int — the raw value of MKS'
    /// <c>KolonyTools.DeliveryStatus</c> enum
    /// (PreLaunch=0, Launched=1, Cancelled=2, Partial=3, Delivered=4,
    /// Failed=5, Returning=6 at pinned SHA <c>ed0f6aa6</c>). The server
    /// stores opaquely; the client maps to/from MKS' enum at the postfix
    /// + projector boundary. A future MKS reorder would break wire
    /// compatibility — the brittleness is flagged in pre-spec §6.
    ///
    /// **<see cref="PayloadBytes"/>** carries the original MKS transfer's
    /// persistent serialization (the result of
    /// <c>OrbitalLogisticsTransferRequest.Save</c>) opaque to LMP. The
    /// projector splices this bytes back into the outgoing
    /// <c>ScenarioOrbitalLogistics</c> blob so KSP-side
    /// <c>OnLoad</c>/<c>OnSave</c> round-trip cleanly. Per pre-spec
    /// §3.c: defensive-copy on store (<c>Buffer.BlockCopy</c>) to avoid
    /// alias-mutation hazards from re-arrivals or operator hand-edits.
    /// </summary>
    public class AgencyOrbitalTransferEntry
    {
        /// <summary>
        /// Pinned symbolic names for MKS' <c>KolonyTools.DeliveryStatus</c> enum
        /// values at SHA <c>ed0f6aa6</c>. Pre-spec §6 brittleness item: a future
        /// MKS enum reorder breaks wire compatibility — these constants give a
        /// single edit-once site if that happens, and let Slice B/D routers /
        /// projector splices reference symbolic names instead of magic ints.
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
    }
}
