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
    /// [Phase 4 Slice C] Unit tests for <see cref="AgencyScenarioProjector"/>'s
    /// ROUTES splice extension to <c>SpliceAgencyWolfState</c>. Mirrors the
    /// <see cref="AgencyWolfDepotProjectorTest"/> shape but focuses on the
    /// new FK integrity sweep (drop routes whose origin or destination
    /// depot is missing from the agency's depot pool — otherwise WOLF's
    /// <c>Route.OnLoad</c> throws <c>DepotDoesNotExistException</c>).
    /// </summary>
    [TestClass]
    public class AgencyWolfRouteProjectorTest
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
        public void Project_AgencyWithDepotsAndValidRoute_SplicesBoth()
        {
            // Happy path: route's origin + destination depots are present in
            // the agency's pool — FK sweep accepts, ROUTE node appears.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry
            {
                Body = "Duna",
                Biome = "Lowlands",
                IsEstablished = true,
            };
            agency.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry
            {
                Body = "Mun",
                Biome = "Highlands",
                IsEstablished = true,
            };
            agency.WolfRoutes["Duna|Lowlands|Mun|Highlands"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 1500,
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, "ROUTES", "ROUTES container must appear.");
            StringAssert.Contains(result, "OriginBody = Duna",
                "Origin/destination fields must be emitted with WOLF's wire names.");
            StringAssert.Contains(result, "DestinationBody = Mun");
            StringAssert.Contains(result, "Payload = 1500");
        }

        [TestMethod]
        public void Project_RouteWithMissingOriginDepot_DroppedByFKSweep()
        {
            // FK integrity gate: the route's origin depot is not in the
            // agency's pool. WOLF's Route.OnLoad at Route.cs:172 would
            // throw DepotDoesNotExistException — the projector pre-empts
            // by dropping the route from the outgoing blob.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry
            {
                Body = "Mun",
                Biome = "Highlands",
                IsEstablished = true,
            };
            // Note: NO Duna depot. Route references Duna as origin.
            agency.WolfRoutes["Duna|Lowlands|Mun|Highlands"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 1500,
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("Payload = 1500"),
                "Orphan route must be FK-swept out — payload-1500 marker must NOT appear.");
            Assert.IsFalse(result.Contains("OriginBody = Duna"),
                "Orphan route must be FK-swept out — Duna origin field must NOT appear.");
        }

        [TestMethod]
        public void Project_RouteWithMissingDestinationDepot_DroppedByFKSweep()
        {
            // Symmetric: destination missing rather than origin.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry
            {
                Body = "Duna",
                Biome = "Lowlands",
                IsEstablished = true,
            };
            // Note: NO Mun depot. Route references Mun as destination.
            agency.WolfRoutes["Duna|Lowlands|Mun|Highlands"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 1500,
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("Payload = 1500"),
                "Orphan route (missing destination depot) must be FK-swept out.");
        }

        [TestMethod]
        public void Project_MixedValidAndOrphanRoutes_OnlyValidSurvive()
        {
            // Per-entry FK isolation: one orphan does not block siblings.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry { Body = "Duna", Biome = "Lowlands" };
            agency.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" };
            // Valid: both depots present.
            agency.WolfRoutes["Duna|Lowlands|Mun|Highlands"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 1500,
            };
            // Orphan: destination biome doesn't match any depot.
            agency.WolfRoutes["Duna|Lowlands|Kerbin|Shores"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Kerbin",
                DestinationBiome = "Shores",
                Payload = 999,
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, "Payload = 1500", "Valid sibling must survive.");
            Assert.IsFalse(result.Contains("Payload = 999"), "Orphan sibling must be FK-swept.");
        }

        [TestMethod]
        public void Project_RouteWithResources_EmitsNestedRESOURCEChildren()
        {
            // Verify the nested wire shape: per WOLF Route.cs:188-205, route
            // resources persist as RESOURCE child nodes. The projector emits
            // using WOLF's wire-side name so Route.OnLoad's parse contract is
            // satisfied.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry { Body = "Duna", Biome = "Lowlands" };
            agency.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" };
            agency.WolfRoutes["Duna|Lowlands|Mun|Highlands"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 1500,
                Resources = new List<AgencyWolfRouteResourceEntry>
                {
                    new AgencyWolfRouteResourceEntry { ResourceName = "Fuel", Quantity = 750 },
                },
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, "RESOURCE",
                "Nested RESOURCE child must appear (matches WOLF wire format).");
            StringAssert.Contains(result, "ResourceName = Fuel");
            StringAssert.Contains(result, "Quantity = 750");
        }

        [TestMethod]
        public void Project_AgencyWithEmptyRoutes_NoROUTESContainerEmitted()
        {
            // Lazy-allocate the ROUTES container: an empty-routes agency
            // should NOT emit an empty <ROUTES /> wrapper. Cleaner than
            // an always-present empty container, and matches the lazy
            // pattern used for the depot emit.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" };
            // No routes added.

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("ROUTES"),
                "Empty WolfRoutes dict must not emit a ROUTES container.");
            // Depots still emit normally.
            StringAssert.Contains(result, "DEPOTS");
            StringAssert.Contains(result, "Body = Mun");
        }

        [TestMethod]
        public void Project_StripsSharedROUTES_BeforeSplicing()
        {
            // Pre-existing shared-input ROUTES from peer agencies must be
            // stripped first — peer's route entries must never leak through
            // even when this agency has its own valid routes.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry { Body = "Duna", Biome = "Lowlands" };
            agency.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" };
            agency.WolfRoutes["Duna|Lowlands|Mun|Highlands"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 1500,
            };

            var input = "name = WOLF_ScenarioModule\n" +
                        "ROUTES\n{\n" +
                        "\tROUTE\n\t{\n\t\tOriginBody = PeerOrigin\n\t\tPayload = 8888\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("PeerOrigin"),
                "Peer's ROUTE must be stripped before per-agency splice.");
            Assert.IsFalse(result.Contains("Payload = 8888"),
                "Peer's Payload must be stripped.");
            StringAssert.Contains(result, "Payload = 1500",
                "Own route still spliced in.");
        }
    }
}
