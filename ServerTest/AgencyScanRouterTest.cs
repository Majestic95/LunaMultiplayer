using LmpCommon.Enums;
using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Vessel.Classes;
using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ServerTest
{
    /// <summary>
    /// [Mod-compat S2 — SCANsat] Unit tests for the deterministic core of
    /// <see cref="AgencyScanRouter"/>: the early-return branches of
    /// <c>TryRoute</c> + the internal <c>UpsertCoverageEntries</c> /
    /// <c>UpsertScannerEntries</c> helpers (including cross-agency rejection,
    /// vessel-not-in-store DROP, multi-Sensor nesting, malformed-entry
    /// isolation). The full TryRoute path with a live
    /// <see cref="Server.Client.ClientStructure"/> requires a NetConnection
    /// (constructor-mandatory parameter) which would bring up the whole server
    /// runtime; we pin the same semantics via the internal helpers instead —
    /// matches the <see cref="AgencyKolonyRouterTest"/> + <c>AgencyTechRouterTest</c>
    /// internal-helper-pin pattern.
    ///
    /// <para>End-to-end wire coverage (handshake → router → projector → wire
    /// echo) is NOT in MockClientTest for S2 — the SCANcontroller blob is
    /// heavy and the helper-pin coverage here + the projector tests are
    /// sufficient for v1. See <c>S2-SCANsat.md</c> breakage analysis.</para>
    /// </summary>
    [TestClass]
    public class AgencyScanRouterTest
    {
        private static readonly string XmlExamplePath = Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");

        private readonly Guid _agencyAlice = Guid.NewGuid();
        private readonly Guid _agencyBob = Guid.NewGuid();
        private readonly Guid _vesselAlice = Guid.NewGuid();
        private readonly Guid _vesselBob = Guid.NewGuid();
        private readonly Guid _vesselUnassigned = Guid.NewGuid();

        private AgencyState _alice;
        private AgencyState _bob;

        [TestInitialize]
        public void Setup()
        {
            VesselStoreSystem.CurrentVessels.Clear();
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            _alice = new AgencyState
            {
                AgencyId = _agencyAlice,
                OwningPlayerName = "alice",
                DisplayName = "Alice Corp",
            };
            _bob = new AgencyState
            {
                AgencyId = _agencyBob,
                OwningPlayerName = "bob",
                DisplayName = "Bob Inc",
            };
            AgencySystem.Agencies[_agencyAlice] = _alice;
            AgencySystem.Agencies[_agencyBob] = _bob;
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            SeedVessel(_vesselAlice, _agencyAlice);
            SeedVessel(_vesselBob, _agencyBob);
            SeedVessel(_vesselUnassigned, Guid.Empty);
        }

        [TestCleanup]
        public void Teardown()
        {
            VesselStoreSystem.CurrentVessels.Clear();
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
        }

        // -------------------------------------------------------------------
        // Early-return branches via TryRoute(null/...) — dual-mode silence
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryRoute_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var scenario = BuildScenario(coverageBodies: new[] { "Kerbin" }, scannersVesselIds: new Guid[0]);

            var handled = AgencyScanRouter.TryRoute(null, scenario);

            Assert.IsFalse(handled, "Gate off must return false (legacy SHA pass runs unchanged)");
            Assert.AreEqual(0, _alice.Coverage.Count);
        }

        [TestMethod]
        public void TryRoute_NullClient_ReturnsFalse()
        {
            // Boot-time scenario loads call RawConfigNodeInsertOrUpdate with
            // client=null; router must short-circuit so the legacy AddOrUpdate
            // path runs.
            var scenario = BuildScenario(coverageBodies: new[] { "Kerbin" }, scannersVesselIds: new Guid[0]);
            var handled = AgencyScanRouter.TryRoute(null, scenario);
            Assert.IsFalse(handled);
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalseWithoutMutating()
        {
            // PerAgencyEnabled = PerAgencyCareer && GameMode == Career. Sandbox
            // closes the gate even with PerAgencyCareer=true.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var scenario = BuildScenario(coverageBodies: new[] { "Kerbin" }, scannersVesselIds: new Guid[0]);
            var handled = AgencyScanRouter.TryRoute(null, scenario);
            Assert.IsFalse(handled);
        }

        // -------------------------------------------------------------------
        // Coverage upsert (via internal helper)
        // -------------------------------------------------------------------

        [TestMethod]
        public void UpsertCoverageEntries_TwoBodies_UpsertsForOwningAgency()
        {
            var scenario = BuildScenario(coverageBodies: new[] { "Kerbin", "Mun" }, scannersVesselIds: new Guid[0]);

            var any = AgencyScanRouter.UpsertCoverageEntries(scenario, _alice);

            Assert.IsTrue(any);
            Assert.AreEqual(2, _alice.Coverage.Count);
            Assert.IsTrue(_alice.Coverage.ContainsKey("Kerbin"));
            Assert.IsTrue(_alice.Coverage.ContainsKey("Mun"));
            Assert.AreEqual(0, _bob.Coverage.Count, "Peer agency MUST NOT see Alice's coverage");
        }

        [TestMethod]
        public void UpsertCoverageEntries_BodyMissingName_DropsEntryKeepsOthers()
        {
            var scenario = new ConfigNode("") { Name = "SCANcontroller" };
            var progress = new ConfigNode("") { Name = "Progress" };
            scenario.AddNode(progress);
            var badBody = new ConfigNode("") { Name = "Body" };
            // Name field deliberately missing.
            badBody.CreateValue(new CfgNodeValue<string, string>("Map", "abc"));
            progress.AddNode(badBody);
            var goodBody = new ConfigNode("") { Name = "Body" };
            goodBody.CreateValue(new CfgNodeValue<string, string>("Name", "Eve"));
            goodBody.CreateValue(new CfgNodeValue<string, string>("Map", "xyz"));
            progress.AddNode(goodBody);

            AgencyScanRouter.UpsertCoverageEntries(scenario, _alice);

            Assert.AreEqual(1, _alice.Coverage.Count, "Malformed Body dropped, sibling survives (Invariant 4)");
            Assert.IsTrue(_alice.Coverage.ContainsKey("Eve"));
        }

        [TestMethod]
        public void UpsertCoverageEntries_NoProgressContainer_ReturnsFalse()
        {
            // Operator-seed blob without any Progress container is a no-op for
            // Coverage. UpsertCoverageEntries returns false (no entries added).
            var scenario = new ConfigNode("") { Name = "SCANcontroller" };
            scenario.CreateValue(new CfgNodeValue<string, string>("mainMapVisible", "True"));

            var any = AgencyScanRouter.UpsertCoverageEntries(scenario, _alice);

            Assert.IsFalse(any);
            Assert.AreEqual(0, _alice.Coverage.Count);
        }

        // -------------------------------------------------------------------
        // Scanners upsert + cross-agency rejection + multi-Sensor
        // -------------------------------------------------------------------

        [TestMethod]
        public void UpsertScannerEntries_VesselOwnedBySender_UpsertsWithNestedSensors()
        {
            var scenario = new ConfigNode("") { Name = "SCANcontroller" };
            scenario.AddNode(BuildScannersBlock(new[]
            {
                BuildVesselWithSensors(_vesselAlice, "AliceScout", new (int, float, double, double, double, bool)[]
                {
                    (16, 3.5f, 5000, 500000, 150000, false),
                    (8,  1.0f, 0,    10000,  5000,   true),
                }),
            }));

            AgencyScanRouter.UpsertScannerEntries(scenario, _alice, _agencyAlice);

            Assert.IsTrue(_alice.Scanners.TryGetValue(_vesselAlice, out var entry));
            Assert.AreEqual(2, entry.Sensors.Count, "Multi-Sensor nesting preserved (Decision §9)");
            Assert.AreEqual("AliceScout", entry.VesselName);
        }

        [TestMethod]
        public void UpsertScannerEntries_VesselOwnedByPeer_RejectsClaim()
        {
            // Alice (agencyAlice) attempts to upsert a Vessel record for a
            // vessel owned by agencyBob. Cross-agency rejection per Decision §3.
            var scenario = new ConfigNode("") { Name = "SCANcontroller" };
            scenario.AddNode(BuildScannersBlock(new[]
            {
                BuildVesselWithSensors(_vesselBob, "ScrapingBobs", new (int, float, double, double, double, bool)[]
                {
                    (16, 3f, 0, 1000, 500, false),
                }),
            }));

            AgencyScanRouter.UpsertScannerEntries(scenario, _alice, _agencyAlice);

            Assert.IsFalse(_alice.Scanners.ContainsKey(_vesselBob),
                "Cross-agency vessel claim must be rejected");
            Assert.IsFalse(_bob.Scanners.ContainsKey(_vesselBob),
                "Peer's actual record must NOT be touched");
        }

        [TestMethod]
        public void UpsertScannerEntries_UnassignedVessel_AllowsAnyAgencyClaim()
        {
            // Pre-0.31 upgrade-in-place: vessel with OwningAgencyId=Empty
            // (Unassigned sentinel) bypasses cross-agency check per spec §10 Q3.
            var scenario = new ConfigNode("") { Name = "SCANcontroller" };
            scenario.AddNode(BuildScannersBlock(new[]
            {
                BuildVesselWithSensors(_vesselUnassigned, "Orphan", new (int, float, double, double, double, bool)[]
                {
                    (16, 3f, 0, 1000, 500, false),
                }),
            }));

            AgencyScanRouter.UpsertScannerEntries(scenario, _alice, _agencyAlice);

            Assert.IsTrue(_alice.Scanners.ContainsKey(_vesselUnassigned),
                "Unassigned-sentinel vessel may be claimed by any agency (spec §10 Q3)");
        }

        [TestMethod]
        public void UpsertScannerEntries_VesselNotInStore_DropsSilently()
        {
            var ghostVessel = Guid.NewGuid(); // not in CurrentVessels
            var scenario = new ConfigNode("") { Name = "SCANcontroller" };
            scenario.AddNode(BuildScannersBlock(new[]
            {
                BuildVesselWithSensors(ghostVessel, "Ghost", new (int, float, double, double, double, bool)[]
                {
                    (16, 3f, 0, 1000, 500, false),
                }),
            }));

            AgencyScanRouter.UpsertScannerEntries(scenario, _alice, _agencyAlice);

            Assert.IsFalse(_alice.Scanners.ContainsKey(ghostVessel),
                "Vessel-not-in-store DROP (no consumer at projection time)");
        }

        [TestMethod]
        public void UpsertScannerEntries_MalformedGuid_SkipsButKeepsSiblings()
        {
            var scenario = new ConfigNode("") { Name = "SCANcontroller" };
            var scanners = new ConfigNode("") { Name = "Scanners" };
            scenario.AddNode(scanners);
            // Malformed guid:
            var bad = new ConfigNode("") { Name = "Vessel" };
            bad.CreateValue(new CfgNodeValue<string, string>("guid", "NOT-A-GUID"));
            scanners.AddNode(bad);
            // Good vessel:
            scanners.AddNode(BuildVesselWithSensors(_vesselAlice, "Good", new (int, float, double, double, double, bool)[]
            {
                (16, 3f, 0, 1000, 500, false),
            }));

            AgencyScanRouter.UpsertScannerEntries(scenario, _alice, _agencyAlice);

            Assert.AreEqual(1, _alice.Scanners.Count, "Malformed-guid Vessel dropped, sibling survives");
            Assert.IsTrue(_alice.Scanners.ContainsKey(_vesselAlice));
        }

        // -------------------------------------------------------------------
        // Baseline-seed (catch-up fix 2026-05-22)
        // -------------------------------------------------------------------

        [TestMethod]
        public void SeedBaselineIfMissing_FreshUniverse_InsertsStrippedBaseline()
        {
            // The bug we're fixing: under gate=on, the dispatch in
            // ScenarioBaseDataUpdater suppresses CurrentScenarios.AddOrUpdate
            // when the router returns true. On a fresh per-agency universe with
            // SCANsat installed, CurrentScenarios["SCANcontroller"] never gets
            // populated by any other path — and SendScenariosToClient at
            // HandshakeSystem.cs:173 then iterates CurrentScenarios.Keys, finds
            // nothing, and sends an empty catch-up. The fix populates a
            // stripped baseline on first inbound.
            ScenarioStoreSystem.CurrentScenarios.TryRemove("SCANcontroller", out _);

            var inbound = BuildScenario(coverageBodies: new[] { "Kerbin", "Minmus" },
                                        scannersVesselIds: new Guid[0]);
            // Add a root scalar that should survive the strip — verifies we keep
            // the operator-configurable UI state.
            inbound.CreateValue(new CfgNodeValue<string, string>("mainMapVisible", "True"));

            AgencyScanRouter.SeedBaselineIfMissing(inbound);

            Assert.IsTrue(ScenarioStoreSystem.CurrentScenarios.TryGetValue("SCANcontroller", out var baseline),
                "Baseline must be inserted on first inbound when CurrentScenarios has no SCANcontroller key — without this the catch-up path sends nothing on reconnect.");
            Assert.AreEqual("True", baseline.GetValue("mainMapVisible")?.Value,
                "Root scalar (mainMapVisible) must survive the strip.");

            var progress = baseline.GetNode("Progress")?.Value;
            Assert.IsNotNull(progress, "Progress container should be retained (empty) so the projector has a sibling to splice into.");
            Assert.AreEqual(0, progress.GetNodes("Body").Count(),
                "Body children must be stripped — they belong in per-agency state, not the shared baseline.");
        }

        [TestMethod]
        public void SeedBaselineIfMissing_AlreadyPresent_DoesNotOverwrite()
        {
            // Operator-seeded baseline OR pre-gate-off-era accumulated state.
            // GetOrAdd is idempotent — the existing entry wins.
            ScenarioStoreSystem.CurrentScenarios.TryRemove("SCANcontroller", out _);
            var operatorSeed = new ConfigNode("") { Name = "SCANcontroller" };
            operatorSeed.CreateValue(new CfgNodeValue<string, string>("MARKER", "operator-original"));
            ScenarioStoreSystem.CurrentScenarios["SCANcontroller"] = operatorSeed;

            var inbound = BuildScenario(coverageBodies: new[] { "Kerbin" },
                                        scannersVesselIds: new Guid[0]);
            inbound.CreateValue(new CfgNodeValue<string, string>("MARKER", "from-client"));

            AgencyScanRouter.SeedBaselineIfMissing(inbound);

            Assert.IsTrue(ScenarioStoreSystem.CurrentScenarios.TryGetValue("SCANcontroller", out var stored));
            Assert.AreEqual("operator-original", stored.GetValue("MARKER")?.Value,
                "Existing baseline must NOT be overwritten — GetOrAdd preserves the original.");
        }

        [TestMethod]
        public void BuildStrippedBaseline_StripsBodyAndVesselChildren()
        {
            var inbound = BuildScenario(coverageBodies: new[] { "Kerbin", "Mun" },
                                        scannersVesselIds: new[] { _vesselAlice });

            var baseline = AgencyScanRouter.BuildStrippedBaseline(inbound);

            var progress = baseline.GetNode("Progress")?.Value;
            Assert.IsNotNull(progress, "Progress container retained (empty).");
            Assert.AreEqual(0, progress.GetNodes("Body").Count(),
                "All Body children must be stripped.");

            var scanners = baseline.GetNode("Scanners")?.Value;
            Assert.IsNotNull(scanners, "Scanners container retained (empty).");
            Assert.AreEqual(0, scanners.GetNodes("Vessel").Count(),
                "All Vessel children must be stripped — player-progress data has no place in the shared baseline.");
        }

        [TestMethod]
        public void BuildStrippedBaseline_IsolatedFromInboundTree()
        {
            // The baseline must be a deep clone, not a shared sub-tree — if the
            // inbound is later mutated by a different code path, the baseline
            // in CurrentScenarios must not see those mutations. Verify by
            // adding a Body to the inbound's Progress container AFTER the
            // build, and confirming the baseline's Progress stays empty.
            var inbound = BuildScenario(coverageBodies: new[] { "Kerbin" },
                                        scannersVesselIds: new Guid[0]);

            var baseline = AgencyScanRouter.BuildStrippedBaseline(inbound);

            // Mutate the inbound after build: add a new Body to its Progress container.
            var inboundProgress = inbound.GetNode("Progress")?.Value;
            Assert.IsNotNull(inboundProgress);
            var injected = new ConfigNode("") { Name = "Body" };
            injected.CreateValue(new CfgNodeValue<string, string>("Name", "InjectedAfterBuild"));
            inboundProgress.AddNode(injected);

            // Baseline must NOT see the post-build mutation — that would mean it
            // shares the inbound's Progress sub-tree (silent reference leak).
            var baselineProgress = baseline.GetNode("Progress")?.Value;
            Assert.IsNotNull(baselineProgress);
            Assert.AreEqual(0, baselineProgress.GetNodes("Body").Count(),
                "Baseline must be fully isolated from the inbound's tree — round-trip via ToString severs any shared sub-node references.");
        }

        [TestMethod]
        public void SeedBaselineIfMissing_NullInbound_NoOp()
        {
            ScenarioStoreSystem.CurrentScenarios.TryRemove("SCANcontroller", out _);
            AgencyScanRouter.SeedBaselineIfMissing(null);
            Assert.IsFalse(ScenarioStoreSystem.CurrentScenarios.ContainsKey("SCANcontroller"),
                "Null inbound must not crash and must not insert a phantom key.");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static ConfigNode BuildScenario(string[] coverageBodies, Guid[] scannersVesselIds)
        {
            var scenario = new ConfigNode("") { Name = "SCANcontroller" };
            if (coverageBodies != null && coverageBodies.Length > 0)
            {
                var progress = new ConfigNode("") { Name = "Progress" };
                scenario.AddNode(progress);
                foreach (var body in coverageBodies)
                {
                    var bNode = new ConfigNode("") { Name = "Body" };
                    bNode.CreateValue(new CfgNodeValue<string, string>("Name", body));
                    bNode.CreateValue(new CfgNodeValue<string, string>("Map", "FAKE_MAP_BLOB"));
                    bNode.CreateValue(new CfgNodeValue<string, string>("MinHeightRange", "0"));
                    bNode.CreateValue(new CfgNodeValue<string, string>("MaxHeightRange", "1000"));
                    bNode.CreateValue(new CfgNodeValue<string, string>("PaletteName", "Default"));
                    bNode.CreateValue(new CfgNodeValue<string, string>("PaletteSize", "7"));
                    bNode.CreateValue(new CfgNodeValue<string, string>("PaletteReverse", "False"));
                    bNode.CreateValue(new CfgNodeValue<string, string>("PaletteDiscrete", "False"));
                    bNode.CreateValue(new CfgNodeValue<string, string>("Disabled", "False"));
                    progress.AddNode(bNode);
                }
            }
            if (scannersVesselIds != null && scannersVesselIds.Length > 0)
            {
                scenario.AddNode(BuildScannersBlock(
                    scannersVesselIds.Select(g => BuildVesselWithSensors(g, "Test", new (int, float, double, double, double, bool)[]
                    {
                        (16, 3f, 0, 1000, 500, false),
                    })).ToArray()));
            }
            return scenario;
        }

        private static ConfigNode BuildScannersBlock(ConfigNode[] vesselNodes)
        {
            var scanners = new ConfigNode("") { Name = "Scanners" };
            foreach (var v in vesselNodes)
                scanners.AddNode(v);
            return scanners;
        }

        private static ConfigNode BuildVesselWithSensors(Guid vesselId, string name,
            (int sensorType, float fov, double minAlt, double maxAlt, double bestAlt, bool requireLight)[] sensors)
        {
            var v = new ConfigNode("") { Name = "Vessel" };
            v.CreateValue(new CfgNodeValue<string, string>("guid", vesselId.ToString("D", CultureInfo.InvariantCulture)));
            v.CreateValue(new CfgNodeValue<string, string>("name", name));
            foreach (var s in sensors)
            {
                var sNode = new ConfigNode("") { Name = "Sensor" };
                sNode.CreateValue(new CfgNodeValue<string, string>("type", s.sensorType.ToString(CultureInfo.InvariantCulture)));
                sNode.CreateValue(new CfgNodeValue<string, string>("fov", s.fov.ToString("R", CultureInfo.InvariantCulture)));
                sNode.CreateValue(new CfgNodeValue<string, string>("min_alt", s.minAlt.ToString("R", CultureInfo.InvariantCulture)));
                sNode.CreateValue(new CfgNodeValue<string, string>("max_alt", s.maxAlt.ToString("R", CultureInfo.InvariantCulture)));
                sNode.CreateValue(new CfgNodeValue<string, string>("best_alt", s.bestAlt.ToString("R", CultureInfo.InvariantCulture)));
                sNode.CreateValue(new CfgNodeValue<string, string>("require_light", s.requireLight.ToString(CultureInfo.InvariantCulture)));
                v.AddNode(sNode);
            }
            return v;
        }

        private static void SeedVessel(Guid vesselId, Guid owningAgencyId)
        {
            var vessel = LoadSampleVessel();
            vessel.OwningAgencyId = owningAgencyId;
            Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel),
                "Test setup: vessel must not already be in the store.");
        }

        private static Vessel LoadSampleVessel()
        {
            return new Vessel(File.ReadAllText(Directory.GetFiles(XmlExamplePath).OrderBy(p => p, StringComparer.Ordinal).First()));
        }
    }
}
