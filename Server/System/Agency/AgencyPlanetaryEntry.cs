using System;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 3 ShareKolony — per-agency planetary-logistics warehouse record.
    /// Lives inside <see cref="AgencyState.PlanetaryEntries"/> AND is the
    /// wire entry type inside <c>AgencyPlanetaryStateMsgData.Entries</c>
    /// (single class used both ways per the Phase 3 pre-spec §2.e
    /// single-class-per-slot default). Mirrors MKS'
    /// <c>PlanetaryLogistics.PlanetaryLogisticsEntry</c> at pinned SHA
    /// <c>ed0f6aa6</c> with the addition of <see cref="OwningVesselId"/>:
    /// MKS' entry is body-resource-keyed only, but Phase 3 needs vessel-
    /// origin context to derive the owning agency, so the client postfix
    /// on <c>ModulePlanetaryLogistics.LevelResources</c> populates this
    /// from <c>this.vessel.id</c> at the per-mutation call site.
    ///
    /// **Partition key in <see cref="AgencyState.PlanetaryEntries"/>:**
    /// <c>$"{bodyIndex}|{resourceName}"</c>. Body-resource-keyed, NOT
    /// vessel-keyed — multiple of an agency's vessels pumping the same
    /// resource on the same body collapse into one entry. Per pre-spec
    /// §4.e: planetary entries do NOT migrate on <c>transferagency</c>
    /// (the entry represents a body's pool, not a vessel's contribution).
    /// </summary>
    public class AgencyPlanetaryEntry
    {
        public Guid OwningVesselId { get; set; }
        public int BodyIndex { get; set; }
        public string ResourceName { get; set; } = string.Empty;
        public double StoredQuantity { get; set; }
    }
}
