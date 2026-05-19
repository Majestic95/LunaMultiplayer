namespace Server.System.Agency
{
    /// <summary>
    /// [Mod-compat S4 — DMagic Orbital Science] Per-agency, per-asteroid science
    /// diminishing-returns record. One entry per <c>Asteroid_Science → DM_Science</c>
    /// node in the inbound DMScienceScenario blob. Stored in
    /// <see cref="AgencyState.DMagicAsteroidScience"/> keyed by <see cref="Title"/>
    /// (Ordinal — DMagic's <c>DMScienceScenario.recoveredDMScience</c> is a
    /// <c>Dictionary&lt;string, DMScienceData&gt;</c> keyed by the same title
    /// string at <c>Source/Scenario/DMScienceScenario.cs:49</c>).
    ///
    /// <para><b>Field types verified against</b> <c>Source/Scenario/DMScienceData.cs:39-40</c>
    /// (verbatim: <c>private float scival, science, cap, basevalue;</c>) — all four
    /// numeric fields are <c>float</c>, NOT double. Decision §A (audit re-walk
    /// 2026-05-19) corrects the 2026-05-18 audit's claim that these were doubles.</para>
    ///
    /// <para><b>Wire shape</b> (verified <c>DMScienceScenario.OnSave:75-91</c> +
    /// <c>OnLoad:150-157</c>):
    /// <code>
    /// DM_Science {
    ///   title = ...
    ///   bsv = ...   // BaseValue
    ///   scv = ...   // SciVal
    ///   sci = ...   // Science (running accumulator)
    ///   cap = ...   // Cap
    /// }
    /// </code></para>
    /// </summary>
    public class AgencyDMagicAsteroidEntry
    {
        /// <summary>Maps to DMagic's <c>DM_Science → title</c>. Dict key in <see cref="AgencyState.DMagicAsteroidScience"/>.</summary>
        public string Title;

        /// <summary>Maps to <c>bsv</c>. The asteroid's base science value (per-asteroid constant).</summary>
        public float BaseValue;

        /// <summary>Maps to <c>scv</c>. DMagic's "scival" — diminishing-returns scalar (clamped 0..1 per <c>DMScienceData.SciVal</c> setter at <c>:60-68</c>).</summary>
        public float SciVal;

        /// <summary>Maps to <c>sci</c>. Running accumulator of recovered science (clamped >=0 per <c>DMScienceData.Science</c> setter at <c>:70-78</c>).</summary>
        public float Science;

        /// <summary>Maps to <c>cap</c>. Maximum recoverable science for this asteroid.</summary>
        public float Cap;
    }
}
