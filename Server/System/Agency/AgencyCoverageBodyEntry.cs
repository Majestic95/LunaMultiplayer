namespace Server.System.Agency
{
    /// <summary>
    /// [Mod-compat S2 — SCANsat] Per-agency, per-body coverage state for the
    /// SCANsat <c>SCANcontroller</c> scenario. One entry per <c>Progress → Body</c>
    /// node in the inbound blob. Stored in <see cref="AgencyState.Coverage"/>
    /// keyed by <see cref="BodyName"/> (Ordinal compare — SCANsat's
    /// <c>SCANcontroller.OnLoad</c> matches body names ordinal via
    /// <c>FlightGlobals.Bodies.FirstOrDefault(b =&gt; b.bodyName == body_name)</c>).
    ///
    /// <para><b>Decision §8 (audit re-walk 2026-05-19).</b> Carries the FULL
    /// SCANsat <c>Body</c> shape, not just the <see cref="Map"/> coverage blob.
    /// Per-body palette + terrain-range fields are runtime-mutable per-player
    /// UI preferences (SCANsat's body-config UI lets the player tweak these),
    /// so partitioning the whole Body node — rather than carving out just Map —
    /// matches "1 player ↔ 1 agency under gate=on" semantics. Per-body
    /// wire-bytes overhead is trivial (~8 small strings × bodies × agencies).</para>
    ///
    /// <para><b><see cref="Map"/> is opaque.</b> SCANsat produces it via
    /// <c>SCANdata.shortSerialize()</c>: Base64-encoded CLZF2-compressed
    /// BinaryFormatter-serialized <c>Int16[360,180]</c> with URL-safe char
    /// substitution (<c>/</c>→<c>-</c>, <c>=</c>→<c>_</c>). The fork-side
    /// router/state/projector NEVER decode this; round-trip as a string
    /// end-to-end. The Base64-URL-safe alphabet (<c>A-Za-z0-9-_</c>) contains
    /// no ConfigNode-special characters so no escape concerns apply.</para>
    ///
    /// <para><b>Optional fields.</b> <see cref="ClampHeight"/> and
    /// <see cref="LandingTarget"/> are emitted by SCANsat only when non-null
    /// (ClampHeight is per-body terrain config; LandingTarget appears when
    /// MechJeb is loaded AND the player has set a landing waypoint on the
    /// body). Serialize emits the field ONLY when non-null; Parse yields null
    /// when the field is absent. Round-trip preserves null vs populated.</para>
    /// </summary>
    public class AgencyCoverageBodyEntry
    {
        /// <summary>Maps to SCANsat's <c>Body → Name</c> field (the CelestialBody.bodyName).</summary>
        public string BodyName;

        /// <summary>Maps to <c>Disabled</c>.</summary>
        public bool Disabled;

        /// <summary>Maps to <c>MinHeightRange</c>. Per-body terrain palette lower bound.</summary>
        public float MinHeightRange;

        /// <summary>Maps to <c>MaxHeightRange</c>. Per-body terrain palette upper bound.</summary>
        public float MaxHeightRange;

        /// <summary>Maps to <c>ClampHeight</c>. Nullable — SCANsat emits only when its <c>TerrainConfig.ClampTerrain</c> is non-null. Serialize omits the field when null; Parse yields null when absent.</summary>
        public float? ClampHeight;

        /// <summary>Maps to <c>PaletteName</c>.</summary>
        public string PaletteName;

        /// <summary>Maps to <c>PaletteSize</c>.</summary>
        public int PaletteSize;

        /// <summary>Maps to <c>PaletteReverse</c>.</summary>
        public bool PaletteReverse;

        /// <summary>Maps to <c>PaletteDiscrete</c>.</summary>
        public bool PaletteDiscrete;

        /// <summary>Maps to <c>Map</c>. Opaque Base64-CLZF2-BinaryFormatter blob — never decoded fork-side. May be null/empty for an "agency aware of this body but with zero scan progress" state.</summary>
        public string Map;

        /// <summary>Maps to <c>LandingTarget</c>. Nullable — SCANsat emits only when MechJeb is loaded AND a vessel waypoint exists on this body. Combined "lat,lon" string format (e.g. "12.3400,-45.6700"). Serialize omits when null; Parse yields null when absent.</summary>
        public string LandingTarget;
    }
}
