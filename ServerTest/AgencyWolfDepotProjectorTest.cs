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
    /// [Phase 4 Slice B-2] Unit tests for <see cref="AgencyScenarioProjector"/>'s
    /// new <c>WOLF_ScenarioModule</c> case + <c>SpliceAgencyWolfState</c>.
    /// Mirrors the <c>AgencyKolonyProjectorTest</c> shape: strip-then-splice
    /// contract, per-agency view, defensive parse-failure fallback.
    ///
    /// <para>Slice B-2 emits only DEPOTS; Slices C-E will extend
    /// SpliceAgencyWolfState to also handle ROUTES / HOPPERS / TERMINALS /
    /// CREWROUTES. All 5 child families are STRIPPED from the shared input
    /// in Slice B-2 (clean slate); only DEPOTS is re-emitted.</para>
    /// </summary>
    [TestClass]
    public class AgencyWolfDepotProjectorTest
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
        public void Project_EmptyAgencyWolf_StripsAllFiveSharedChildFamilies()
        {
            // Strip-then-splice contract. An agency with zero per-agency depots
            // projects an empty WOLF_ScenarioModule (no DEPOTS emit) regardless
            // of what the shared scenario carried — peers' WOLF state must not
            // leak. Slices C-E will extend the strip+emit shape to also handle
            // ROUTES / HOPPERS / TERMINALS / CREWROUTES; until then all 5 are
            // stripped on input (clean slate) and only DEPOTS is potentially
            // re-emitted.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = WOLF_ScenarioModule\n" +
                        "DEPOTS\n{\n" +
                        "\tDEPOT\n\t{\n\t\tBody = SharedBody\n\t\tBiome = SharedBiome\n\t}\n" +
                        "}\n" +
                        "ROUTES\n{\n" +
                        "\tROUTE\n\t{\n\t\tOriginBody = SharedOrigin\n\t}\n" +
                        "}\n" +
                        "HOPPERS\n{\n" +
                        "\tHOPPER\n\t{\n\t\tId = abc-shared\n\t}\n" +
                        "}\n" +
                        "TERMINALS\n{\n" +
                        "\tTERMINAL\n\t{\n\t\tId = sharedterminal\n\t}\n" +
                        "}\n" +
                        "CREWROUTES\n{\n" +
                        "\tROUTE\n\t{\n\t\tUniqueId = sharedcrewroute\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            // All 5 shared child families stripped:
            Assert.IsFalse(result.Contains("SharedBody"), "Shared DEPOT must be stripped.");
            Assert.IsFalse(result.Contains("SharedOrigin"), "Shared ROUTE must be stripped.");
            Assert.IsFalse(result.Contains("abc-shared"), "Shared HOPPER must be stripped.");
            Assert.IsFalse(result.Contains("sharedterminal"), "Shared TERMINAL must be stripped.");
            Assert.IsFalse(result.Contains("sharedcrewroute"), "Shared CREWROUTE must be stripped.");
        }

        [TestMethod]
        public void Project_AgencyWithDepots_SplicesOnlyOwn()
        {
            // Per-agency view: the splice emits only the requesting agency's
            // depots. Shared scenario has 1 peer depot; agency has 2 own
            // depots; output has exactly 2 depots (both agency-owned).
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry
            {
                Body = "Duna",
                Biome = "Lowlands",
                IsEstablished = true,
                IsSurveyed = true,
            };
            agency.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry
            {
                Body = "Mun",
                Biome = "Highlands",
                IsEstablished = false,
                IsSurveyed = true,
            };

            var input = "name = WOLF_ScenarioModule\n" +
                        "DEPOTS\n{\n" +
                        "\tDEPOT\n\t{\n\t\tBody = PeerBody\n\t\tBiome = PeerBiome\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            // Peer's entry stripped:
            Assert.IsFalse(result.Contains("PeerBody"),
                "Peer's DEPOT must NOT appear in this agency's projection.");
            Assert.IsFalse(result.Contains("PeerBiome"),
                "Peer's biome must NOT appear in this agency's projection.");

            // Own entries spliced:
            StringAssert.Contains(result, "Duna");
            StringAssert.Contains(result, "Lowlands");
            StringAssert.Contains(result, "Mun");
            StringAssert.Contains(result, "Highlands");
        }

        [TestMethod]
        public void Project_DepotWithResourceStreams_EmitsNestedRESOURCEChildren()
        {
            // Verify the nested wire shape: per WOLF Depot.cs:257-262,
            // ResourceStreams persist as RESOURCE child nodes (NOT WOLF_RESOURCE_STREAM
            // — that's our disk-side name). The projector emits using WOLF's
            // wire-side name so Depot.OnLoad's parse contract is satisfied.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry
            {
                Body = "Duna",
                Biome = "Lowlands",
                ResourceStreams = new List<AgencyWolfResourceStreamEntry>
                {
                    new AgencyWolfResourceStreamEntry { ResourceName = "Hydrates", Incoming = 100, Outgoing = 50 },
                },
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, "RESOURCE",
                "Nested RESOURCE child must appear (matches WOLF wire format, NOT WOLF_RESOURCE_STREAM disk format).");
            StringAssert.Contains(result, "Hydrates");
            StringAssert.Contains(result, "Incoming = 100");
            StringAssert.Contains(result, "Outgoing = 50");
        }

        [TestMethod]
        public void Project_NullAgency_ReturnsInputUnchanged()
        {
            var input = "name = WOLF_ScenarioModule\nDEPOTS { }\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, null);
            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void Project_NullInput_ReturnsNull()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", null, agency);
            Assert.IsNull(result);
        }

        [TestMethod]
        public void Project_EmptyInput_PassthroughEmpty()
        {
            // Empty-string input falls through the projector unchanged. Same
            // posture as Slice B Kolony test
            // (Project_NullInput_ReturnsNull's sibling defensive case).
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", "", agency);
            Assert.AreEqual("", result);
        }

        // Gate-off behaviour is enforced in AgencyScenarioProjector.ProjectForClient
        // (the outer entry-point), not in the inner Project method this test class
        // exercises. Direct Project() calls bypass the gate check intentionally —
        // mirrors the Phase 3 AgencyKolonyProjectorTest scope. Gate-off e2e
        // coverage lives in MockClientTest/WolfDepotRoutingTest.cs.
    }
}
