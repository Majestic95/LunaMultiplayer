using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Globalization;
using System.Text;

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
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox; // restore default for adjacent test classes
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
            // Use a deliberately unrecognised scenario name (e.g. a stock or
            // mod scenario the projector doesn't claim). "MissionControlsystem"
            // is a stable placeholder — not in CareerScenarios. Stage 5.18d
            // slice (j) added ContractSystem to the recognised set, so we no
            // longer use that as the "unknown" test fixture.
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Funds = 99999 };
            var input = "name = ScenarioDeltaVApp\nweights\n{\n\trep = 1\n}\n";

            var result = AgencyScenarioProjector.Project("ScenarioDeltaVApp", input, agency);

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
        public void Project_ResearchAndDevelopment_SplicesPerAgencyTech()
        {
            // [Stage 5.17e-4] The projector strips shared Tech entries and splices
            // per-agency Tech nodes from AgencyState.TechNodes. Closes the leak
            // where Alice unlocks a tech via the router, persists to per-agency
            // state, but the NEXT scene-load shipped the stale shared tree and
            // overwrote her local R&D — review caught this pre-ship.
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Science = 250 };
            var techText = "id = basicRocketry\ncost = 5\nstate = Available";
            agency.TechNodes["basicRocketry"] = new AgencyTechNodeEntry
            {
                TechId = "basicRocketry",
                Data = System.Text.Encoding.UTF8.GetBytes(techText),
                NumBytes = techText.Length,
            };

            var input = "name = ResearchAndDevelopment\nsci = 0\n";
            var result = AgencyScenarioProjector.Project("ResearchAndDevelopment", input, agency);

            StringAssert.Contains(result, "sci = 250", "sci scalar must still be replaced by the existing pass.");
            StringAssert.Contains(result, "Tech", "Per-agency Tech node was not spliced into the R&D scenario.");
            StringAssert.Contains(result, "basicRocketry", "Tech body did not survive the round-trip.");

            // [Round-2 review SHOULD FIX] Parse-back assertion. A regression that
            // produces malformed text (stray unescaped braces, lost newlines) would
            // let the StringAssert.Contains checks pass while the client side would
            // fail to deserialize the scenario. Verify the projected text is
            // structurally valid by parsing it back via LunaConfigNode + checking
            // the Tech node is reachable as a real child node, not just substring.
            var roundTripped = new LunaConfigNode.CfgNode.ConfigNode(result) { Name = "ResearchAndDevelopment" };
            var techNodes = roundTripped.GetNodes("Tech");
            Assert.IsNotNull(techNodes, "Projected scenario does not parse back into ConfigNode-with-Tech-children.");
            // Verify the spliced tech entry's `id` field is reachable through the
            // structured ConfigNode API (not just present as a substring).
            using (var iter = techNodes.GetEnumerator())
            {
                Assert.IsTrue(iter.MoveNext(), "Projected scenario has no Tech children after parse-back.");
                var idValue = iter.Current.Value.GetValue("id")?.Value;
                Assert.AreEqual("basicRocketry", idValue,
                    "Spliced Tech's `id` field is not reachable as a real ConfigNode value — splice produced malformed text.");
            }
        }

        [TestMethod]
        public void Project_ResearchAndDevelopment_StripsSharedTechEvenWhenAgencyEmpty()
        {
            // Critical upgrade-lens behavior: an upgrade-in-place universe with
            // accumulated shared Tech nodes must NOT bleed into a fresh per-agency
            // client's R&D scenario. A fresh agency with zero TechNodes still gets
            // the shared Tech entries stripped — they start with an empty tree.
            var emptyAgency = new AgencyState { AgencyId = Guid.NewGuid(), Science = 0 };
            var inputWithSharedTech =
                "name = ResearchAndDevelopment\nsci = 1000\n" +
                "Tech\n{\n\tid = sharedTechFromUpgrade\n\tcost = 5\n}\n";

            var result = AgencyScenarioProjector.Project("ResearchAndDevelopment", inputWithSharedTech, emptyAgency);

            Assert.IsFalse(result.Contains("sharedTechFromUpgrade"),
                "Shared Tech from an upgrade universe leaked into a fresh agency's projected R&D — " +
                "WarnAboutSharedTechOnUpgrade is the operator notice; the projector strip is the actual defence.");
        }

        [TestMethod]
        public void Project_ResearchAndDevelopment_PreservesUnrelatedChildren()
        {
            // Defensive: the projector's ConfigNode round-trip must not eat other
            // child nodes the scenario carries (e.g. mod-extension blocks). Plant
            // an unrelated child and verify it survives.
            var agency = new AgencyState { AgencyId = Guid.NewGuid(), Science = 100 };
            var inputWithUnrelated =
                "name = ResearchAndDevelopment\nsci = 0\n" +
                "Tech\n{\n\tid = oldTech\n}\n" +
                "ModExtension\n{\n\tcustomKey = customValue\n}\n";

            var result = AgencyScenarioProjector.Project("ResearchAndDevelopment", inputWithUnrelated, agency);

            Assert.IsFalse(result.Contains("oldTech"),
                "Shared Tech entry was not stripped.");
            StringAssert.Contains(result, "customKey",
                "Unrelated mod-extension child node was lost during the projection round-trip.");
        }

        [TestMethod]
        public void ProjectForClient_Sandbox_ReturnsInputUnchanged()
        {
            // Sandbox has no career scalars — projection skips even with the gate on.
            // [Stage 5.17e-1] Sandbox bypass is now folded into AgencySystem.PerAgencyEnabled
            // (which requires GameMode==Career) but the test still asserts the externally
            // observable contract: gate-on + Sandbox = input unchanged.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var input = "name = Funding\nfunds = 100\n";

            var result = AgencyScenarioProjector.ProjectForClient("Funding", input, null);

            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void ProjectForClient_Science_ReturnsInputUnchanged()
        {
            // [Stage 5.17e-1, spec §10 Q-Mode] Career-only product decision: Science mode
            // closes the gate even with PerAgencyCareer=true. Without this, projection
            // would run, but the client's Funding.Instance is null in Science mode — the
            // Stage 5.17e-3 write-path routers would NRE on first mutation. Bypass at
            // projection time keeps the wire identical to the shared-agency Science path.
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
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

        [TestMethod]
        public void Project_ProgressTracking_StripsSharedEntries_WhenAgencyHasNoMatchingAchievement()
        {
            // [Stage 5.17e-6 review-finding A.2] Strict-isolation: shared
            // ProgressTracking → Progress → {child} entries must be stripped from
            // the outbound scenario when the requesting agency has no matching
            // achievement of its own. Pre-fix, the projector used upsert semantics
            // that left unmatched shared children intact (partial bleed). The
            // upgrade-in-place hazard then leaked accumulated world-firsts from
            // pre-per-agency play to every per-agency client.
            //
            // Scenario shape: ProgressTracking { Progress { Kerbin { … } FirstLaunch { … } } }.
            var input = "Progress\n{\n\tKerbin\n\t{\n\t\tcompleted = True\n\t}\n\tFirstLaunch\n\t{\n\t\tcompleted = True\n\t}\n}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            // Empty Achievements dict — no per-agency progress to splice.

            var result = AgencyScenarioProjector.Project("ProgressTracking", input, agency);

            Assert.IsFalse(result.Contains("Kerbin"),
                "Shared Progress entry 'Kerbin' must be stripped when agency has no matching achievement.");
            Assert.IsFalse(result.Contains("FirstLaunch"),
                "Shared Progress entry 'FirstLaunch' must be stripped when agency has no matching achievement.");
            // Container shape preserved — the Progress child node must still exist
            // (so client KSP's ProgressTracking ScenarioModule deserialises cleanly).
            StringAssert.Contains(result, "Progress",
                "Progress container must be preserved even when stripped of all children.");
        }

        [TestMethod]
        public void Project_ProgressTracking_StripsShared_AndSplicesAgencyAchievements()
        {
            // [Stage 5.17e-6 review-finding A.2] Mixed case: shared scenario has
            // entries the agency does NOT have AND entries the agency DOES have.
            // Result: ALL shared children stripped; only agency's spliced. Mirrors
            // SpliceAgencyStrategiesIntoScenario's strip-then-splice pattern.
            var input = "Progress\n{\n\tKerbin\n\t{\n\t\tcompleted = True\n\t}\n\tFirstLaunch\n\t{\n\t\tcompleted = True\n\t}\n}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };

            // Agency has its OWN "FirstLaunch" with different content + a new "Mun".
            agency.Achievements["FirstLaunch"] = new AgencyAchievementEntry
            {
                Id = "FirstLaunch",
                Data = Encoding.UTF8.GetBytes("agency-specific = TrueAgency\n"),
                NumBytes = Encoding.UTF8.GetByteCount("agency-specific = TrueAgency\n"),
            };
            agency.Achievements["Mun"] = new AgencyAchievementEntry
            {
                Id = "Mun",
                Data = Encoding.UTF8.GetBytes("landed = True\n"),
                NumBytes = Encoding.UTF8.GetByteCount("landed = True\n"),
            };

            var result = AgencyScenarioProjector.Project("ProgressTracking", input, agency);

            Assert.IsFalse(result.Contains("Kerbin"),
                "Unmatched shared entry 'Kerbin' must be stripped (strict isolation).");
            // The agency's "FirstLaunch" entry has unique content — confirms the
            // splice ran and the shared version did NOT survive next to it.
            StringAssert.Contains(result, "agency-specific = TrueAgency",
                "Agency's FirstLaunch achievement content must be spliced in.");
            // Agency-new entry appears.
            StringAssert.Contains(result, "Mun",
                "Agency's new 'Mun' achievement must be spliced into Progress.");
            StringAssert.Contains(result, "landed = True",
                "Agency's 'Mun' achievement content must be spliced.");
            // The shared "completed = True" line under Kerbin must NOT appear —
            // proves strict-strip rather than upsert. (Agency's FirstLaunch uses
            // distinct content so we can tell apart.)
            Assert.IsFalse(result.Contains("completed = True"),
                "Shared content of stripped entries must be gone, not just the entry names.");
        }

        // --- ContractSystem projection — Stage 5.18d slice (j) --------------
        //
        // Keeps shared CONTRACTS entries with state Offered/Generated; strips
        // others (pre-5.17d leftovers); splices per-agency Contracts;
        // strips CONTRACTS_FINISHED entirely.

        [TestMethod]
        public void Project_ContractSystem_KeepsSharedOfferedAndGenerated()
        {
            // CC's ContractPreLoader needs Offered + Generated visible across
            // agencies — those entries must survive projection. Input shape
            // matches GetScenarioInConfigNodeFormat (inner content only).
            var input =
                "CONTRACTS\n" +
                "{\n" +
                "\tCONTRACT\n" +
                "\t{\n" +
                "\t\tstate = Offered\n" +
                "\t\tname = TestOffered\n" +
                "\t}\n" +
                "\tCONTRACT\n" +
                "\t{\n" +
                "\t\tstate = Generated\n" +
                "\t\tname = TestGenerated\n" +
                "\t}\n" +
                "}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };

            var result = AgencyScenarioProjector.Project("ContractSystem", input, agency);

            StringAssert.Contains(result, "TestOffered", "Offered contracts must survive.");
            StringAssert.Contains(result, "TestGenerated", "Generated contracts must survive.");
        }

        [TestMethod]
        public void Project_ContractSystem_StripsSharedNonOfferedGenerated()
        {
            // Pre-5.17d-shape entries (Active/Completed/etc) in the shared pool
            // get stripped — under steady-state 5.17d the router removes them on
            // Accept; this projection strip catches upgrade-in-place leakage.
            // Input shape matches GetScenarioInConfigNodeFormat output (inner
            // content, no outer ContractSystem brace-wrap).
            var input =
                "CONTRACTS\n" +
                "{\n" +
                "\tCONTRACT\n" +
                "\t{\n" +
                "\t\tstate = Active\n" +
                "\t\tname = SharedActiveLeak\n" +
                "\t}\n" +
                "\tCONTRACT\n" +
                "\t{\n" +
                "\t\tstate = Completed\n" +
                "\t\tname = SharedCompletedLeak\n" +
                "\t}\n" +
                "}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };

            var result = AgencyScenarioProjector.Project("ContractSystem", input, agency);

            Assert.IsFalse(result.Contains("SharedActiveLeak"),
                "Active shared-pool entry must be stripped.");
            Assert.IsFalse(result.Contains("SharedCompletedLeak"),
                "Completed shared-pool entry must be stripped.");
        }

        [TestMethod]
        public void Project_ContractSystem_StripsContractsFinished()
        {
            // Pre-0.31 CONTRACTS_FINISHED data would otherwise leak to every
            // per-agency client. There is no per-agency Finished surface today;
            // strip the whole container's children.
            var input =
                "CONTRACTS_FINISHED\n" +
                "{\n" +
                "\tCONTRACT\n" +
                "\t{\n" +
                "\t\tstate = Completed\n" +
                "\t\tname = OldFinishedLeak\n" +
                "\t}\n" +
                "}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };

            var result = AgencyScenarioProjector.Project("ContractSystem", input, agency);

            Assert.IsFalse(result.Contains("OldFinishedLeak"),
                "Pre-0.31 CONTRACTS_FINISHED entries must be stripped.");
            StringAssert.Contains(result, "CONTRACTS_FINISHED",
                "The CONTRACTS_FINISHED container itself stays (empty); just its children are stripped.");
        }

        [TestMethod]
        public void Project_ContractSystem_SplicesAgencyContracts()
        {
            // Agency Active contract spliced into the outgoing CONTRACTS node.
            var input = "CONTRACTS\n{\n}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var contractBytes = Encoding.UTF8.GetBytes("state = Active\nname = AgencyActive\n");
            agency.Contracts.Add(new AgencyContractEntry
            {
                ContractGuid = Guid.NewGuid(),
                State = "Active",
                Data = contractBytes,
                NumBytes = contractBytes.Length,
            });

            var result = AgencyScenarioProjector.Project("ContractSystem", input, agency);

            StringAssert.Contains(result, "AgencyActive",
                "Per-agency contract must be spliced into the outgoing CONTRACTS.");
            StringAssert.Contains(result, "state = Active",
                "Per-agency contract's state must round-trip via the splice.");
        }

        [TestMethod]
        public void Project_ContractSystem_TerminalAgencyContract_GoesIntoContractsFinished()
        {
            // Consumer-lens v1 MUST FIX: Completed/Failed/etc agency contracts
            // must splice into CONTRACTS_FINISHED, NOT CONTRACTS. KSP treats
            // CONTRACTS entries as live (Mission Control's Active tab); a
            // Completed contract sitting there would persist forever as
            // "active-but-checkmarked" UX.
            var input = "CONTRACTS\n{\n}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var bytes = Encoding.UTF8.GetBytes("state = Completed\nname = OldVictoryLap\n");
            agency.Contracts.Add(new AgencyContractEntry
            {
                ContractGuid = Guid.NewGuid(),
                State = "Completed",
                Data = bytes,
                NumBytes = bytes.Length,
            });

            var result = AgencyScenarioProjector.Project("ContractSystem", input, agency);

            // The completed contract appears, but inside CONTRACTS_FINISHED —
            // KSP's archived/inactive container. Asserted indirectly: the
            // result mentions CONTRACTS_FINISHED (now non-empty) and the
            // contract's name field.
            StringAssert.Contains(result, "CONTRACTS_FINISHED",
                "Terminal contract must land under CONTRACTS_FINISHED.");
            StringAssert.Contains(result, "OldVictoryLap",
                "Terminal contract's content must round-trip through the splice.");
            // Specifically NOT in the live CONTRACTS container — look for the
            // "Completed" state appearing BEFORE the CONTRACTS_FINISHED block
            // would mean it landed in CONTRACTS. We do this by checking the
            // ordering of substring indices.
            var finishedIdx = result.IndexOf("CONTRACTS_FINISHED", StringComparison.Ordinal);
            var completedStateIdx = result.IndexOf("state = Completed", StringComparison.Ordinal);
            Assert.IsTrue(finishedIdx >= 0 && completedStateIdx > finishedIdx,
                "Terminal contract's 'state = Completed' line must appear inside CONTRACTS_FINISHED, not the live CONTRACTS container.");
        }

        [TestMethod]
        public void Project_ContractSystem_AgencyContractsPartitionedByState()
        {
            // Active stays in CONTRACTS; everything else (terminal states +
            // unknown / empty) goes to CONTRACTS_FINISHED.
            var input = "CONTRACTS\n{\n}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };

            var activeBytes = Encoding.UTF8.GetBytes("state = Active\nname = LiveContract\n");
            agency.Contracts.Add(new AgencyContractEntry
            {
                ContractGuid = Guid.NewGuid(),
                State = "Active",
                Data = activeBytes,
                NumBytes = activeBytes.Length,
            });

            // Five terminal/edge-case states — verify the "anything except
            // Active" partition rule covers them all.
            var terminalStates = new[] { "Completed", "Failed", "Cancelled", "DeadlineExpired", "Withdrawn" };
            foreach (var tState in terminalStates)
            {
                var bytes = Encoding.UTF8.GetBytes($"state = {tState}\nname = Terminal_{tState}\n");
                agency.Contracts.Add(new AgencyContractEntry
                {
                    ContractGuid = Guid.NewGuid(),
                    State = tState,
                    Data = bytes,
                    NumBytes = bytes.Length,
                });
            }

            var result = AgencyScenarioProjector.Project("ContractSystem", input, agency);

            var finishedIdx = result.IndexOf("CONTRACTS_FINISHED", StringComparison.Ordinal);
            Assert.IsTrue(finishedIdx > 0, "CONTRACTS_FINISHED container must be present.");
            // LiveContract should appear BEFORE CONTRACTS_FINISHED (inside CONTRACTS).
            var liveIdx = result.IndexOf("LiveContract", StringComparison.Ordinal);
            Assert.IsTrue(liveIdx > 0 && liveIdx < finishedIdx,
                "Active contract must appear inside CONTRACTS (before the CONTRACTS_FINISHED block).");

            // All terminal-state contracts should appear AFTER the
            // CONTRACTS_FINISHED marker.
            foreach (var tState in terminalStates)
            {
                var idx = result.IndexOf($"Terminal_{tState}", StringComparison.Ordinal);
                Assert.IsTrue(idx > finishedIdx,
                    $"Terminal contract for state '{tState}' must land inside CONTRACTS_FINISHED.");
            }
        }

        [TestMethod]
        public void Project_ContractSystem_MixedCase_StripsAndKeepsAndSplices()
        {
            // The integration case: shared has Offered (KEEP) + Active (STRIP) +
            // CONTRACTS_FINISHED (STRIP); agency has its own Active (SPLICE).
            // Input shape matches GetScenarioInConfigNodeFormat output.
            var input =
                "CONTRACTS\n" +
                "{\n" +
                "\tCONTRACT\n" +
                "\t{\n" +
                "\t\tstate = Offered\n" +
                "\t\tname = SharedOfferedKeep\n" +
                "\t}\n" +
                "\tCONTRACT\n" +
                "\t{\n" +
                "\t\tstate = Active\n" +
                "\t\tname = SharedActiveStrip\n" +
                "\t}\n" +
                "}\n" +
                "CONTRACTS_FINISHED\n" +
                "{\n" +
                "\tCONTRACT\n" +
                "\t{\n" +
                "\t\tstate = Completed\n" +
                "\t\tname = OldFinishedStrip\n" +
                "\t}\n" +
                "}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var contractBytes = Encoding.UTF8.GetBytes("state = Active\nname = AgencyActive\n");
            agency.Contracts.Add(new AgencyContractEntry
            {
                ContractGuid = Guid.NewGuid(),
                State = "Active",
                Data = contractBytes,
                NumBytes = contractBytes.Length,
            });

            var result = AgencyScenarioProjector.Project("ContractSystem", input, agency);

            StringAssert.Contains(result, "SharedOfferedKeep", "Offered shared entry kept.");
            Assert.IsFalse(result.Contains("SharedActiveStrip"), "Non-Offered shared entry stripped.");
            Assert.IsFalse(result.Contains("OldFinishedStrip"), "CONTRACTS_FINISHED children stripped.");
            StringAssert.Contains(result, "AgencyActive", "Per-agency contract spliced.");
        }

        [TestMethod]
        public void Project_ContractSystem_GateOff_PassesThroughUnchanged()
        {
            // Dual-mode silence: gate off → projector returns input unchanged
            // via the ProjectForClient gate (Project itself is invoked with a
            // state but the higher-level dispatch wouldn't fire).
            GameplaySettings.SettingsStore.PerAgencyCareer = false;

            var input =
                "CONTRACTS\n" +
                "{\n" +
                "\tCONTRACT\n" +
                "\t{\n" +
                "\t\tstate = Active\n" +
                "\t\tname = ShouldStayUnchanged\n" +
                "\t}\n" +
                "}\n";

            // Use ProjectForClient (which honours the gate) — passing null client
            // forces the early-bypass branch but the gate check fires first.
            var result = AgencyScenarioProjector.ProjectForClient("ContractSystem", input, null);

            Assert.AreEqual(input, result,
                "Gate-off projection must pass through input bit-for-bit.");
        }

        [TestMethod]
        public void Project_ContractSystem_MalformedAgencyContract_PerEntryIsolation()
        {
            // A malformed agency contract drops without aborting the batch.
            // Verifies Q6 commitment (b) — per-contract exception isolation.
            var input = "CONTRACTS\n{\n}\n";
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            // Malformed entry — empty bytes; ParseClientConfigNode returns an
            // empty node which the splice skips silently.
            agency.Contracts.Add(new AgencyContractEntry
            {
                ContractGuid = Guid.NewGuid(),
                State = "Active",
                Data = new byte[0],
                NumBytes = 0,
            });
            var goodBytes = Encoding.UTF8.GetBytes("state = Active\nname = GoodContract\n");
            agency.Contracts.Add(new AgencyContractEntry
            {
                ContractGuid = Guid.NewGuid(),
                State = "Active",
                Data = goodBytes,
                NumBytes = goodBytes.Length,
            });

            var result = AgencyScenarioProjector.Project("ContractSystem", input, agency);

            StringAssert.Contains(result, "GoodContract",
                "Sibling contracts must splice even when one entry's bytes are malformed.");
        }
    }
}
