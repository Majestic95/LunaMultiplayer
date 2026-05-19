using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// [Phase 4 Slice A — WOLF] Round-trip pinning for the new
    /// <see cref="AgencyState.WolfDepots"/> / <see cref="AgencyState.WolfRoutes"/> /
    /// <see cref="AgencyState.WolfHoppers"/> / <see cref="AgencyState.WolfTerminals"/> /
    /// <see cref="AgencyState.WolfCrewRoutes"/> persistence sections. Mirrors
    /// <c>AgencyStateSCANsatRoundTripTest</c> / <c>AgencyStateDMagicRoundTripTest</c>
    /// (BUG-013 / Invariant 9 precedent) — every double + int round-trips under a
    /// non-en thread culture without corruption. Adds nested-list preservation cases
    /// (ResourceStreams under Depot, Resources under Route, Passengers under CrewRoute)
    /// + per-entry isolation cases (malformed entry skipped, siblings survive) +
    /// forward-compat case (pre-Phase-4 file loads as empty dicts).
    /// </summary>
    [TestClass]
    public class AgencyStateWolfRoundTripTest
    {
        private CultureInfo _originalCulture;

        [TestInitialize]
        public void Setup()
        {
            // Force the test thread to a comma-decimal culture so "R"+invariant
            // emit shows itself if a code path forgets the culture override.
            _originalCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        }

        [TestCleanup]
        public void Teardown()
        {
            Thread.CurrentThread.CurrentCulture = _originalCulture;
        }

        // -------------------------------------------------------------------
        // Depot round-trip
        // -------------------------------------------------------------------

        [TestMethod]
        public void Depot_FullFields_RoundTripWithNestedResourceStreams()
        {
            var agency = BuildAgency();
            var depot = new AgencyWolfDepotEntry
            {
                Body = "Duna",
                Biome = "Lowlands",
                IsEstablished = true,
                IsSurveyed = true,
                ResourceStreams = new List<AgencyWolfResourceStreamEntry>
                {
                    new AgencyWolfResourceStreamEntry { ResourceName = "Hydrates", Incoming = 1000, Outgoing = 250 },
                    new AgencyWolfResourceStreamEntry { ResourceName = "MaterialKits", Incoming = 50, Outgoing = 0 },
                },
            };
            agency.WolfDepots["Duna|Lowlands"] = depot;

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.WolfDepots.TryGetValue("Duna|Lowlands", out var entry), "Depot lost on round-trip");
            Assert.AreEqual("Duna", entry.Body);
            Assert.AreEqual("Lowlands", entry.Biome);
            Assert.IsTrue(entry.IsEstablished);
            Assert.IsTrue(entry.IsSurveyed);
            Assert.AreEqual(2, entry.ResourceStreams.Count);
            Assert.AreEqual("Hydrates", entry.ResourceStreams[0].ResourceName);
            Assert.AreEqual(1000, entry.ResourceStreams[0].Incoming);
            Assert.AreEqual(250, entry.ResourceStreams[0].Outgoing);
            Assert.AreEqual("MaterialKits", entry.ResourceStreams[1].ResourceName);
            Assert.AreEqual(50, entry.ResourceStreams[1].Incoming);
            Assert.AreEqual(0, entry.ResourceStreams[1].Outgoing);
        }

        // -------------------------------------------------------------------
        // Route round-trip
        // -------------------------------------------------------------------

        [TestMethod]
        public void Route_FullFields_RoundTripWithNestedResources()
        {
            var agency = BuildAgency();
            agency.WolfRoutes["Duna|Lowlands|Ike|Highlands"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Ike",
                DestinationBiome = "Highlands",
                Payload = 5000,
                Resources = new List<AgencyWolfRouteResourceEntry>
                {
                    new AgencyWolfRouteResourceEntry { ResourceName = "Fuel", Quantity = 2000 },
                    new AgencyWolfRouteResourceEntry { ResourceName = "Oxidizer", Quantity = 3000 },
                },
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.WolfRoutes.TryGetValue("Duna|Lowlands|Ike|Highlands", out var entry), "Route lost on round-trip");
            Assert.AreEqual("Duna", entry.OriginBody);
            Assert.AreEqual("Ike", entry.DestinationBody);
            Assert.AreEqual(5000, entry.Payload);
            Assert.AreEqual(2, entry.Resources.Count);
            Assert.AreEqual("Fuel", entry.Resources[0].ResourceName);
            Assert.AreEqual(2000, entry.Resources[0].Quantity);
            Assert.AreEqual("Oxidizer", entry.Resources[1].ResourceName);
            Assert.AreEqual(3000, entry.Resources[1].Quantity);
        }

        // -------------------------------------------------------------------
        // Hopper round-trip
        // -------------------------------------------------------------------

        [TestMethod]
        public void Hopper_PreservesGuidWithHyphensInId_DoesNotNormalize()
        {
            // WOLF's HopperMetadata.Id uses Guid.ToString() WITH hyphens
            // (HopperMetadata.cs:18). The round-trip must preserve this exact form;
            // do NOT normalize to "N" because that would lose the distinction from
            // Terminal Ids (which DO use "N") and break the dict key contract.
            var agency = BuildAgency();
            var hopperId = "550e8400-e29b-41d4-a716-446655440000";   // Guid with hyphens
            agency.WolfHoppers[hopperId] = new AgencyWolfHopperEntry
            {
                Id = hopperId,
                Body = "Mun",
                Biome = "Highlands",
                Recipe = "Hydrates,100,MetallicOre,50",
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.WolfHoppers.TryGetValue(hopperId, out var entry),
                "Hopper Id with hyphens lost on round-trip (key changed?)");
            Assert.AreEqual(hopperId, entry.Id);
            Assert.IsTrue(entry.Id.Contains("-"), "Hyphens must be preserved");
            Assert.AreEqual("Mun", entry.Body);
            Assert.AreEqual("Highlands", entry.Biome);
            Assert.AreEqual("Hydrates,100,MetallicOre,50", entry.Recipe);
        }

        // -------------------------------------------------------------------
        // Terminal round-trip
        // -------------------------------------------------------------------

        [TestMethod]
        public void Terminal_PreservesGuidNFormInId()
        {
            // TerminalMetadata.Id uses Guid.ToString("N") (no hyphens) per
            // TerminalMetadata.cs:15. Round-trip preserves this form.
            var agency = BuildAgency();
            var terminalId = "550e8400e29b41d4a716446655440000";   // Guid "N" form
            agency.WolfTerminals[terminalId] = new AgencyWolfTerminalEntry
            {
                Id = terminalId,
                Body = "Eve",
                Biome = "Foothills",
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.WolfTerminals.TryGetValue(terminalId, out var entry),
                "Terminal Id (N form) lost on round-trip");
            Assert.AreEqual(terminalId, entry.Id);
            Assert.IsFalse(entry.Id.Contains("-"), "N form has no hyphens");
            Assert.AreEqual("Eve", entry.Body);
            Assert.AreEqual("Foothills", entry.Biome);
        }

        // -------------------------------------------------------------------
        // CrewRoute round-trip (the heaviest entity)
        // -------------------------------------------------------------------

        [TestMethod]
        public void CrewRoute_FullFields_RoundTripWithNestedPassengers_InvariantCulture()
        {
            var agency = BuildAgency();
            var uniqueId = Guid.NewGuid().ToString("N");
            agency.WolfCrewRoutes[uniqueId] = new AgencyWolfCrewRouteEntry
            {
                ArrivalTime = 123456.789012,         // double; must round-trip under comma-decimal culture
                Duration = 21600.5,                  // double
                OriginBody = "Kerbin",
                OriginBiome = "Shores",
                DestinationBody = "Mun",
                DestinationBiome = "Crater",
                EconomyBerths = 4,
                LuxuryBerths = 2,
                FlightNumber = "7AB",
                FlightStatus = "Enroute",
                UniqueId = uniqueId,
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "Jebediah Kerman", DisplayName = "Jebediah Kerman", IsTourist = false, Occupation = "Pilot", Stars = 5 },
                    new AgencyWolfPassengerEntry { Name = "Tourist Alice", DisplayName = "Tourist Alice", IsTourist = true, Occupation = "Tourist", Stars = 0 },
                },
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.WolfCrewRoutes.TryGetValue(uniqueId, out var entry), "CrewRoute lost on round-trip");
            Assert.AreEqual(123456.789012, entry.ArrivalTime, 1e-6,
                "ArrivalTime must round-trip under comma-decimal culture (BUG-013 / Invariant 9)");
            Assert.AreEqual(21600.5, entry.Duration, 1e-6);
            Assert.AreEqual("Kerbin", entry.OriginBody);
            Assert.AreEqual("Mun", entry.DestinationBody);
            Assert.AreEqual(4, entry.EconomyBerths);
            Assert.AreEqual(2, entry.LuxuryBerths);
            Assert.AreEqual("7AB", entry.FlightNumber);
            Assert.AreEqual("Enroute", entry.FlightStatus);
            Assert.AreEqual(uniqueId, entry.UniqueId);

            Assert.AreEqual(2, entry.Passengers.Count);
            Assert.AreEqual("Jebediah Kerman", entry.Passengers[0].Name);
            Assert.IsFalse(entry.Passengers[0].IsTourist);
            Assert.AreEqual("Pilot", entry.Passengers[0].Occupation);
            Assert.AreEqual(5, entry.Passengers[0].Stars);
            Assert.AreEqual("Tourist Alice", entry.Passengers[1].Name);
            Assert.IsTrue(entry.Passengers[1].IsTourist);
        }

        // -------------------------------------------------------------------
        // Forward-compat
        // -------------------------------------------------------------------

        [TestMethod]
        public void Parse_PrePhase4File_NoWolfNodes_ProducesEmptyDicts()
        {
            // Forward-compat: an agency file written by a pre-Phase-4 server has
            // no WOLF_DEPOTS / WOLF_ROUTES / WOLF_HOPPERS / WOLF_TERMINALS /
            // WOLF_CREWROUTES nodes. Parse yields empty dicts without warning
            // or exception. Same forward-compat shape as the Phase 3
            // KOLONY_ENTRIES / SCAN_COVERAGE / DMAGIC_* sections.
            var raw = "AgencyId = " + Guid.NewGuid().ToString("N") + "\n" +
                      "OwningPlayerName = Pre-Phase-4 Player\n" +
                      "DisplayName = Pre-Phase-4 Agency\n" +
                      "Funds = 0\nScience = 0\nReputation = 0\n";

            var parsed = AgencyState.Parse(raw);

            Assert.AreEqual(0, parsed.WolfDepots.Count, "Pre-Phase-4 file must load empty WolfDepots");
            Assert.AreEqual(0, parsed.WolfRoutes.Count, "Pre-Phase-4 file must load empty WolfRoutes");
            Assert.AreEqual(0, parsed.WolfHoppers.Count, "Pre-Phase-4 file must load empty WolfHoppers");
            Assert.AreEqual(0, parsed.WolfTerminals.Count, "Pre-Phase-4 file must load empty WolfTerminals");
            Assert.AreEqual(0, parsed.WolfCrewRoutes.Count, "Pre-Phase-4 file must load empty WolfCrewRoutes");
        }

        // -------------------------------------------------------------------
        // Per-entry isolation
        // -------------------------------------------------------------------

        [TestMethod]
        public void Parse_MalformedDepotEntry_SkipsItKeepsSiblings()
        {
            // Per-entry isolation: a WOLF_DEPOT entry missing Body OR Biome
            // skips with a Warning; sibling entries in the same WOLF_DEPOTS
            // block survive. Mirrors KOLONY parse per-entry isolation
            // (Invariant 4).
            var agency = BuildAgency();
            var raw = string.Format(CultureInfo.InvariantCulture,
                "AgencyId = {0}\nOwningPlayerName = {1}\nDisplayName = {2}\nFunds = 0\nScience = 0\nReputation = 0\n" +
                "WOLF_DEPOTS\n{{\n" +
                "\tWOLF_DEPOT\n\t{{\n\t\tBody = \n\t\tBiome = Lowlands\n\t\tIsEstablished = True\n\t}}\n" +
                "\tWOLF_DEPOT\n\t{{\n\t\tBody = Mun\n\t\tBiome = Highlands\n\t\tIsEstablished = True\n\t\tIsSurveyed = False\n\t}}\n" +
                "}}\n",
                agency.AgencyId.ToString("N", CultureInfo.InvariantCulture),
                agency.OwningPlayerName,
                agency.DisplayName);

            var parsed = AgencyState.Parse(raw);

            Assert.AreEqual(1, parsed.WolfDepots.Count,
                "Bad entry skipped; good sibling survives");
            Assert.IsTrue(parsed.WolfDepots.ContainsKey("Mun|Highlands"));
        }

        [TestMethod]
        public void Parse_MalformedCrewRouteEntry_MissingUniqueId_SkipsKeepsSiblings()
        {
            // CrewRoute parse-time per-entry isolation: missing UniqueId skips
            // the slot; sibling entries survive. UniqueId is the dict key —
            // without it the entry has no identity.
            var agency = BuildAgency();
            var goodUid = Guid.NewGuid().ToString("N");
            var raw = string.Format(CultureInfo.InvariantCulture,
                "AgencyId = {0}\nOwningPlayerName = {1}\nDisplayName = {2}\nFunds = 0\nScience = 0\nReputation = 0\n" +
                "WOLF_CREWROUTES\n{{\n" +
                "\tWOLF_CREWROUTE\n\t{{\n\t\tArrivalTime = 0\n\t\tDuration = 0\n\t\tOriginBody = K\n\t\tOriginBiome = Sh\n\t\tDestinationBody = M\n\t\tDestinationBiome = Cr\n\t\tFlightNumber = ABC\n\t\tFlightStatus = Boarding\n\t\tUniqueId = \n\t}}\n" +
                "\tWOLF_CREWROUTE\n\t{{\n\t\tArrivalTime = 100\n\t\tDuration = 50\n\t\tOriginBody = K\n\t\tOriginBiome = Sh\n\t\tDestinationBody = M\n\t\tDestinationBiome = Cr\n\t\tFlightNumber = XYZ\n\t\tFlightStatus = Enroute\n\t\tUniqueId = {3}\n\t}}\n" +
                "}}\n",
                agency.AgencyId.ToString("N", CultureInfo.InvariantCulture),
                agency.OwningPlayerName,
                agency.DisplayName,
                goodUid);

            var parsed = AgencyState.Parse(raw);

            Assert.AreEqual(1, parsed.WolfCrewRoutes.Count,
                "Bad entry skipped; good sibling survives");
            Assert.IsTrue(parsed.WolfCrewRoutes.ContainsKey(goodUid));
        }

        // -------------------------------------------------------------------
        // Empty-dict + non-empty-mix
        // -------------------------------------------------------------------

        [TestMethod]
        public void Mixed_AllFiveTypesPopulated_RoundTrip()
        {
            // End-to-end pin: all 5 WOLF dicts populated with one entry each;
            // single round-trip preserves the entire set. Mirrors the "smoke
            // test" pattern from AgencyStateTest.
            var agency = BuildAgency();
            agency.WolfDepots["Kerbin|Shores"] = new AgencyWolfDepotEntry
            {
                Body = "Kerbin", Biome = "Shores", IsEstablished = true,
            };
            agency.WolfRoutes["A|B|C|D"] = new AgencyWolfRouteEntry
            {
                OriginBody = "A", OriginBiome = "B", DestinationBody = "C", DestinationBiome = "D", Payload = 100,
            };
            var hopperId = "abc12345-6789-1234-5678-90abcdef1234";
            agency.WolfHoppers[hopperId] = new AgencyWolfHopperEntry
            {
                Id = hopperId, Body = "Mun", Biome = "Crater", Recipe = "Ore,100",
            };
            var terminalId = "abc1234567891234567890abcdef12345";
            agency.WolfTerminals[terminalId] = new AgencyWolfTerminalEntry
            {
                Id = terminalId, Body = "Mun", Biome = "Crater",
            };
            var crewRouteId = Guid.NewGuid().ToString("N");
            agency.WolfCrewRoutes[crewRouteId] = new AgencyWolfCrewRouteEntry
            {
                ArrivalTime = 1000.5, Duration = 200.25,
                OriginBody = "K", OriginBiome = "Sh", DestinationBody = "M", DestinationBiome = "Cr",
                EconomyBerths = 2, LuxuryBerths = 1, FlightNumber = "MIX", FlightStatus = "Arrived",
                UniqueId = crewRouteId,
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.AreEqual(1, parsed.WolfDepots.Count);
            Assert.AreEqual(1, parsed.WolfRoutes.Count);
            Assert.AreEqual(1, parsed.WolfHoppers.Count);
            Assert.AreEqual(1, parsed.WolfTerminals.Count);
            Assert.AreEqual(1, parsed.WolfCrewRoutes.Count);
            Assert.IsTrue(parsed.WolfHoppers[hopperId].Id.Contains("-"), "Hopper Id retains hyphens");
            Assert.IsFalse(parsed.WolfTerminals[terminalId].Id.Contains("-"), "Terminal Id stays N-form (no hyphens)");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyState BuildAgency()
        {
            return new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "WolfAlice",
                DisplayName = "Wolf Alice Logistics Co",
            };
        }
    }
}
