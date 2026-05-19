namespace Server.System.Agency
{
    /// <summary>
    /// [Mod-compat S4 — DMagic Orbital Science] Per-agency, per-anomaly record
    /// (the "you've discovered the Mun monolith" log). Stored in
    /// <see cref="AgencyState.DMagicAnomalies"/> keyed by composite
    /// <c>$"{BodyIndex}|{Name}"</c> Ordinal string.
    ///
    /// <para><b>Storage vs wire shape — flattened on disk, 2-level nested on
    /// wire.</b> Decision §B (audit re-walk 2026-05-19). The wire shape
    /// (verified <c>DMScienceScenario.OnSave:92-119</c>) is:
    /// <code>
    /// Anomaly_Records {
    ///   DM_Anomaly_List {       // one wrapper per celestial body
    ///     Body = N               // flightGlobalsIndex
    ///     DM_Anomaly {           // 1-N anomaly records inside the per-body wrapper
    ///       Name = ...
    ///       Lat = ...   // "N5" format on stock DMagic; projector uses "R" + InvariantCulture (Invariant 9)
    ///       Lon = ...
    ///       Alt = ...
    ///     }
    ///   }
    /// }
    /// </code>
    /// We flatten to <c>Dictionary&lt;string, AgencyDMagicAnomalyEntry&gt;</c>
    /// with composite key <c>"$bodyIndex|$name"</c> for storage convenience
    /// (mirrors the kolony precedent's <c>"$vesselId|$bodyIndex"</c> shape on
    /// <see cref="AgencyState.KolonyEntries"/>). The projector splice
    /// reconstructs the nested wire shape on emit by grouping entries by
    /// <see cref="BodyIndex"/>.</para>
    ///
    /// <para><b>Anomalies are deterministic per KSP world state</b>, not
    /// random — they come from <c>PQSSurfaceObject[]</c> baked into each
    /// CelestialBody's definition. So an "agency starts with empty anomalies"
    /// state means "this agency hasn't discovered any anomalies yet," not
    /// "the world doesn't have any anomalies." Decision §2 still applies
    /// (each agency starts at zero discovery state).</para>
    ///
    /// <para><b>Lat/Lon/Alt doubles round-trip via <c>InvariantCulture</c> + "R"</b>
    /// on the projector emit per Invariant 9 (BUG-013 precedent). Stock DMagic
    /// writes "N5" which is culture-sensitive; the fork's stricter "R" + invariant
    /// roundtrip is defense against comma-decimal server locales corrupting
    /// coordinates on round-trip. DMagic's <c>parse("Lat", (double)0)</c> at
    /// <c>OnLoad:174-176</c> accepts both formats so the higher-precision
    /// output is forward-compat.</para>
    /// </summary>
    public class AgencyDMagicAnomalyEntry
    {
        /// <summary>Maps to <c>DM_Anomaly_List → Body</c>. The celestial body's <c>flightGlobalsIndex</c>; used as the first half of the composite dict key.</summary>
        public int BodyIndex;

        /// <summary>Maps to <c>DM_Anomaly → Name</c>. The anomaly name (e.g. "Monolith01"); used as the second half of the composite dict key.</summary>
        public string Name;

        /// <summary>Maps to <c>DM_Anomaly → Lat</c>. Latitude on the body's surface (degrees).</summary>
        public double Latitude;

        /// <summary>Maps to <c>DM_Anomaly → Lon</c>. Longitude on the body's surface (degrees).</summary>
        public double Longitude;

        /// <summary>Maps to <c>DM_Anomaly → Alt</c>. Altitude above the body's surface (metres).</summary>
        public double Altitude;
    }
}
