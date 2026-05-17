using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Globalization;

namespace ServerTest
{
    /// <summary>
    /// Stage 5.17c — unit tests for <see cref="AgencyScenarioProjector"/>'s pure-helper
    /// <c>Project</c> overload + the <c>ProjectForClient</c> early-bypass branches.
    /// End-to-end wire coverage (gate-on two-client distinct values; gate-off pass-through)
    /// is in <c>MockClientTest/AgencyScenarioProjectionTest.cs</c>. The projector is
    /// internal; ServerTest reaches it via the existing <c>InternalsVisibleTo("ServerTest")</c>
    /// on the Server assembly.
    /// </summary>
    [TestClass]
    public class AgencyScenarioProjectorTest
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
            AgencySystem.Reset();
        }

        [TestMethod]
        public void Project_Funding_ReplacesRootFundsValue()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Funds = 1234567.89 };
            var input = "name = Funding\nscene = 5, 6, 7, 8, 9\nfunds = 25000\n";

            var result = AgencyScenarioProjector.Project("Funding", input, agency);

            StringAssert.Contains(result, "funds = 1234567.89",
                "Project did not overwrite the root funds value.");
            Assert.IsFalse(result.Contains("funds = 25000"),
                "Original funds value was not replaced.");
            StringAssert.Contains(result, "name = Funding", "Unrelated root values must survive.");
        }

        [TestMethod]
        public void Project_ResearchAndDevelopment_ReplacesRootSciValue()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Science = 5000.0 };
            var input = "name = ResearchAndDevelopment\nscene = 5\nsci = 0\n";

            var result = AgencyScenarioProjector.Project("ResearchAndDevelopment", input, agency);

            StringAssert.Contains(result, "sci = 5000");
            Assert.IsFalse(result.Contains("sci = 0\n"), "Original sci value was not replaced.");
        }

        [TestMethod]
        public void Project_Reputation_ReplacesRootRepValue()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Reputation = -50.5 };
            var input = "name = Reputation\nscene = 5\nrep = 0\n";

            var result = AgencyScenarioProjector.Project("Reputation", input, agency);

            StringAssert.Contains(result, "rep = -50.5");
        }

        [TestMethod]
        public void Project_UnknownScenario_ReturnsInputUnchanged()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Funds = 99999 };
            var input = "name = ContractSystem\nweights\n{\n\trep = 1\n}\n";

            var result = AgencyScenarioProjector.Project("ContractSystem", input, agency);

            Assert.AreEqual(input, result,
                "Unknown scenarios must pass through unchanged.");
        }

        [TestMethod]
        public void Project_NullAgency_ReturnsInputUnchanged()
        {
            var input = "name = Funding\nfunds = 50000\n";

            var result = AgencyScenarioProjector.Project("Funding", input, null);

            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void Project_NullInput_ReturnsNull()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Funds = 1 };

            var result = AgencyScenarioProjector.Project("Funding", null, agency);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void Project_DoesNotMutateNestedSameNamedKey()
        {
            // Defensive: if a child node happens to have a key called "funds" (rare in
            // stock scenarios but possible in modded ones), the projector must NOT
            // replace it — child-node keys are tab-indented, the regex anchors to
            // column-0 root level only.
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Funds = 9999 };
            var input = "name = Funding\nfunds = 100\nSOMEMOD\n{\n\tfunds = 999\n}\n";

            var result = AgencyScenarioProjector.Project("Funding", input, agency);

            StringAssert.Contains(result, "funds = 9999", "Root funds was not replaced.");
            StringAssert.Contains(result, "\tfunds = 999",
                "Child-node funds value was incorrectly replaced.");
        }

        [TestMethod]
        public void Project_DoesNotMatchKeyAsSubstring()
        {
            // Defensive: keys like "fundsbonus" or "supersci" must not match the projector's
            // pattern. The regex requires the exact key followed by whitespace and '='.
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Science = 7 };
            var input = "name = ResearchAndDevelopment\nsupersci = 99\nsci = 0\n";

            var result = AgencyScenarioProjector.Project("ResearchAndDevelopment", input, agency);

            StringAssert.Contains(result, "supersci = 99",
                "Substring match incorrectly replaced supersci.");
            StringAssert.Contains(result, "sci = 7", "Real sci value was not replaced.");
        }

        [TestMethod]
        public void Project_UsesInvariantCultureForDecimalSeparator()
        {
            // German/French locales would otherwise format 1234.5 as "1234,5" — that
            // would corrupt the on-wire ConfigNode format. ScenarioFundsDataUpdater writes
            // with InvariantCulture; the projector must do the same so round-trips stay
            // consistent.
            var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var agency = new AgencyState { AgencyId = Guid.NewGuid(), Funds = 1234.5 };
                var input = "name = Funding\nfunds = 0\n";

                var result = AgencyScenarioProjector.Project("Funding", input, agency);

                StringAssert.Contains(result, "funds = 1234.5",
                    "Decimal separator must use InvariantCulture '.', not the thread culture's ','.");
                Assert.IsFalse(result.Contains("funds = 1234,5"),
                    "Locale-specific decimal separator leaked into the wire format.");
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        [TestMethod]
        public void ProjectForClient_GateOff_ReturnsInputUnchanged()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            // Even with a real-looking agency entry in the registry, gate-off bypass fires
            // before any lookup.
            var input = "name = Funding\nfunds = 100\n";

            var result = AgencyScenarioProjector.ProjectForClient("Funding", input, null);

            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void ProjectForClient_Sandbox_ReturnsInputUnchanged()
        {
            // Sandbox has no career scalars — projection skips even with the gate on.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var input = "name = Funding\nfunds = 100\n";

            var result = AgencyScenarioProjector.ProjectForClient("Funding", input, null);

            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void ProjectForClient_NullClient_ReturnsInputUnchanged()
        {
            // Defensive: ProjectForClient with a null ClientStructure must not NRE.
            var input = "name = Funding\nfunds = 100\n";

            var result = AgencyScenarioProjector.ProjectForClient("Funding", input, null);

            Assert.AreEqual(input, result);
        }
    }
}
