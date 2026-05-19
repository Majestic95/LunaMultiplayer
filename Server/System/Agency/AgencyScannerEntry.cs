using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// [Mod-compat S2 — SCANsat] Per-agency, per-vessel active-scanner record.
    /// One entry per <c>Scanners → Vessel</c> node in the inbound SCANcontroller
    /// blob. Stored in <see cref="AgencyState.Scanners"/> keyed by
    /// <see cref="VesselId"/> (Guid).
    ///
    /// <para><b>Decision §9 (audit re-walk 2026-05-19).</b> Vessel record
    /// carries a nested list of <see cref="AgencyScannerSensorRecord"/> —
    /// a single vessel may run 1-N sensors (altimetry + survey + resource
    /// scanner simultaneously). Flat fields cannot represent this shape.
    /// The 2026-05-18 WebFetch-based audit had a flat shape; the local-clone
    /// re-walk against SCANsat SHA <c>0d67371</c> at
    /// <c>SCANcontroller.cs:797-806</c> (verbatim
    /// <c>foreach (SCANsensor sensor in sv.sensors) { ... node_vessel.AddNode(node_sensor); }</c>)
    /// caught the error.</para>
    ///
    /// <para><b>Decision §3 — vessel-keyed migration under
    /// <c>transferagency</c>.</b> When an admin runs
    /// <c>transferagency &lt;vessel&gt; A→B</c>, this entry moves from
    /// <c>AgencyState[A].Scanners[VesselId]</c> to
    /// <c>AgencyState[B].Scanners[VesselId]</c> with the nested
    /// <see cref="Sensors"/> list intact. Per-body <c>Coverage</c> does
    /// NOT migrate (body-keyed, agency-scoped — A's discoveries on Eve
    /// stay A's). See <see cref="AgencyScanRouter.MigrateForVesselTransfer"/>.</para>
    /// </summary>
    public class AgencyScannerEntry
    {
        /// <summary>Maps to SCANsat's <c>Vessel → guid</c> field (lowercase 'guid' on the wire). Indexed by this value in <see cref="AgencyState.Scanners"/>.</summary>
        public Guid VesselId;

        /// <summary>Maps to SCANsat's <c>Vessel → name</c> field. Informational — SCANsat's <c>OnLoad</c> re-derives the vessel reference from <c>FlightGlobals</c> at load time, so this string is not load-bearing. May be null/empty.</summary>
        public string VesselName;

        /// <summary>One <see cref="AgencyScannerSensorRecord"/> per active sensor on the vessel. May be empty (vessel registered but no sensors active yet). Order is not load-bearing (SCANsat treats sensors as a set).</summary>
        public List<AgencyScannerSensorRecord> Sensors = new List<AgencyScannerSensorRecord>();
    }
}
