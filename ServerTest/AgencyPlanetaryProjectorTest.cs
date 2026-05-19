using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// Phase 3 Slice C — unit tests for <see cref="AgencyScenarioProjector"/>'s
    /// new <c>PlanetaryLogisticsScenario</c> case + <c>SpliceAgencyPlanetaryEntries</c>.
    /// Same shape as the Slice B <see cref="AgencyKolonyProjectorTest"/>;
    /// differences trace to (a) the body-and-resource partition key vs
    /// vessel-and-body, and (b) the 4-field MKS-1:1 mapping (no Reputation→Rep
    /// remap). End-to-end wire coverage in
    /// <c>MockClientTest/AgencyPlanetaryRoutingTest.cs</c>.
    /// </summary>
    [TestClass]
    public class AgencyPlanetaryProjectorTest
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
        public void Project_EmptyAgencyPlanetary_StripsAllSharedEntries()
        {
            // Strip-then-splice contract. An agency with zero per-agency
            // entries projects an empty PLANETARY_LOGISTICS container, even
            // if the shared scenario carried entries from peers — those
            // peers' planetary balances must not leak into this agency's view.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = PlanetaryLogisticsScenario\nscene = 7, 8\n" +
                        "PLANETARY_LOGISTICS\n{\n" +
                        "\tLOGISTICS_ENTRY\n\t{\n\t\tBodyIndex = 5\n\t\tResourceName = Hydrates\n\t\tStoredQuantity = 100\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, agency);

            Assert.IsFalse(result.Contains("ResourceName = Hydrates"),
                "Shared LOGISTICS_ENTRY must be stripped from the projected scenario.");
            Assert.IsFalse(result.Contains("StoredQuantity = 100"),
                "Stripped entry's values must not appear in the projected output.");
            StringAssert.Contains(result, "PLANETARY_LOGISTICS",
                "PLANETARY_LOGISTICS container itself must survive even when emptied.");
        }

        [TestMethod]
        public void Project_AgencyWithEntries_SplicesOnlyOwn()
        {
            // Per-agency view: the splice emits only the requesting agency's
            // entries. Shared scenario has 1 peer entry; agency has 2 own
            // entries on distinct (body, resource) pairs; output has exactly
            // 2 entries (both agency-owned).
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var vesselA = Guid.NewGuid();
            var vesselB = Guid.NewGuid();
            agency.PlanetaryEntries["5|Hydrates"] = new AgencyPlanetaryEntry
            {
                OwningVesselId = vesselA, BodyIndex = 5, ResourceName = "Hydrates", StoredQuantity = 111.0,
            };
            agency.PlanetaryEntries["7|Karbonite"] = new AgencyPlanetaryEntry
            {
                OwningVesselId = vesselB, BodyIndex = 7, ResourceName = "Karbonite", StoredQuantity = 222.0,
            };

            var input = "name = PlanetaryLogisticsScenario\n" +
                        "PLANETARY_LOGISTICS\n{\n" +
                        "\tLOGISTICS_ENTRY\n\t{\n\t\tBodyIndex = 9\n\t\tResourceName = MetallicOre\n\t\tStoredQuantity = 999\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, agency);

            // Peer's entry stripped:
            Assert.IsFalse(result.Contains("ResourceName = MetallicOre"),
                "Peer's LOGISTICS_ENTRY must NOT appear in this agency's projection.");
            Assert.IsFalse(result.Contains("StoredQuantity = 999"),
                "Peer's StoredQuantity must NOT appear in this agency's projection.");

            // Own entries spliced:
            StringAssert.Contains(result, "ResourceName = Hydrates", "Agency's first resource must appear in projection.");
            StringAssert.Contains(result, "ResourceName = Karbonite", "Agency's second resource must appear in projection.");
            StringAssert.Contains(result, "StoredQuantity = 111", "Agency's first StoredQuantity value must appear.");
            StringAssert.Contains(result, "StoredQuantity = 222", "Agency's second StoredQuantity value must appear.");
        }

        [TestMethod]
        public void Project_NullAgency_ReturnsInputUnchanged()
        {
            var input = "name = PlanetaryLogisticsScenario\nPLANETARY_LOGISTICS { }\n";
            var result = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, null);
            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void Project_NullInput_ReturnsInputUnchanged()
        {
            // [Phase 3 Slice C / consumer-lens SHOULD FIX SF#6] Assert reads
            // as "null input returns input unchanged" instead of "result is
            // null" — both pass on the same call (null input maps to null
            // result here), but the Assert.AreEqual(input, result) shape
            // signals to a future Slice D author cloning this test that the
            // pass-through contract is the load-bearing property, not the
            // null-ness of the result.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            string input = null;
            var result = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, agency);
            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void Project_MissingPlanetaryContainer_AddsContainer_NoSharedLeak()
        {
            // A scenario blob that lacks the PLANETARY_LOGISTICS child entirely
            // (fresh PlanetaryLogisticsScenario before any save) should not
            // throw — the splice creates the container, then the empty agency
            // leaves it empty. Defensive against KSP-side first-load shape.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = PlanetaryLogisticsScenario\nscene = 7, 8\n";

            var result = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, agency);

            StringAssert.Contains(result, "PLANETARY_LOGISTICS");
            Assert.IsFalse(result.Contains("LOGISTICS_ENTRY"),
                "Empty agency must not introduce LOGISTICS_ENTRY children.");
        }

        [TestMethod]
        public void Project_NoOwningVesselIdEmitted_FieldIsForkOnly()
        {
            // OwningVesselId is the LMP-side fork addition (not in MKS' on-disk
            // PlanetaryLogisticsEntry shape). The projector deliberately does
            // NOT emit it to the projected scenario — KSP-side
            // ResourceUtilities.LoadNodeProperties<PlanetaryLogisticsEntry>
            // would silently ignore it, but emitting it is wire bloat for no
            // reader and would clutter on-disk universe state if KSP later
            // re-saves the scenario.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var vesselId = Guid.NewGuid();
            agency.PlanetaryEntries["1|Hydrates"] = new AgencyPlanetaryEntry
            {
                OwningVesselId = vesselId, BodyIndex = 1, ResourceName = "Hydrates", StoredQuantity = 5.0,
            };
            var input = "name = PlanetaryLogisticsScenario\nPLANETARY_LOGISTICS { }\n";

            var result = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, agency);

            StringAssert.Contains(result, "BodyIndex = 1", "Projector must emit BodyIndex.");
            StringAssert.Contains(result, "ResourceName = Hydrates", "Projector must emit ResourceName.");
            StringAssert.Contains(result, "StoredQuantity = 5", "Projector must emit StoredQuantity.");
            Assert.IsFalse(result.Contains("OwningVesselId"),
                "Projector must NOT emit OwningVesselId (LMP-side fork addition, not part of MKS on-disk shape).");
            Assert.IsFalse(result.Contains(vesselId.ToString("N")),
                "Projector must NOT emit the Guid value either (defence against accidental serialization).");
        }

        [TestMethod]
        public void Project_DoubleValues_UsesInvariantCulture()
        {
            // Locale safety. A server with comma-decimal thread culture must
            // emit dot-decimal values so KSP-side parsers read correctly.
            var prevCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var agency = new AgencyState { AgencyId = Guid.NewGuid() };
                agency.PlanetaryEntries["1|Hydrates"] = new AgencyPlanetaryEntry
                {
                    OwningVesselId = Guid.NewGuid(),
                    BodyIndex = 1,
                    ResourceName = "Hydrates",
                    StoredQuantity = 1234.56,
                };
                var input = "name = PlanetaryLogisticsScenario\nPLANETARY_LOGISTICS { }\n";

                var result = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, agency);

                StringAssert.Contains(result, "StoredQuantity = 1234.56",
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
            // PlanetaryEntries only, never the other's.
            var agencyA = new AgencyState { AgencyId = Guid.NewGuid() };
            var agencyB = new AgencyState { AgencyId = Guid.NewGuid() };
            agencyA.PlanetaryEntries["1|Hydrates"] = new AgencyPlanetaryEntry
            {
                OwningVesselId = Guid.NewGuid(), BodyIndex = 1, ResourceName = "Hydrates", StoredQuantity = 100.0,
            };
            agencyB.PlanetaryEntries["1|Karbonite"] = new AgencyPlanetaryEntry
            {
                OwningVesselId = Guid.NewGuid(), BodyIndex = 1, ResourceName = "Karbonite", StoredQuantity = 200.0,
            };
            var input = "name = PlanetaryLogisticsScenario\nPLANETARY_LOGISTICS { }\n";

            var resultA = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, agencyA);
            var resultB = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, agencyB);

            StringAssert.Contains(resultA, "ResourceName = Hydrates", "Agency A's projection must contain own resource.");
            Assert.IsFalse(resultA.Contains("ResourceName = Karbonite"),
                "Agency A's projection must NOT contain agency B's resource.");
            StringAssert.Contains(resultB, "ResourceName = Karbonite", "Agency B's projection must contain own resource.");
            Assert.IsFalse(resultB.Contains("ResourceName = Hydrates"),
                "Agency B's projection must NOT contain agency A's resource.");
        }

        [TestMethod]
        public void Project_SameBodyResourceCollapse_OneEntryPerAgency()
        {
            // Body-and-resource keyed partition means an agency can never have
            // more than one entry per (body, resource) — multiple of an agency's
            // vessels pumping the same resource on the same body collapse into
            // one entry (the intended planetary-pool product). Sanity-check the
            // projector renders one entry per dict key.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.PlanetaryEntries["5|Hydrates"] = new AgencyPlanetaryEntry
            {
                OwningVesselId = Guid.NewGuid(), BodyIndex = 5, ResourceName = "Hydrates", StoredQuantity = 42.0,
            };
            var input = "name = PlanetaryLogisticsScenario\n" +
                        "PLANETARY_LOGISTICS\n{\n" +
                        "\tLOGISTICS_ENTRY\n\t{\n\t\tBodyIndex = 5\n\t\tResourceName = Hydrates\n\t\tStoredQuantity = 999\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("PlanetaryLogisticsScenario", input, agency);

            // Shared 999 stripped; agency's 42 emitted exactly once.
            Assert.IsFalse(result.Contains("StoredQuantity = 999"));
            var firstIdx = result.IndexOf("StoredQuantity = 42", StringComparison.Ordinal);
            Assert.IsTrue(firstIdx >= 0, "Agency entry must be present in projection.");
            var secondIdx = result.IndexOf("StoredQuantity = 42", firstIdx + 1, StringComparison.Ordinal);
            Assert.AreEqual(-1, secondIdx, "Agency entry must appear exactly once (no duplicates).");
        }
    }
}
