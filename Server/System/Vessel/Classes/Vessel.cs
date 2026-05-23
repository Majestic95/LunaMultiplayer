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
        /// Phase 2 of server-side-offload — atomic string cache of the celestial body
        /// this vessel is currently orbiting. WRITTEN by
        /// <see cref="Server.System.Vessel.VesselDataUpdater"/> under the per-vessel
        /// <c>Semaphore</c> (proto ingest path) and
        /// <see cref="Server.System.Vessel.VesselDataUpdater.WritePositionDataToFile"/>
        /// under the same semaphore (Position update path).
        ///
        /// READ lock-free from <see cref="Server.Server.MessageQueuer.ResolveSenderBody"/>
        /// on the Lidgren receive thread during same-body relay decisions. Reference
        /// assignment to a <c>string</c> field is atomic in .NET (ECMA-335 §I.12.6.6
        /// — aligned reference loads and stores are atomic up to native word size).
        ///
        /// Phase-2 review M1 fix: the prior implementation read
        /// <see cref="GetOrbitingBodyName"/> on the receive thread, which enumerates
        /// the <c>Orbit</c> MixedCollection — racy with the WritePositionDataToFile
        /// background task that mutates Orbit.IDENT inside the per-vessel semaphore.
        /// Same shape as BUG-033's race-on-ConfigNode.ToString. The cached field
        /// removes the MixedCollection traversal entirely; reader sees either the
        /// stale-but-coherent previous value or the new value, never torn state.
        ///
        /// <c>null</c> = body unknown (vessel just minted via proto, no position
        /// update has landed yet) → same-body filter falls back to permissive.
        /// </summary>
        public string CurrentBodyName { get; set; }

        /// <summary>
        /// Phase 3 of server-side-offload — millisecond timestamp of the last Position
        /// relay decision for this vessel, read from <see cref="Server.Context.ServerContext.ServerClock"/>.
        /// Drives the per-vessel cadence-by-lock-holder throttle: when no client holds the
        /// Control lock on this vessel, the server downsamples Position relays to one per
        /// (<see cref="Server.Settings.Definition.IntervalSettingsDefinition.SecondaryVesselUpdatesMsInterval"/>
        /// * <see cref="Server.Settings.Definition.OptimizationSettingsDefinition.UnpilotedVesselCadenceMultiplier"/>) ms.
        /// Default cadence: 150ms × 5 = 750ms (vs the baseline 50ms primary cadence — ~93%
        /// reduction in inactive-vessel relay volume).
        ///
        /// Written + read on the Lidgren receive thread (single-threaded sequential
        /// dispatch per <c>LidgrenServer.StartReceivingMessagesAsync</c>); no lock
        /// required. 0 = no relay decision recorded yet → first inbound relays
        /// unconditionally.
        /// </summary>
        public long LastRelayedPositionMs { get; set; }

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
