namespace Server.System.Agency
{
    /// <summary>
    /// Phase 3 ShareKolony — per-agency kolonization record. Lives inside
    /// <see cref="AgencyState.KolonyEntries"/> AND is shipped as the wire
    /// entry type inside <c>AgencyKolonyStateMsgData.Entries</c> (single
    /// class used both ways — no compression boundary on the kolony entry,
    /// unlike Stage 5.17d's <see cref="AgencyContractEntry"/> vs
    /// <c>ContractInfo</c> split for QuickLZ payloads, and unlike Phase 3's
    /// own <see cref="AgencyOrbitalTransferEntry.PayloadBytes"/> which IS a
    /// compressed-MKS-payload analogue). The 13-field shape mirrors MKS'
    /// <c>KolonyTools.KolonizationEntry</c> at pinned SHA <c>ed0f6aa6</c>;
    /// a future MKS field rename is the Phase 3 brittleness surface
    /// flagged in <c>docs/research/mks-lmp-compatibility-phase-3-prespec.md</c>
    /// §6 item 4.
    ///
    /// **Partition key in <see cref="AgencyState.KolonyEntries"/>:**
    /// <c>$"{vesselId:N}|{bodyIndex}"</c>. The vessel-keyed partition lets
    /// admin <c>transferagency</c> migrate kolony research with the vessel
    /// (operator sign-off session 25 Q1). <see cref="VesselId"/> is stored
    /// as a string matching the MKS-side field type, but populated by the
    /// client postfix as <c>vessel.id.ToString("N")</c> (Guid form).
    ///
    /// **Persisted form (in <c>{guid}.txt</c>):** one <c>KOLONY</c> sub-node
    /// per entry under the parent <c>KOLONY_ENTRIES</c> node. Plain numeric
    /// values (no Base64 — the entry is not a wire-compressed payload like
    /// contracts).
    /// </summary>
    public class AgencyKolonyEntry
    {
        public string VesselId { get; set; } = string.Empty;
        public int BodyIndex { get; set; }
        public double LastUpdate { get; set; }
        public double KolonyDate { get; set; }
        public double GeologyResearch { get; set; }
        public double BotanyResearch { get; set; }
        public double KolonizationResearch { get; set; }
        public double Science { get; set; }
        public double Reputation { get; set; }
        public double Funds { get; set; }
        public int RepBoosters { get; set; }
        public int FundsBoosters { get; set; }
        public int ScienceBoosters { get; set; }
    }
}
