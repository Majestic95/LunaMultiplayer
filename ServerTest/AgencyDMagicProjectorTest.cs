using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;

namespace ServerTest
{
    /// <summary>
    /// [Mod-compat S4 — DMagic Orbital Science] Unit tests for
    /// <see cref="AgencyScenarioProjector"/>'s new DMScienceScenario case +
    /// <c>SpliceDMagicScienceIntoScenario</c>. Strip-then-splice on both
    /// container collections; nested DM_Anomaly_List emit grouped by
    /// BodyIndex per Decision §B; empty agency → empty containers (M9).
    /// </summary>
    [TestClass]
    public class AgencyDMagicProjectorTest
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
            // M9 — empty-container retention. After stripping shared children
            // emit empty Asteroid_Science / Anomaly_Records containers (DMagic
            // OnLoad guards on container presence, not child count).
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = DMScienceScenario\n" +
                        "Asteroid_Science\n{\n" +
                        "\tDM_Science\n\t{\n\t\ttitle = PeerAsteroid\n\t\tsci = 99\n\t}\n" +
                        "}\n" +
                        "Anomaly_Records\n{\n" +
                        "\tDM_Anomaly_List\n\t{\n\t\tBody = 5\n" +
                        "\t\tDM_Anomaly\n\t\t{\n\t\t\tName = PeerMonolith\n\t\t}\n" +
                        "\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("DMScienceScenario", input, agency);

            // Shared content stripped:
            Assert.IsFalse(result.Contains("PeerAsteroid"),
                "Shared DM_Science must be stripped");
            Assert.IsFalse(result.Contains("PeerMonolith"),
                "Shared DM_Anomaly must be stripped");

            // Containers survive (M9):
            StringAssert.Contains(result, "Asteroid_Science",
                "Asteroid_Science container must survive when empty");
            StringAssert.Contains(result, "Anomaly_Records",
                "Anomaly_Records container must survive when empty");
        }

        [TestMethod]
        public void Project_AgencyWithAsteroids_SplicesOwnStripsPeers()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.DMagicAsteroidScience["AsteroidEve"] = new AgencyDMagicAsteroidEntry
            {
                Title = "AsteroidEve", BaseValue = 1f, SciVal = 0.5f, Science = 11f, Cap = 20f,
            };
            agency.DMagicAsteroidScience["AsteroidDuna"] = new AgencyDMagicAsteroidEntry
            {
                Title = "AsteroidDuna", BaseValue = 2f, SciVal = 0.25f, Science = 7.5f, Cap = 30f,
            };

            var input = "name = DMScienceScenario\n" +
                        "Asteroid_Science\n{\n" +
                        "\tDM_Science\n\t{\n\t\ttitle = PeerAsteroid\n\t\tsci = 999\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("DMScienceScenario", input, agency);

            Assert.IsFalse(result.Contains("PeerAsteroid"),
                "Peer DM_Science must NOT appear in agency projection");
            StringAssert.Contains(result, "title = AsteroidEve");
            StringAssert.Contains(result, "title = AsteroidDuna");
            // Field names lowercase per DMagic wire contract.
            StringAssert.Contains(result, "sci = 11",
                "Asteroid science accumulator must round-trip through projection");
        }

        [TestMethod]
        public void Project_AnomaliesOnMultipleBodies_GroupsIntoPerBodyWrappers()
        {
            // Decision §B core property — the projector groups flat agency
            // entries into per-body DM_Anomaly_List wrappers on emit. Two
            // bodies × N anomalies per body → two DM_Anomaly_List wrappers
            // with correct Body values + correct child anomalies.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.DMagicAnomalies["5|EveMonolith"] = new AgencyDMagicAnomalyEntry
            {
                BodyIndex = 5, Name = "EveMonolith", Latitude = 10, Longitude = 20, Altitude = 30,
            };
            agency.DMagicAnomalies["5|EvePyramid"] = new AgencyDMagicAnomalyEntry
            {
                BodyIndex = 5, Name = "EvePyramid", Latitude = 40, Longitude = 50, Altitude = 60,
            };
            agency.DMagicAnomalies["6|DunaFace"] = new AgencyDMagicAnomalyEntry
            {
                BodyIndex = 6, Name = "DunaFace", Latitude = 70, Longitude = 80, Altitude = 90,
            };

            var input = "name = DMScienceScenario\n";
            var result = AgencyScenarioProjector.Project("DMScienceScenario", input, agency);

            // Must contain two distinct Body wrappers — count occurrences of "Body =".
            StringAssert.Contains(result, "Body = 5",
                "Wrapper for body 5 must appear");
            StringAssert.Contains(result, "Body = 6",
                "Wrapper for body 6 must appear");
            // All three anomalies present
            StringAssert.Contains(result, "Name = EveMonolith");
            StringAssert.Contains(result, "Name = EvePyramid");
            StringAssert.Contains(result, "Name = DunaFace");
            // Lat values should round-trip
            StringAssert.Contains(result, "Lat = 10");
            StringAssert.Contains(result, "Lat = 70");
        }

        [TestMethod]
        public void Project_MalformedScenarioInput_DoesNotThrow()
        {
            // ConfigNode is permissive about malformed input; the splice
            // shouldn't crash regardless. (S2's analogous parse-failure test
            // was dropped because ConfigNode accepts most inputs; just verify
            // no exception escapes.)
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "garbled text without proper structure { broken";
            var result = AgencyScenarioProjector.Project("DMScienceScenario", input, agency);
            Assert.IsNotNull(result, "Projection must not throw on malformed input");
        }

        [TestMethod]
        public void Project_NullAgency_ReturnsInputUnchanged()
        {
            var input = "name = DMScienceScenario\n";
            var result = AgencyScenarioProjector.Project("DMScienceScenario", input, null);
            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void Project_GateOff_ProjectForClientReturnsInputUnchanged()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            // ProjectForClient checks gate; Project (no client overload) is
            // the gate-on path. Verify the ProjectForClient null-client path
            // bypasses too.
            var input = "name = DMScienceScenario\nfoo = bar\n";
            var result = AgencyScenarioProjector.ProjectForClient("DMScienceScenario", input, null);
            Assert.AreEqual(input, result);
        }
    }
}
