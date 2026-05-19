using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Globalization;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// Phase 3 Slice B — unit tests for <see cref="AgencyScenarioProjector"/>'s
    /// new <c>KolonizationScenario</c> case + <c>SpliceAgencyKolonyEntries</c>.
    /// Same shape as the existing <c>AgencyScenarioProjectorTest</c> Funding /
    /// ResearchAndDevelopment / Reputation pins (Stage 5.17c). End-to-end wire
    /// coverage (gate-on cross-agency privacy across two clients) is in
    /// <c>MockClientTest/AgencyKolonyRoutingTest.cs</c>.
    /// </summary>
    [TestClass]
    public class AgencyKolonyProjectorTest
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
        public void Project_EmptyAgencyKolony_StripsAllSharedEntries()
        {
            // Strip-then-splice contract (matches the Strategy/Achievement
            // pattern at AgencyScenarioProjector.cs:245-262). An agency with
            // zero per-agency entries projects an empty KOLONIZATION container,
            // even if the shared scenario carried entries from peers — those
            // peers' research must not leak into this agency's view.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = KolonizationScenario\nscene = 7, 8\n" +
                        "KOLONIZATION\n{\n" +
                        "\tKOLONY_ENTRY\n\t{\n\t\tBodyIndex = 5\n\t\tVesselId = aabbccdd\n\t\tGeologyResearch = 100\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("KolonizationScenario", input, agency);

            Assert.IsFalse(result.Contains("VesselId = aabbccdd"),
                "Shared KOLONY_ENTRY must be stripped from the projected scenario.");
            Assert.IsFalse(result.Contains("GeologyResearch = 100"),
                "Stripped entry's values must not appear in the projected output.");
            StringAssert.Contains(result, "KOLONIZATION",
                "KOLONIZATION container itself must survive even when emptied.");
        }

        [TestMethod]
        public void Project_AgencyWithEntries_SplicesOnlyOwn()
        {
            // Per-agency view: the splice emits only the requesting agency's
            // entries. Shared scenario has 1 peer entry; agency has 2 own
            // entries; output has exactly 2 entries (both agency-owned).
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var vesselA = Guid.NewGuid().ToString("N");
            var vesselB = Guid.NewGuid().ToString("N");
            agency.KolonyEntries[$"{vesselA}|5"] = new AgencyKolonyEntry
            {
                VesselId = vesselA,
                BodyIndex = 5,
                GeologyResearch = 11.0,
            };
            agency.KolonyEntries[$"{vesselB}|7"] = new AgencyKolonyEntry
            {
                VesselId = vesselB,
                BodyIndex = 7,
                BotanyResearch = 22.0,
            };

            var input = "name = KolonizationScenario\n" +
                        "KOLONIZATION\n{\n" +
                        "\tKOLONY_ENTRY\n\t{\n\t\tBodyIndex = 9\n\t\tVesselId = ffffffffffffffffffffffffffffffff\n\t\tGeologyResearch = 999\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("KolonizationScenario", input, agency);

            // Peer's entry stripped:
            Assert.IsFalse(result.Contains("GeologyResearch = 999"),
                "Peer's KOLONY_ENTRY must NOT appear in this agency's projection.");
            Assert.IsFalse(result.Contains("VesselId = ffffffffffffffffffffffffffffffff"),
                "Peer's VesselId must NOT appear in this agency's projection.");

            // Own entries spliced:
            StringAssert.Contains(result, vesselA, "Agency's first vessel id must appear in projection.");
            StringAssert.Contains(result, vesselB, "Agency's second vessel id must appear in projection.");
            StringAssert.Contains(result, "GeologyResearch = 11",
                "Agency's first GeologyResearch value must appear in projection.");
            StringAssert.Contains(result, "BotanyResearch = 22",
                "Agency's second BotanyResearch value must appear in projection.");
        }

        [TestMethod]
        public void Project_NullAgency_ReturnsInputUnchanged()
        {
            var input = "name = KolonizationScenario\nKOLONIZATION { }\n";
            var result = AgencyScenarioProjector.Project("KolonizationScenario", input, null);
            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void Project_NullInput_ReturnsNull()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var result = AgencyScenarioProjector.Project("KolonizationScenario", null, agency);
            Assert.IsNull(result);
        }

        [TestMethod]
        public void Project_MissingKolonizationContainer_AddsContainer_NoSharedLeak()
        {
            // A scenario blob that lacks the KOLONIZATION child entirely (fresh
            // KolonizationScenario before any save) should not throw — the
            // splice creates the container, then the empty agency leaves it
            // empty. Defensive against KSP-side first-load shape.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = KolonizationScenario\nscene = 7, 8\n";

            var result = AgencyScenarioProjector.Project("KolonizationScenario", input, agency);

            // The projector adds the container even when the agency has zero
            // entries (the strip-then-splice path needs a container to splice
            // into). Output therefore contains "KOLONIZATION" but no
            // KOLONY_ENTRY children.
            StringAssert.Contains(result, "KOLONIZATION");
            Assert.IsFalse(result.Contains("KOLONY_ENTRY"),
                "Empty agency must not introduce KOLONY_ENTRY children.");
        }

        [TestMethod]
        public void Project_FieldNameMapping_EmitsRepNotReputation()
        {
            // MKS' on-disk KOLONY_ENTRY uses field name "Rep" (matching
            // KolonizationEntry.Rep); LMP's per-agency AgencyKolonyEntry uses
            // "Reputation" (matching LMP naming). The splice must emit the
            // MKS-side name so KSP-side ResourceUtilities.LoadNodeProperties
            // reads the value into the right field.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var vesselId = Guid.NewGuid().ToString("N");
            agency.KolonyEntries[$"{vesselId}|1"] = new AgencyKolonyEntry
            {
                VesselId = vesselId,
                BodyIndex = 1,
                Reputation = 42.0,
            };
            var input = "name = KolonizationScenario\nKOLONIZATION { }\n";

            var result = AgencyScenarioProjector.Project("KolonizationScenario", input, agency);

            StringAssert.Contains(result, "Rep = 42",
                "Projector must emit MKS-side field name 'Rep' (not 'Reputation').");
            Assert.IsFalse(result.Contains("Reputation = 42"),
                "Projector must NOT emit the LMP-side field name in the projected scenario.");
        }

        [TestMethod]
        public void Project_DoubleValues_UsesInvariantCulture()
        {
            // Locale safety: a server with a comma-decimal thread culture must
            // emit dot-decimal values so KSP-side parsers (which expect dot
            // decimal) read correctly. Same shape as the Funding scalar
            // round-trip in Stage 5.17c's projector.
            var prevCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var agency = new AgencyState { AgencyId = Guid.NewGuid() };
                var vesselId = Guid.NewGuid().ToString("N");
                agency.KolonyEntries[$"{vesselId}|1"] = new AgencyKolonyEntry
                {
                    VesselId = vesselId,
                    BodyIndex = 1,
                    GeologyResearch = 1234.56,
                };
                var input = "name = KolonizationScenario\nKOLONIZATION { }\n";

                var result = AgencyScenarioProjector.Project("KolonizationScenario", input, agency);

                StringAssert.Contains(result, "GeologyResearch = 1234.56",
                    "Projector must emit invariant-culture dot-decimal under de-DE thread culture.");
                Assert.IsFalse(result.Contains("1234,56"),
                    "Projector must NOT emit comma-decimal under de-DE culture.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prevCulture;
            }
        }

        [TestMethod]
        public void Project_NoCrossAgencyEntryLeak_ProjectionIsAgencyScoped()
        {
            // Spec §10 Q1 PrivateAgencyResources. Two agencies each get their
            // own projection — the splice reads from the *target* agency's
            // KolonyEntries only, never the other's. Direct call to Project
            // proves the per-target-agency partitioning is in the helper itself
            // (not just in the routing layer above).
            var agencyA = new AgencyState { AgencyId = Guid.NewGuid() };
            var agencyB = new AgencyState { AgencyId = Guid.NewGuid() };
            var vesselA = Guid.NewGuid().ToString("N");
            var vesselB = Guid.NewGuid().ToString("N");
            agencyA.KolonyEntries[$"{vesselA}|1"] = new AgencyKolonyEntry
            {
                VesselId = vesselA, BodyIndex = 1, GeologyResearch = 100.0,
            };
            agencyB.KolonyEntries[$"{vesselB}|1"] = new AgencyKolonyEntry
            {
                VesselId = vesselB, BodyIndex = 1, GeologyResearch = 200.0,
            };
            var input = "name = KolonizationScenario\nKOLONIZATION { }\n";

            var resultA = AgencyScenarioProjector.Project("KolonizationScenario", input, agencyA);
            var resultB = AgencyScenarioProjector.Project("KolonizationScenario", input, agencyB);

            StringAssert.Contains(resultA, vesselA, "Agency A's projection must contain own vessel id.");
            Assert.IsFalse(resultA.Contains(vesselB),
                "Agency A's projection must NOT contain agency B's vessel id.");
            StringAssert.Contains(resultB, vesselB, "Agency B's projection must contain own vessel id.");
            Assert.IsFalse(resultB.Contains(vesselA),
                "Agency B's projection must NOT contain agency A's vessel id.");
        }
    }
}
