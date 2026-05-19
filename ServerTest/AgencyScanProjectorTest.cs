using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Collections.Generic;

namespace ServerTest
{
    /// <summary>
    /// [Mod-compat S2 — SCANsat] Unit tests for
    /// <see cref="AgencyScenarioProjector"/>'s new SCANcontroller case +
    /// <c>SpliceSCANsatCoverageIntoScenario</c>. Same shape as
    /// <see cref="AgencyKolonyProjectorTest"/>. Strips Progress / Scanners
    /// shared children; splices per-agency entries; leaves SCANResources +
    /// root UI scalars untouched (Decisions §6 + §7).
    /// </summary>
    [TestClass]
    public class AgencyScanProjectorTest
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
        public void Project_EmptyAgency_StripsSharedAndEmitsEmptyContainers()
        {
            // M9 — empty-container retention. After stripping shared Body /
            // Vessel children, emit empty Progress { } + Scanners { } so
            // SCANsat's OnLoad does not skip the load branch.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = SCANcontroller\n" +
                        "mainMapVisible = True\n" +
                        "Progress\n{\n" +
                        "\tBody\n\t{\n\t\tName = Eve\n\t\tMap = peer_blob\n\t}\n" +
                        "}\n" +
                        "Scanners\n{\n" +
                        "\tVessel\n\t{\n\t\tguid = ffffffff-ffff-ffff-ffff-ffffffffffff\n\t\tname = peer\n\t}\n" +
                        "}\n" +
                        "SCANResources\n{\n" +
                        "\tResourceType\n\t{\n\t\tResource = Ore\n\t\tMinColor = 0,0,0,255\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("SCANcontroller", input, agency);

            // Shared content stripped:
            Assert.IsFalse(result.Contains("peer_blob"),
                "Shared Body's Map blob must be stripped");
            Assert.IsFalse(result.Contains("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                "Shared Vessel's guid must be stripped");

            // Containers themselves survive (M9):
            StringAssert.Contains(result, "Progress",
                "Progress container must survive even when emptied");
            StringAssert.Contains(result, "Scanners",
                "Scanners container must survive even when emptied");

            // SCANResources + root UI scalars untouched (Decisions §6 + §7):
            StringAssert.Contains(result, "SCANResources",
                "SCANResources container must NOT be touched (Decision §6 — shared)");
            StringAssert.Contains(result, "Resource = Ore",
                "SCANResources content must pass through unchanged");
            StringAssert.Contains(result, "mainMapVisible",
                "Root UI scalar must NOT be touched (Decision §7 — shared)");
        }

        [TestMethod]
        public void Project_AgencyWithCoverage_SplicesOwnBodiesStripsPeers()
        {
            // Strip-then-splice. Inbound has 1 peer Body; agency has 2 own
            // Body entries. Output has exactly 2 Body entries (both
            // agency-owned).
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.Coverage["Eve"] = new AgencyCoverageBodyEntry
            {
                BodyName = "Eve", Map = "AGENCY_EVE", PaletteName = "Default", PaletteSize = 7,
                MinHeightRange = 0f, MaxHeightRange = 1000f,
            };
            agency.Coverage["Duna"] = new AgencyCoverageBodyEntry
            {
                BodyName = "Duna", Map = "AGENCY_DUNA", PaletteName = "Default", PaletteSize = 7,
                MinHeightRange = 0f, MaxHeightRange = 5000f,
            };

            var input = "name = SCANcontroller\n" +
                        "Progress\n{\n" +
                        "\tBody\n\t{\n\t\tName = Kerbin\n\t\tMap = PEER_KERBIN\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("SCANcontroller", input, agency);

            // Peer stripped:
            Assert.IsFalse(result.Contains("PEER_KERBIN"),
                "Peer's Map blob must NOT appear in agency projection");

            // Own spliced:
            StringAssert.Contains(result, "AGENCY_EVE", "Agency's Eve Map must appear");
            StringAssert.Contains(result, "AGENCY_DUNA", "Agency's Duna Map must appear");
            StringAssert.Contains(result, "Name = Eve");
            StringAssert.Contains(result, "Name = Duna");
        }

        [TestMethod]
        public void Project_AgencyWithMultiSensorVessel_RoundTripsAllSensors()
        {
            // Decision §9 — nested Sensor list must round-trip through
            // projection. Agency has one Vessel with 3 Sensor records; the
            // projected blob's Vessel child contains all 3 Sensor children.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var vesselId = Guid.NewGuid();
            agency.Scanners[vesselId] = new AgencyScannerEntry
            {
                VesselId = vesselId,
                VesselName = "Multi",
                Sensors = new List<AgencyScannerSensorRecord>
                {
                    new AgencyScannerSensorRecord { SensorType = 16, Fov = 3, MinAlt = 100, MaxAlt = 1000, BestAlt = 500 },
                    new AgencyScannerSensorRecord { SensorType = 8,  Fov = 1, MinAlt = 0,   MaxAlt = 100,  BestAlt = 50 },
                    new AgencyScannerSensorRecord { SensorType = 32, Fov = 5, MinAlt = 200, MaxAlt = 2000, BestAlt = 1000 },
                },
            };

            var input = "name = SCANcontroller\n";
            var result = AgencyScenarioProjector.Project("SCANcontroller", input, agency);

            // Spot-check that all three sensor types round-trip through the
            // projection with SCANsat's lowercase-underscore field naming. The
            // specific newline/brace formatting of ConfigNode.ToString() is
            // implementation-detail and not load-bearing for the assertion —
            // SCANsat's OnLoad cares about field names + values, not whitespace.
            StringAssert.Contains(result, "type = 16",
                "Sensor record with type=16 must appear in projection");
            StringAssert.Contains(result, "type = 8",
                "Sensor record with type=8 must appear in projection");
            StringAssert.Contains(result, "type = 32",
                "Sensor record with type=32 must appear in projection");
            StringAssert.Contains(result, "min_alt", "Sensor field uses lowercase-underscore");
            StringAssert.Contains(result, "best_alt");
        }

        [TestMethod]
        public void Project_SCANResources_UntouchedByProjection()
        {
            // Decision §6 — SCANResources stays shared. Agency has no
            // ResourceType entries (it's not even modeled on AgencyState); the
            // projector must pass SCANResources through verbatim from input.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = SCANcontroller\n" +
                        "SCANResources\n{\n" +
                        "\tResourceType\n\t{\n" +
                        "\t\tResource = Karbonite\n" +
                        "\t\tMinMaxValues = 1|0.5|95.0,2|10.0|89.0\n" +
                        "\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("SCANcontroller", input, agency);

            StringAssert.Contains(result, "Resource = Karbonite",
                "SCANResources content must survive projection");
            StringAssert.Contains(result, "1|0.5|95.0",
                "MinMaxValues must survive projection unchanged");
        }

        [TestMethod]
        public void Project_RootUiScalars_UntouchedByProjection()
        {
            // Decision §7 — root-level KSPField UI scalars stay shared. The
            // splice must not touch any root-level key=value pair.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = SCANcontroller\n" +
                        "mainMapVisible = True\n" +
                        "bigMapColor = False\n" +
                        "zoomMapType = 0\n";

            var result = AgencyScenarioProjector.Project("SCANcontroller", input, agency);

            StringAssert.Contains(result, "mainMapVisible = True");
            StringAssert.Contains(result, "bigMapColor = False");
            StringAssert.Contains(result, "zoomMapType = 0");
        }

        [TestMethod]
        public void Project_NullAgency_ReturnsInputUnchanged()
        {
            // Defensive: Project's outer guard handles null targetAgency.
            var input = "name = SCANcontroller\n";
            var result = AgencyScenarioProjector.Project("SCANcontroller", input, null);
            Assert.AreEqual(input, result);
        }

    }
}
