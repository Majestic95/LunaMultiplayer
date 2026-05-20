using Lidgren.Network;
using System.Collections.Generic;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — per-agency cargo-route record. Single class used both as the
    /// wire entry inside <see cref="AgencyWolfRouteStateMsgData.Entries"/> AND as
    /// the value type for the server-side <c>AgencyState.WolfRoutes</c> dictionary.
    /// Mirrors WOLF's <c>Route</c> shape persisted via <c>Route.OnSave</c> at MKS
    /// SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Route.cs:188-206</c>).
    ///
    /// <para><b>Partition key in <c>AgencyState.WolfRoutes</c>:</b>
    /// <c>$"{OriginBody}|{OriginBiome}|{DestinationBody}|{DestinationBiome}"</c>
    /// (Ordinal compare). The 4-string composite mirrors WOLF's own
    /// <c>ScenarioPersister.GetRoute</c> + <c>HasRoute</c> lookup semantics.</para>
    ///
    /// <para><b>Two distinct ConfigNode shapes — DO NOT CONFLATE</b>:
    /// <list type="bullet">
    ///   <item><b>Disk-side (in <c>Universe/Agencies/{guid}.txt</c>):</b>
    ///        one <c>WOLF_ROUTE</c> sub-node per entry under the parent
    ///        <c>WOLF_ROUTES</c> node; <see cref="Resources"/> emit as a
    ///        nested <c>WOLF_ROUTE_RESOURCES</c> child containing one
    ///        <c>WOLF_ROUTE_RESOURCE</c> sub-node per entry. See
    ///        <c>AgencyState.SaveAgency</c> / <c>LoadAgency</c>.</item>
    ///   <item><b>Wire/scenario-projection side (in the
    ///        <c>WOLF_ScenarioModule</c> blob emitted by
    ///        <c>AgencyScenarioProjector.SpliceAgencyWolfState</c>):</b> the
    ///        nesting tags MUST match what WOLF's <c>Route.OnLoad</c> at
    ///        <c>Route.cs:164-186</c> reads — namely <c>ROUTE</c> sub-nodes
    ///        under a <c>ROUTES</c> parent, with <see cref="Resources"/>
    ///        emitted as nested <c>RESOURCE</c> children directly under
    ///        each ROUTE (no intermediate RESOURCES wrapper). Field names
    ///        are 1:1 PascalCase on both sides
    ///        (<c>OriginBody</c>/<c>OriginBiome</c>/<c>DestinationBody</c>/
    ///        <c>DestinationBiome</c>/<c>Payload</c> on the route;
    ///        <c>ResourceName</c>/<c>Quantity</c> on each resource).</item>
    /// </list>
    /// The disk format is internal to LMP; the wire/projection format
    /// satisfies WOLF's parse contract. Fields verified at pre-spec
    /// §2.f.iii against WOLF SHA <c>ed0f6aa6</c>.</para>
    /// </summary>
    public class AgencyWolfRouteEntry
    {
        public string OriginBody { get; set; } = string.Empty;
        public string OriginBiome { get; set; } = string.Empty;
        public string DestinationBody { get; set; } = string.Empty;
        public string DestinationBiome { get; set; } = string.Empty;
        public int Payload { get; set; }
        public List<AgencyWolfRouteResourceEntry> Resources { get; set; } = new List<AgencyWolfRouteResourceEntry>();

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(OriginBody ?? string.Empty);
            lidgrenMsg.Write(OriginBiome ?? string.Empty);
            lidgrenMsg.Write(DestinationBody ?? string.Empty);
            lidgrenMsg.Write(DestinationBiome ?? string.Empty);
            lidgrenMsg.Write(Payload);

            var resources = Resources ?? new List<AgencyWolfRouteResourceEntry>();
            lidgrenMsg.Write(resources.Count);
            foreach (var r in resources)
            {
                (r ?? new AgencyWolfRouteResourceEntry()).Serialize(lidgrenMsg);
            }
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            OriginBody = lidgrenMsg.ReadString();
            OriginBiome = lidgrenMsg.ReadString();
            DestinationBody = lidgrenMsg.ReadString();
            DestinationBiome = lidgrenMsg.ReadString();
            Payload = lidgrenMsg.ReadInt32();

            var resourceCount = lidgrenMsg.ReadInt32();
            if (resourceCount < 0 || resourceCount > MaxResourcesPerRoute)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfRouteEntry resource count out of range: {resourceCount} (allowed 0..{MaxResourcesPerRoute})");

            Resources = new List<AgencyWolfRouteResourceEntry>(resourceCount);
            for (var i = 0; i < resourceCount; i++)
            {
                var entry = new AgencyWolfRouteResourceEntry();
                entry.Deserialize(lidgrenMsg);
                Resources.Add(entry);
            }
        }

        public int GetByteCount()
        {
            var oBodyLen = OriginBody?.Length ?? 0;
            var oBiomeLen = OriginBiome?.Length ?? 0;
            var dBodyLen = DestinationBody?.Length ?? 0;
            var dBiomeLen = DestinationBiome?.Length ?? 0;
            var resourcesSize = sizeof(int);    // count prefix
            if (Resources != null)
            {
                foreach (var r in Resources)
                {
                    resourcesSize += (r ?? new AgencyWolfRouteResourceEntry()).GetByteCount();
                }
            }
            return 5 + oBodyLen * 4
                + 5 + oBiomeLen * 4
                + 5 + dBodyLen * 4
                + 5 + dBiomeLen * 4
                + sizeof(int)                // Payload
                + resourcesSize;
        }

        /// <summary>
        /// DoS-amplification cap on the per-route resource list. A route's payload
        /// allocation is bounded by WOLF's UI; 256 is generous overhead.
        /// </summary>
        public const int MaxResourcesPerRoute = 256;
    }
}
