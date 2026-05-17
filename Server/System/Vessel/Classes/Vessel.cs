using LunaConfigNode;
using LunaConfigNode.CfgNode;
using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Server.System.Vessel.Classes
{
    public class Vessel
    {
        /// <summary>
        /// Top-level field name LMP uses on the vessel ConfigNode to record the
        /// AuthoritativeSubspaceId. Prefixed with `lmp` so KSP's vessel loader ignores
        /// it as an unknown field. See BUG-005/006.
        /// </summary>
        public const string AuthSubspaceFieldName = "lmpAuthSubspace";

        /// <summary>
        /// Top-level field name LMP uses on the vessel ConfigNode to record the
        /// owning AgencyId under per-agency career mode. Same `lmp`-prefix rationale
        /// as <see cref="AuthSubspaceFieldName"/> — KSP's vessel loader ignores the
        /// unknown field, so our addition round-trips through any KSP-side
        /// persistence path. Stage 5.16b (spec §12 step 7).
        /// </summary>
        public const string OwningAgencyFieldName = "lmpOwningAgency";

        public MixedCollection<string, string> Fields;
        public MixedCollection<uint, Part> Parts;
        public MixedCollection<string, string> Orbit;
        public MixedCollection<string, string> ActionGroups;

        public readonly ConfigNode Discovery;
        public readonly ConfigNode FlightPlan;
        public readonly ConfigNode Target;
        public readonly ConfigNode Waypoint;
        public readonly ConfigNode CtrlState;
        public readonly ConfigNode VesselModules;

        /// <summary>
        /// The subspace whose timeline is canonical for this vessel. 0 = no authority yet
        /// (vessel exists but no proto-update has been received). Stored as the
        /// `lmpAuthSubspace` top-level field of the vessel ConfigNode and round-tripped
        /// via <see cref="Fields"/>. See docs/research/02-analysis/bug-005-006-cross-subspace-lock.md.
        /// </summary>
        public int AuthoritativeSubspaceId
        {
            get => int.TryParse(Fields.GetSingle(AuthSubspaceFieldName)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            set
            {
                var asString = value.ToString(CultureInfo.InvariantCulture);
                if (Fields.Exists(AuthSubspaceFieldName))
                    Fields.Update(AuthSubspaceFieldName, asString);
                else
                    Fields.Add(new CfgNodeValue<string, string>(AuthSubspaceFieldName, asString));
            }
        }

        /// <summary>
        /// The agency that owns this vessel under per-agency career mode.
        /// <see cref="Guid.Empty"/> = unassigned (vessel pre-dates per-agency, or the gate
        /// is off, or admin de-assigned). Stored as the "N" format Guid string on the
        /// <see cref="OwningAgencyFieldName"/> top-level field so the on-disk form is
        /// the same 32-char hex used by <c>Universe/Agencies/{guid}.txt</c> filenames.
        /// See spec §10 Q3 (unassigned-sentinel handling) + §12 step 7 (this field).
        ///
        /// **Sentinel convention.** Stage 5.16b treats <see cref="Guid.Empty"/> as the
        /// "Unassigned" agency directly — no <see cref="Server.System.Agency.AgencyState"/>
        /// object is created for it. Stage 5.17a's <see cref="Server.System.LockSystem"/>
        /// cross-agency rejection will special-case <see cref="Guid.Empty"/> as "any agency
        /// may interact." Future admin de-assignment (Stage 5.18d) writes the all-zero
        /// 32-char hex string back into this field rather than removing it; the setter
        /// matches that shape so the on-disk form is symmetric with
        /// <see cref="AuthoritativeSubspaceId"/>=0.
        /// </summary>
        public Guid OwningAgencyId
        {
            get => Guid.TryParseExact(Fields.GetSingle(OwningAgencyFieldName)?.Value, "N", out var v) ? v : Guid.Empty;
            set
            {
                var asString = value.ToString("N", CultureInfo.InvariantCulture);
                if (Fields.Exists(OwningAgencyFieldName))
                    Fields.Update(OwningAgencyFieldName, asString);
                else
                    Fields.Add(new CfgNodeValue<string, string>(OwningAgencyFieldName, asString));
            }
        }

        public Vessel(ConfigNode cfgNode)
        {
            Fields = new MixedCollection<string, string>(cfgNode.GetAllValues());
            Parts = new MixedCollection<uint, Part>(cfgNode.GetNodes("PART").Select(n => new CfgNodeValue<uint, Part>(uint.Parse(n.Value.GetValues("uid")[0].Value), new Part(n.Value))));
            Orbit = new MixedCollection<string, string>(cfgNode.GetNodes("ORBIT").First().Value.GetAllValues().Select(n => new CfgNodeValue<string, string>(n.Key, n.Value)));
            ActionGroups = new MixedCollection<string, string>(cfgNode.GetNodes("ACTIONGROUPS").First().Value.GetAllValues().Select(n => new CfgNodeValue<string, string>(n.Key, n.Value)));

            Discovery = cfgNode.GetNodes("DISCOVERY").First().Value;
            FlightPlan = cfgNode.GetNodes("FLIGHTPLAN").First().Value;
            Target = cfgNode.GetNodes("TARGET").FirstOrDefault()?.Value;
            Waypoint = cfgNode.GetNodes("WAYPOINT").FirstOrDefault()?.Value;
            CtrlState = cfgNode.GetNodes("CTRLSTATE").First().Value;
            VesselModules = cfgNode.GetNodes("VESSELMODULES").First().Value;
        }

        public Vessel(string vesselText) : this(new ConfigNode(vesselText)) { }

        public Part GetPart(uint partFlightId)
        {
            return Parts.GetSingle(partFlightId).Value;
        }

        public string GetOrbitingBodyName()
        {
            var body = Orbit.GetSingle("body")?.Value;
            if (!string.IsNullOrEmpty(body)) return body;

            var ident = Orbit.GetSingle("IDENT")?.Value;
            if (!string.IsNullOrEmpty(ident))
            {
                // IDENT format is usually "Squad/Sun" or "OPM/Sarnus"
                var parts = ident.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) return parts.Last();
            }

            return "Unknown";
        }

        public override string ToString()
        {
            var builder = new StringBuilder();

            CfgNodeWriter.WriteValues(Fields.GetAll(), 0, builder);

            CfgNodeWriter.InitializeNode("ORBIT", 0, builder);
            CfgNodeWriter.WriteValues(Orbit.GetAll(), 1, builder);
            CfgNodeWriter.FinishNode(0, builder);
            builder.AppendLine();

            foreach (var part in Parts.GetAllValues())
            {
                builder.AppendLine(part.ToString());
            }

            CfgNodeWriter.InitializeNode("ACTIONGROUPS", 0, builder);
            CfgNodeWriter.WriteValues(ActionGroups.GetAll(), 1, builder);
            CfgNodeWriter.FinishNode(0, builder);
            builder.AppendLine();

            builder.AppendLine(CfgNodeWriter.WriteConfigNode(Discovery));
            builder.AppendLine(CfgNodeWriter.WriteConfigNode(FlightPlan));
            if (Target != null) builder.AppendLine(CfgNodeWriter.WriteConfigNode(Target));
            if (Waypoint != null) builder.AppendLine(CfgNodeWriter.WriteConfigNode(Waypoint));
            builder.AppendLine(CfgNodeWriter.WriteConfigNode(CtrlState));
            builder.AppendLine(CfgNodeWriter.WriteConfigNode(VesselModules));

            return builder.ToString().TrimEnd();
        }
    }
}
