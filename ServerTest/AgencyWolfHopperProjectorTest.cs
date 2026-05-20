using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;

namespace ServerTest
{
    /// <summary>
    /// [Phase 4 Slice D] Unit tests for the HOPPERS splice in
    /// <see cref="AgencyScenarioProjector"/>'s <c>SpliceAgencyWolfState</c>.
    /// Hoppers are FK-coupled to depots via WOLF's
    /// <c>ScenarioPersister.OnLoad</c> at <c>ScenarioPersister.cs:320-329</c>
    /// — a hopper whose Body+Biome isn't present in the depot pool is
    /// silently dropped by WOLF on load. The projector pre-empts that loss
    /// by FK-sweeping against the just-emitted per-agency depot pool.
    /// </summary>
    [TestClass]
    public class AgencyWolfHopperProjectorTest
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
        public void Project_AgencyWithDepotAndValidHopper_SplicesBoth()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry
            {
                Body = "Duna", Biome = "Lowlands", IsEstablished = true,
            };
            var hopperId = Guid.NewGuid().ToString();
            agency.WolfHoppers[hopperId] = new AgencyWolfHopperEntry
            {
                Id = hopperId, Body = "Duna", Biome = "Lowlands",
                Recipe = "Hydrates,100,Substrate,50",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, "HOPPERS", "HOPPERS container must appear.");
            StringAssert.Contains(result, $"Id = {hopperId}",
                "Hopper Id (with hyphens — ToString() form) must be emitted verbatim.");
            StringAssert.Contains(result, "Body = Duna");
            StringAssert.Contains(result, "Biome = Lowlands");
            StringAssert.Contains(result, "Recipe = Hydrates,100,Substrate,50",
                "Recipe must be emitted as the flat comma-joined WOLF wire format.");
        }

        [TestMethod]
        public void Project_HopperWithMissingDepot_DroppedByFKSweep()
        {
            // FK integrity gate: WOLF's ScenarioPersister.OnLoad at
            // ScenarioPersister.cs:320-329 looks up the hopper's depot by
            // Body+Biome and silently drops on miss. The projector pre-empts
            // by dropping the hopper from the outgoing blob too — keeps the
            // owner's local snapshot consistent with what the projection
            // could reload.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            // No Duna depot. Hopper references Duna.
            agency.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" };
            var hopperId = Guid.NewGuid().ToString();
            agency.WolfHoppers[hopperId] = new AgencyWolfHopperEntry
            {
                Id = hopperId, Body = "Duna", Biome = "Lowlands",
                Recipe = "Hydrates,100",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains($"Id = {hopperId}"),
                "Orphan hopper must be FK-swept out.");
            Assert.IsFalse(result.Contains("HOPPERS"),
                "Empty HOPPERS container must not be emitted (lazy-allocate).");
        }

        [TestMethod]
        public void Project_MixedValidAndOrphanHoppers_OnlyValidSurvive()
        {
            // Per-entry isolation: one orphan doesn't block siblings.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry { Body = "Duna", Biome = "Lowlands" };

            var validId = Guid.NewGuid().ToString();
            agency.WolfHoppers[validId] = new AgencyWolfHopperEntry
            {
                Id = validId, Body = "Duna", Biome = "Lowlands", Recipe = "Hydrates,100",
            };
            var orphanId = Guid.NewGuid().ToString();
            agency.WolfHoppers[orphanId] = new AgencyWolfHopperEntry
            {
                Id = orphanId, Body = "Mun", Biome = "Highlands", Recipe = "Substrate,999",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, $"Id = {validId}",
                "Valid sibling must survive.");
            Assert.IsFalse(result.Contains($"Id = {orphanId}"),
                "Orphan sibling must be FK-swept out.");
            Assert.IsFalse(result.Contains("Recipe = Substrate,999"),
                "Orphan recipe must not appear.");
        }

        [TestMethod]
        public void Project_ReusesDepotKeySetFromRoutes_NoDoubleBuild()
        {
            // The depotKeySet HashSet is hoisted to projector method scope
            // (Slice C) for reuse by Slice D's hopper FK sweep + Slice E's
            // crew-route FK sweep. Co-emit verifies both routes AND hoppers
            // can share the build without double-allocation issues.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry { Body = "Duna", Biome = "Lowlands" };
            agency.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" };
            agency.WolfRoutes["Duna|Lowlands|Mun|Highlands"] = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna", OriginBiome = "Lowlands",
                DestinationBody = "Mun", DestinationBiome = "Highlands",
                Payload = 1500,
            };
            var hopperId = Guid.NewGuid().ToString();
            agency.WolfHoppers[hopperId] = new AgencyWolfHopperEntry
            {
                Id = hopperId, Body = "Duna", Biome = "Lowlands", Recipe = "Hydrates,100",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, "ROUTES", "ROUTES container appears (FK-swept against same depot pool).");
            StringAssert.Contains(result, "HOPPERS", "HOPPERS container appears (FK-swept against same depot pool).");
            StringAssert.Contains(result, "Payload = 1500");
            StringAssert.Contains(result, $"Id = {hopperId}");
        }

        [TestMethod]
        public void Project_LazyBuildOfDepotKeySet_WhenRoutesEmptyButHoppersPresent()
        {
            // Routes block doesn't run (empty WolfRoutes) so depotKeySet
            // would be null. The Hoppers block must lazily build it itself.
            // Test exercises the "null-check + populate" path the routes
            // block normally triggers.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry { Body = "Duna", Biome = "Lowlands" };
            var hopperId = Guid.NewGuid().ToString();
            agency.WolfHoppers[hopperId] = new AgencyWolfHopperEntry
            {
                Id = hopperId, Body = "Duna", Biome = "Lowlands", Recipe = "Hydrates,100",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            // No routes emit (empty dict), but hopper still emits — proves
            // the lazy depotKeySet build inside the hoppers block runs even
            // when the routes block doesn't.
            Assert.IsFalse(result.Contains("ROUTES"), "Empty WolfRoutes — no ROUTES emit.");
            StringAssert.Contains(result, "HOPPERS", "Hopper FK passes even without routes path having run first.");
            StringAssert.Contains(result, $"Id = {hopperId}");
        }

        [TestMethod]
        public void Project_StripsSharedHOPPERS_BeforeSplicing()
        {
            // Pre-existing shared-input HOPPERS from peer agencies must be
            // stripped first — peer's hopper entries must never leak through
            // even when this agency has its own valid hoppers.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry { Body = "Duna", Biome = "Lowlands" };
            var hopperId = Guid.NewGuid().ToString();
            agency.WolfHoppers[hopperId] = new AgencyWolfHopperEntry
            {
                Id = hopperId, Body = "Duna", Biome = "Lowlands", Recipe = "Hydrates,100",
            };

            var input = "name = WOLF_ScenarioModule\n" +
                        "HOPPERS\n{\n" +
                        "\tHOPPER\n\t{\n\t\tId = peer-hopper-id\n\t\tRecipe = PeerOnly,9999\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("peer-hopper-id"),
                "Peer's HOPPER must be stripped before per-agency splice.");
            Assert.IsFalse(result.Contains("Recipe = PeerOnly,9999"),
                "Peer's recipe data must not leak through.");
            StringAssert.Contains(result, $"Id = {hopperId}",
                "Own hopper still spliced in.");
        }
    }
}
