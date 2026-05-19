using Lidgren.Network;
using System.Collections.Generic;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — per-agency crew-route record. Single class used both as the
    /// wire entry inside <see cref="AgencyWolfCrewRouteStateMsgData.Entries"/> AND
    /// as the value type for the server-side <c>AgencyState.WolfCrewRoutes</c>
    /// dictionary. Mirrors WOLF's <c>CrewRoute</c> shape persisted via
    /// <c>CrewRoute.OnSave</c> at MKS SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\CrewRoute.cs:253-276</c>).
    ///
    /// <para><b>Partition key in <c>AgencyState.WolfCrewRoutes</c>:</b>
    /// <see cref="UniqueId"/> (Guid in <c>ToString("N")</c> form — matches WOLF's
    /// <c>Guid.NewGuid().ToString("N")</c> at <c>CrewRoute.cs:90</c>).</para>
    ///
    /// <para><b>Persisted form (in <c>{guid}.txt</c>):</b> one
    /// <c>WOLF_CREWROUTE</c> sub-node per entry under the parent
    /// <c>WOLF_CREWROUTES</c> node, with <see cref="Passengers"/> emitted as a
    /// nested <c>WOLF_PASSENGERS</c> child containing one <c>WOLF_PASSENGER</c>
    /// sub-node per entry. Fields verified at pre-spec §2.f.vii.</para>
    ///
    /// <para><b>Culture-sensitivity:</b> <see cref="ArrivalTime"/> and
    /// <see cref="Duration"/> are <c>double</c> — wire serialization is
    /// culture-invariant (Lidgren binary), but the ConfigNode persistence path
    /// in <c>AgencyState</c> must use <c>CultureInfo.InvariantCulture</c> per
    /// the BUG-013 family / Invariant 9 precedent (mirror
    /// <c>AgencyState.cs:1009-1019</c> <c>ParseDoubleOrZero</c> helper).</para>
    ///
    /// <para><b>FlightStatus</b> is serialized as an enum-name string to match
    /// WOLF's <c>CrewRoute.OnSave</c> at line 262 (which writes
    /// <c>routeNode.AddValue(nameof(FlightStatus), FlightStatus)</c>; WOLF
    /// stringifies the <c>WOLFUI.FlightStatus</c> enum value). Forward-compat
    /// against future WOLF enum reordering: the string form survives reorders
    /// (the int form would not).</para>
    /// </summary>
    public class AgencyWolfCrewRouteEntry
    {
        public double ArrivalTime { get; set; }
        public string OriginBody { get; set; } = string.Empty;
        public string OriginBiome { get; set; } = string.Empty;
        public string DestinationBody { get; set; } = string.Empty;
        public string DestinationBiome { get; set; } = string.Empty;
        public double Duration { get; set; }
        public int EconomyBerths { get; set; }
        public int LuxuryBerths { get; set; }

        /// <summary>WOLF's <c>FlightNumber</c> — 3-char namespace, display-only. Per pre-spec §1.c known limitation: <c>UniqueId</c> is the correctness anchor.</summary>
        public string FlightNumber { get; set; } = string.Empty;

        /// <summary>WOLF's <c>WOLFUI.FlightStatus</c> enum serialized as enum-name string ("Boarding" / "Enroute" / "Arrived" / "Unknown").</summary>
        public string FlightStatus { get; set; } = string.Empty;

        /// <summary>WOLF's <c>CrewRoute.UniqueId</c> — Guid in <c>ToString("N")</c> form. Dict key + correctness anchor for the route.</summary>
        public string UniqueId { get; set; } = string.Empty;

        public List<AgencyWolfPassengerEntry> Passengers { get; set; } = new List<AgencyWolfPassengerEntry>();

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(ArrivalTime);
            lidgrenMsg.Write(OriginBody ?? string.Empty);
            lidgrenMsg.Write(OriginBiome ?? string.Empty);
            lidgrenMsg.Write(DestinationBody ?? string.Empty);
            lidgrenMsg.Write(DestinationBiome ?? string.Empty);
            lidgrenMsg.Write(Duration);
            lidgrenMsg.Write(EconomyBerths);
            lidgrenMsg.Write(LuxuryBerths);
            lidgrenMsg.Write(FlightNumber ?? string.Empty);
            lidgrenMsg.Write(FlightStatus ?? string.Empty);
            lidgrenMsg.Write(UniqueId ?? string.Empty);

            var passengers = Passengers ?? new List<AgencyWolfPassengerEntry>();
            lidgrenMsg.Write(passengers.Count);
            foreach (var p in passengers)
            {
                (p ?? new AgencyWolfPassengerEntry()).Serialize(lidgrenMsg);
            }
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            ArrivalTime = lidgrenMsg.ReadDouble();
            OriginBody = lidgrenMsg.ReadString();
            OriginBiome = lidgrenMsg.ReadString();
            DestinationBody = lidgrenMsg.ReadString();
            DestinationBiome = lidgrenMsg.ReadString();
            Duration = lidgrenMsg.ReadDouble();
            EconomyBerths = lidgrenMsg.ReadInt32();
            LuxuryBerths = lidgrenMsg.ReadInt32();
            FlightNumber = lidgrenMsg.ReadString();
            FlightStatus = lidgrenMsg.ReadString();
            UniqueId = lidgrenMsg.ReadString();

            var passengerCount = lidgrenMsg.ReadInt32();
            if (passengerCount < 0 || passengerCount > MaxPassengersPerCrewRoute)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfCrewRouteEntry passenger count out of range: {passengerCount} (allowed 0..{MaxPassengersPerCrewRoute})");

            Passengers = new List<AgencyWolfPassengerEntry>(passengerCount);
            for (var i = 0; i < passengerCount; i++)
            {
                var entry = new AgencyWolfPassengerEntry();
                entry.Deserialize(lidgrenMsg);
                Passengers.Add(entry);
            }
        }

        public int GetByteCount()
        {
            var oBodyLen = OriginBody?.Length ?? 0;
            var oBiomeLen = OriginBiome?.Length ?? 0;
            var dBodyLen = DestinationBody?.Length ?? 0;
            var dBiomeLen = DestinationBiome?.Length ?? 0;
            var fnLen = FlightNumber?.Length ?? 0;
            var fsLen = FlightStatus?.Length ?? 0;
            var uidLen = UniqueId?.Length ?? 0;
            var passengersSize = sizeof(int);
            if (Passengers != null)
            {
                foreach (var p in Passengers)
                {
                    passengersSize += (p ?? new AgencyWolfPassengerEntry()).GetByteCount();
                }
            }
            return sizeof(double) * 2          // ArrivalTime + Duration
                + 5 + oBodyLen * 4
                + 5 + oBiomeLen * 4
                + 5 + dBodyLen * 4
                + 5 + dBiomeLen * 4
                + sizeof(int) * 2              // EconomyBerths + LuxuryBerths
                + 5 + fnLen * 4
                + 5 + fsLen * 4
                + 5 + uidLen * 4
                + passengersSize;
        }

        /// <summary>
        /// DoS-amplification cap on per-route passenger list. A CrewRoute's berth
        /// count is bounded by WOLF's UI; 64 is generous overhead.
        /// </summary>
        public const int MaxPassengersPerCrewRoute = 64;
    }
}
