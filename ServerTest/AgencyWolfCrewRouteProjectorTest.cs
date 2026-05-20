using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Collections.Generic;

namespace ServerTest
{
    /// <summary>
    /// [Phase 4 Slice E] Unit tests for the CREWROUTES splice in
    /// <see cref="AgencyScenarioProjector"/>'s <c>SpliceAgencyWolfState</c>.
    /// CrewRoutes are FK-coupled to depots via WOLF's <c>CrewRoute.OnLoad</c>
    /// at <c>CrewRoute.cs:249-250</c> — <c>_registry.GetDepot</c> throws
    /// <c>DepotDoesNotExistException</c> on FK miss for EITHER origin or
    /// destination, killing the whole WOLF scenario load. The projector
    /// pre-empts that loss by FK-sweeping against the just-emitted
    /// per-agency depot pool. Mirrors the Slice C ROUTES sweep semantics
    /// (origin + destination strict FK).
    /// </summary>
    [TestClass]
    public class AgencyWolfCrewRouteProjectorTest
    {
        [TestInitialize]
        public void Setup()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();
        }

        [TestCleanup]
        public void Teardown()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            AgencySystem.Reset();
        }

        [TestMethod]
        public void Project_AgencyWithValidCrewRoute_SplicesWithPassengers()
        {
            var agency = BuildAgencyWithTwoDepots();
            var routeId = Guid.NewGuid().ToString("N");
            agency.WolfCrewRoutes[routeId] = new AgencyWolfCrewRouteEntry
            {
                UniqueId = routeId,
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                FlightNumber = "3K7",
                FlightStatus = "Enroute",
                ArrivalTime = 99999.0,
                Duration = 360.0,
                EconomyBerths = 4,
                LuxuryBerths = 1,
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "Jebediah Kerman", DisplayName = "Jeb", IsTourist = false, Occupation = "Pilot", Stars = 5 },
                    new AgencyWolfPassengerEntry { Name = "Touristina", DisplayName = "Tourist Lina", IsTourist = true, Occupation = "Tourist", Stars = 0 },
                },
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, "CREWROUTES", "CREWROUTES container must appear.");
            // Per-entry container is "ROUTE" (NOT "CREWROUTE") per
            // WOLF's CrewRoute.OnSave at CrewRoute.cs:255 (the
            // ROUTE_NODE_NAME const at CrewRoute.cs:45).
            StringAssert.Contains(result, $"UniqueId = {routeId}",
                "UniqueId must be emitted in N form verbatim.");
            StringAssert.Contains(result, "FlightStatus = Enroute",
                "FlightStatus must persist as enum-name string per WOLF source contract.");
            StringAssert.Contains(result, "FlightNumber = 3K7");
            StringAssert.Contains(result, "EconomyBerths = 4");
            StringAssert.Contains(result, "LuxuryBerths = 1");
            // Nested PASSENGERS → PASSENGER × N
            StringAssert.Contains(result, "PASSENGERS", "PASSENGERS container must appear.");
            StringAssert.Contains(result, "Name = Jebediah Kerman");
            StringAssert.Contains(result, "Name = Touristina");
            StringAssert.Contains(result, "IsTourist = True",
                "Bool serialized invariant-culture (True/False) — matches WOLF's bool.Parse expectations.");
            StringAssert.Contains(result, "Stars = 5");
            StringAssert.Contains(result, "Occupation = Pilot");
        }

        [TestMethod]
        public void Project_CrewRouteWithMissingOriginDepot_DroppedByFKSweep()
        {
            // FK integrity gate: WOLF's CrewRoute.OnLoad calls
            // _registry.GetDepot(OriginBody, OriginBiome) which throws on
            // miss. Projector pre-empts by dropping the offending entry.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            // Only DESTINATION depot exists; origin is missing.
            agency.WolfDepots["Mun|MidlandCraters"] = new AgencyWolfDepotEntry { Body = "Mun", Biome = "MidlandCraters" };
            var routeId = Guid.NewGuid().ToString("N");
            agency.WolfCrewRoutes[routeId] = new AgencyWolfCrewRouteEntry
            {
                UniqueId = routeId,
                OriginBody = "Kerbin", OriginBiome = "KSC", // no Kerbin|KSC depot
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                FlightStatus = "Boarding",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains($"UniqueId = {routeId}"),
                "Orphan crew route (missing origin depot) must be FK-swept out.");
            Assert.IsFalse(result.Contains("CREWROUTES"),
                "Empty CREWROUTES container must not be emitted (lazy-allocate).");
        }

        [TestMethod]
        public void Project_CrewRouteWithMissingDestinationDepot_DroppedByFKSweep()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            // Only ORIGIN depot exists; destination is missing.
            agency.WolfDepots["Kerbin|KSC"] = new AgencyWolfDepotEntry { Body = "Kerbin", Biome = "KSC" };
            var routeId = Guid.NewGuid().ToString("N");
            agency.WolfCrewRoutes[routeId] = new AgencyWolfCrewRouteEntry
            {
                UniqueId = routeId,
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters", // no Mun|MidlandCraters depot
                FlightStatus = "Boarding",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains($"UniqueId = {routeId}"),
                "Orphan crew route (missing destination depot) must be FK-swept out.");
        }

        [TestMethod]
        public void Project_DepotKeySetReusedAcrossAllThreeFKConsumers()
        {
            // The depotKeySet is shared by Routes (Slice C) + Hoppers
            // (Slice D) + CrewRoutes (Slice E). Co-emit verifies all three
            // FK consumers reach the same depot-pool view and the lazy-
            // build local function fires once.
            var agency = BuildAgencyWithTwoDepots();
            agency.WolfRoutes["Kerbin|KSC|Mun|MidlandCraters"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                Payload = 1500,
            };
            var hopperId = Guid.NewGuid().ToString();
            agency.WolfHoppers[hopperId] = new AgencyWolfHopperEntry
            {
                Id = hopperId, Body = "Kerbin", Biome = "KSC", Recipe = "Hydrates,100",
            };
            var routeId = Guid.NewGuid().ToString("N");
            agency.WolfCrewRoutes[routeId] = new AgencyWolfCrewRouteEntry
            {
                UniqueId = routeId,
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                FlightStatus = "Boarding",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, "ROUTES");
            StringAssert.Contains(result, "Payload = 1500");
            StringAssert.Contains(result, "HOPPERS");
            StringAssert.Contains(result, $"Id = {hopperId}");
            StringAssert.Contains(result, "CREWROUTES");
            StringAssert.Contains(result, $"UniqueId = {routeId}");
        }

        [TestMethod]
        public void Project_LazyBuildOfDepotKeySet_WhenRoutesAndHoppersEmptyButCrewRoutesPresent()
        {
            // CrewRoutes are the 3rd FK consumer. When the first two
            // (Routes + Hoppers) are empty, depotKeySet is still null
            // when CrewRoutes runs — the local function builds it here.
            // Verifies the BuildDepotKeySet extraction (replacing the
            // 7-line duplicated build) works for the cold case.
            var agency = BuildAgencyWithTwoDepots();
            var routeId = Guid.NewGuid().ToString("N");
            agency.WolfCrewRoutes[routeId] = new AgencyWolfCrewRouteEntry
            {
                UniqueId = routeId,
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                FlightStatus = "Boarding",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("ROUTES\n"), "Empty WolfRoutes — no ROUTES emit.");
            Assert.IsFalse(result.Contains("HOPPERS"), "Empty WolfHoppers — no HOPPERS emit.");
            StringAssert.Contains(result, "CREWROUTES", "CrewRoute FK passes even without prior consumers having run.");
            StringAssert.Contains(result, $"UniqueId = {routeId}");
        }

        [TestMethod]
        public void Project_StripsSharedCREWROUTES_BeforeSplicing()
        {
            // Pre-existing shared-input CREWROUTES from peer agencies must
            // be stripped first — peer's crew routes (and their passenger
            // lists — kerbal-stranding hazard if leaked) must never appear
            // even when this agency has its own valid crew routes.
            var agency = BuildAgencyWithTwoDepots();
            var routeId = Guid.NewGuid().ToString("N");
            agency.WolfCrewRoutes[routeId] = new AgencyWolfCrewRouteEntry
            {
                UniqueId = routeId,
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                FlightStatus = "Boarding",
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "OwnPassenger" },
                },
            };

            var input = "name = WOLF_ScenarioModule\n" +
                        "CREWROUTES\n{\n" +
                        "\tROUTE\n\t{\n\t\tUniqueId = peer-crew-route\n\t\t" +
                        "OriginBody = Kerbin\n\t\tOriginBiome = KSC\n\t\t" +
                        "DestinationBody = Mun\n\t\tDestinationBiome = MidlandCraters\n\t\t" +
                        "FlightStatus = Enroute\n\t\tFlightNumber = PEER\n\t\t" +
                        "PASSENGERS\n\t\t{\n\t\t\tPASSENGER\n\t\t\t{\n\t\t\t\tName = PeerKerbal\n\t\t\t}\n\t\t}\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("peer-crew-route"),
                "Peer's CrewRoute UniqueId must be stripped before per-agency splice.");
            Assert.IsFalse(result.Contains("Name = PeerKerbal"),
                "Peer's PASSENGER must not leak through — operator's kerbals must never see other-agency passengers.");
            Assert.IsFalse(result.Contains("FlightNumber = PEER"),
                "Peer's FlightNumber must not leak.");
            StringAssert.Contains(result, $"UniqueId = {routeId}",
                "Own crew route still spliced in.");
            StringAssert.Contains(result, "Name = OwnPassenger");
        }

        [TestMethod]
        public void Project_EmptyCrewRoutes_NoContainerEmitted()
        {
            // Lazy-allocate semantics: no CrewRoutes → no container.
            var agency = BuildAgencyWithTwoDepots();
            // No WolfCrewRoutes entries.

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("CREWROUTES"),
                "Empty WolfCrewRoutes — no CREWROUTES container emitted.");
        }

        [TestMethod]
        public void Project_CrewRouteWithEmptyPassengersList_NoPassengersContainerEmitted()
        {
            // A freshly-created CrewRoute (CreateCrewRoute postfix fires
            // before any Embark) has an empty passenger list. The route
            // must still emit, but the nested PASSENGERS container must
            // NOT be emitted (lazy-allocate; matches WOLF source's
            // OnSave behaviour at CrewRoute.cs:268-275 which only adds
            // the PASSENGERS node when Passengers.Count > 0).
            var agency = BuildAgencyWithTwoDepots();
            var routeId = Guid.NewGuid().ToString("N");
            agency.WolfCrewRoutes[routeId] = new AgencyWolfCrewRouteEntry
            {
                UniqueId = routeId,
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                FlightStatus = "Boarding",
                Passengers = new List<AgencyWolfPassengerEntry>(),
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, $"UniqueId = {routeId}", "Empty-passenger route still emits.");
            Assert.IsFalse(result.Contains("PASSENGERS"),
                "Lazy-allocate: empty passenger list emits no PASSENGERS container.");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyState BuildAgencyWithTwoDepots()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Kerbin|KSC"] = new AgencyWolfDepotEntry
            {
                Body = "Kerbin", Biome = "KSC", IsEstablished = true,
            };
            agency.WolfDepots["Mun|MidlandCraters"] = new AgencyWolfDepotEntry
            {
                Body = "Mun", Biome = "MidlandCraters", IsEstablished = true,
            };
            return agency;
        }
    }
}
