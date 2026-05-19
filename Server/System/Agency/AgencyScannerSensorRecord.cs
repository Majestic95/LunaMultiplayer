namespace Server.System.Agency
{
    /// <summary>
    /// [Mod-compat S2 — SCANsat] One active sensor on a scanner vessel. Mirrors
    /// SCANsat's <c>Scanners → Vessel → Sensor</c> child node verbatim. A single
    /// vessel may run multiple sensors simultaneously (altimetry + survey +
    /// resource scanner on the same satellite is common); each sensor gets its
    /// own record inside the parent <see cref="AgencyScannerEntry.Sensors"/>
    /// list. Decision §9 (audit re-walk 2026-05-19) — the 2026-05-18 audit
    /// flattened these into Vessel scalars which cannot represent multi-sensor
    /// vessels; the nested-list shape is correct.
    ///
    /// <para><b>Field-name mapping</b> matches SCANsat's lowercase-underscore
    /// convention: the projector emits <c>type</c>/<c>fov</c>/<c>min_alt</c>/
    /// <c>max_alt</c>/<c>best_alt</c>/<c>require_light</c> when splicing back
    /// into the SCANcontroller blob, so KSP-side
    /// <c>SCANcontroller.OnLoad</c>'s <c>node_sensor.parse("type", ...)</c>
    /// reads the values into the right fields.</para>
    /// </summary>
    public class AgencyScannerSensorRecord
    {
        /// <summary>Maps to SCANsat's <c>Sensor → type</c> field (a <c>SCANtype</c> enum value cast to int).</summary>
        public int SensorType;

        /// <summary>Maps to <c>fov</c>. Field-of-view in degrees. Stored as
        /// <c>double</c> to match SCANsat's <c>SCANsensor.fov</c> source type
        /// (<c>SCANcontroller.cs:2362</c>) — the on-wire <c>parse("fov", 3d)</c>
        /// reads as double. The narrowing-to-float on the in-memory side would
        /// be harmless at typical fov values (3-30 degrees) but the wider type
        /// matches the source-of-truth.</summary>
        public double Fov;

        /// <summary>Maps to <c>min_alt</c>. Minimum operational altitude in metres.</summary>
        public double MinAlt;

        /// <summary>Maps to <c>max_alt</c>. Maximum operational altitude in metres.</summary>
        public double MaxAlt;

        /// <summary>Maps to <c>best_alt</c>. Optimal scanning altitude in metres.</summary>
        public double BestAlt;

        /// <summary>Maps to <c>require_light</c>. Whether the sensor only operates in sunlight.</summary>
        public bool RequireLight;
    }
}
