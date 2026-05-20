using LmpCommon.Enums;
using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Scenario;
using System;
using System.IO;
using System.Linq;

namespace ServerTest
{
    /// <summary>
    /// Stage 6 Phase 6.3 — pins the per-agency kerbal disk layout helpers +
    /// lifecycle hooks. Phase 6.3 ships:
    ///
    /// 1. <see cref="AgencySystem.GetKerbalsPathForAgency"/> — path helper
    ///    resolving to <c>Universe/Agencies/{guid:N}/Kerbals/</c>.
    /// 2. Private <c>SeedStockKerbalsForAgency</c> helper writing Jeb / Bill /
    ///    Bob / Valentina templates from <c>Server.Properties.Resources</c>.
    /// 3. Wiring into <see cref="AgencySystem.RegisterAgency"/> (mint),
    ///    <see cref="AgencySystem.LoadAgency"/> (backfill via
    ///    <c>LoadAgencyFromFile</c>), and <see cref="AgencySystem.TryDeleteAgency"/>
    ///    (cascade subdir delete).
    /// 4. <see cref="FileHandler.FolderDeleteRecursive"/> — sibling of
    ///    <see cref="FileHandler.FolderDelete"/> with
    ///    <c>Directory.Delete(path, recursive: true)</c>.
    ///
    /// Tested via the public surface — <c>SeedStockKerbalsForAgency</c> itself
    /// is private but is observable through RegisterAgency / LoadAgency. No
    /// handler routing is exercised here — Phase 6.4/6.5 lands that.
    /// </summary>
    [TestClass]
    public class PerAgencyKerbalDiskLayoutTest
    {
        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(),
                "lmp-stage6-disklayout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            // Default to combined-gate-not-required for Phase 6.3 helpers:
            // PerAgencyEnabled (PerAgencyCareer + Career mode) is the actual
            // gate (NOT PerAgencyKerbalRosterEnabled) — disk layout is laid
            // down up-front regardless of the kerbal-roster setting.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            GameplaySettings.SettingsStore.StartingFunds = 25_000f;
            GameplaySettings.SettingsStore.StartingScience = 10f;
            GameplaySettings.SettingsStore.StartingReputation = 5f;

            AgencySystem.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();

            // EnsureStartTechSeeded runs from RegisterAgency / LoadAgencyFromFile
            // alongside our new SeedStockKerbalsForAgency. Pre-seed the shared
            // ResearchAndDevelopment scenario so it doesn't log a "shared
            // scenario absent" Warning during these tests — we're focused on
            // the kerbal disk layout, not the tech seed.
            SeedSharedResearchAndDevelopment();
        }

        [TestCleanup]
        public void Teardown()
        {
            AgencySystem.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();

            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            GameplaySettings.SettingsStore.StartingFunds = 0f;
            GameplaySettings.SettingsStore.StartingScience = 0f;
            GameplaySettings.SettingsStore.StartingReputation = 0f;

            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, recursive: true);
        }

        private static void SeedSharedResearchAndDevelopment()
        {
            var rd = new ConfigNode("") { Name = "ResearchAndDevelopment" };
            rd.CreateValue(new CfgNodeValue<string, string>("sci", "0"));

            var start = new ConfigNode("") { Name = "Tech" };
            start.CreateValue(new CfgNodeValue<string, string>("id", "start"));
            start.CreateValue(new CfgNodeValue<string, string>("state", "Available"));
            start.CreateValue(new CfgNodeValue<string, string>("cost", "0"));
            start.CreateValue(new CfgNodeValue<string, string>("part", "mk1pod"));

            rd.AddNode(start);

            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = rd;
        }

        // ------------------------------------------------------------------
        // 1. Path helper
        // ------------------------------------------------------------------

        [TestMethod]
        public void GetKerbalsPathForAgency_ResolvesUnderUniverseDirectory()
        {
            var agencyId = Guid.NewGuid();
            var resolved = AgencySystem.GetKerbalsPathForAgency(agencyId);

            var expected = Path.Combine(
                ServerContext.UniverseDirectory,
                "Agencies",
                agencyId.ToString("N"),
                "Kerbals");

            Assert.AreEqual(expected, resolved);
        }

        [TestMethod]
        public void GetKerbalsPathForAgency_ReResolvesAfterUniverseDirectoryRewrite()
        {
            // Expression-bodied contract: ServerContext.UniverseDirectory
            // mutations between calls must flow through. ServerTest's per-test
            // temp-dir pattern depends on this — if the property snapshotted
            // its value at first access, every test except the first would
            // write to a stale path.
            var agencyId = Guid.NewGuid();
            var first = AgencySystem.GetKerbalsPathForAgency(agencyId);

            var newUniverse = Path.Combine(Path.GetTempPath(),
                "lmp-stage6-rerresolve-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(newUniverse);
            try
            {
                ServerContext.UniverseDirectory = newUniverse;
                var second = AgencySystem.GetKerbalsPathForAgency(agencyId);

                Assert.AreNotEqual(first, second);
                Assert.IsTrue(second.StartsWith(newUniverse));
            }
            finally
            {
                Directory.Delete(newUniverse, recursive: true);
            }
        }

        // ------------------------------------------------------------------
        // 2. Mint path — RegisterAgency wires SeedStockKerbalsForAgency
        // ------------------------------------------------------------------

        [TestMethod]
        public void RegisterAgency_UnderPerAgencyEnabled_SeedsFourStockKerbalFiles()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");

            Assert.IsNotNull(state);
            var kerbalsDir = AgencySystem.GetKerbalsPathForAgency(state.AgencyId);
            Assert.IsTrue(Directory.Exists(kerbalsDir),
                "RegisterAgency must create the per-agency Kerbals subdir under gate=on.");

            var files = Directory.GetFiles(kerbalsDir, "*.txt").Select(Path.GetFileName).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(
                new[] { "Bill Kerman.txt", "Bob Kerman.txt", "Jebediah Kerman.txt", "Valentina Kerman.txt" },
                files,
                "RegisterAgency must seed exactly the four stock kerbal files.");

            // All four files must have non-zero byte content (resource templates).
            foreach (var name in files)
            {
                var bytes = File.ReadAllBytes(Path.Combine(kerbalsDir, name));
                Assert.IsTrue(bytes.Length > 0, $"Seeded {name} must have non-zero content from the embedded resource.");
            }
        }

        // ------------------------------------------------------------------
        // 3. Idempotency
        // ------------------------------------------------------------------

        [TestMethod]
        public void RegisterAgency_CalledTwice_DoesNotDuplicateOrCorruptFiles()
        {
            // RegisterAgency itself is single-shot per player name (returns the
            // existing agency on the second call). To exercise the seed-twice
            // idempotency, drive through LoadAgency after writing the file
            // out — the second LoadAgency hits the partial-seed-recovery path
            // with all 4 already present, which is a no-op.
            var first = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(first);

            var kerbalsDir = AgencySystem.GetKerbalsPathForAgency(first.AgencyId);
            var initialFiles = Directory.GetFiles(kerbalsDir, "*.txt").OrderBy(p => p).ToArray();
            var initialMTimes = initialFiles.Select(File.GetLastWriteTimeUtc).ToArray();

            // Evict from in-memory registry so LoadAgency re-reads from disk
            // (which triggers LoadAgencyFromFile -> SeedStockKerbalsForAgency
            // backfill path — should be a no-op with all 4 files present).
            AgencySystem.Agencies.TryRemove(first.AgencyId, out _);
            var reloaded = AgencySystem.LoadAgency(first.AgencyId);
            Assert.IsNotNull(reloaded);
            Assert.AreEqual(first.AgencyId, reloaded.AgencyId);

            var reloadedFiles = Directory.GetFiles(kerbalsDir, "*.txt").OrderBy(p => p).ToArray();
            CollectionAssert.AreEqual(initialFiles, reloadedFiles,
                "Idempotent re-seed must produce the same file set (no duplicates, no removals).");

            // mtimes preserved → confirms WriteAtomic was NOT re-invoked for
            // any of the 4 (per-file FileExists check fired).
            for (var i = 0; i < initialFiles.Length; i++)
            {
                Assert.AreEqual(initialMTimes[i], File.GetLastWriteTimeUtc(reloadedFiles[i]),
                    $"{Path.GetFileName(reloadedFiles[i])} mtime changed — idempotency guard missed.");
            }
        }

        // ------------------------------------------------------------------
        // 4. Partial-seed recovery
        // ------------------------------------------------------------------

        [TestMethod]
        public void LoadAgency_AfterOperatorDeletedOneKerbalFile_RestoresMissingFileOnly()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            var kerbalsDir = AgencySystem.GetKerbalsPathForAgency(state.AgencyId);
            var bobPath = Path.Combine(kerbalsDir, "Bob Kerman.txt");

            // Snapshot mtimes of the OTHER 3 files (must be unchanged after
            // recovery — only Bob should be rewritten).
            var jebMtime = File.GetLastWriteTimeUtc(Path.Combine(kerbalsDir, "Jebediah Kerman.txt"));
            var billMtime = File.GetLastWriteTimeUtc(Path.Combine(kerbalsDir, "Bill Kerman.txt"));
            var valMtime = File.GetLastWriteTimeUtc(Path.Combine(kerbalsDir, "Valentina Kerman.txt"));

            File.Delete(bobPath);
            Assert.IsFalse(File.Exists(bobPath));

            // Evict + LoadAgency triggers the LoadAgencyFromFile backfill path.
            AgencySystem.Agencies.TryRemove(state.AgencyId, out _);
            var reloaded = AgencySystem.LoadAgency(state.AgencyId);
            Assert.IsNotNull(reloaded);

            Assert.IsTrue(File.Exists(bobPath), "Partial-seed recovery must restore the missing Bob file.");
            Assert.IsTrue(new FileInfo(bobPath).Length > 0, "Restored Bob file must have non-zero content.");

            // The other three must NOT have been rewritten (per-file FileExists skip).
            Assert.AreEqual(jebMtime, File.GetLastWriteTimeUtc(Path.Combine(kerbalsDir, "Jebediah Kerman.txt")));
            Assert.AreEqual(billMtime, File.GetLastWriteTimeUtc(Path.Combine(kerbalsDir, "Bill Kerman.txt")));
            Assert.AreEqual(valMtime, File.GetLastWriteTimeUtc(Path.Combine(kerbalsDir, "Valentina Kerman.txt")));
        }

        // ------------------------------------------------------------------
        // 5. Gate=off — no-op
        // ------------------------------------------------------------------

        [TestMethod]
        public void RegisterAgency_UnderPerAgencyCareerOff_DoesNotCreateKerbalsSubdir()
        {
            // PerAgencyEnabled false → RegisterAgency returns null AND no
            // disk layout is written. Phase 6.3 helper inherits the same gate.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;

            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNull(state, "RegisterAgency must return null when the career gate is off.");

            // The AgenciesPath might be created by other paths; just confirm
            // no per-agency Kerbals subdir was ever created under it.
            var anySubdirs = Directory.Exists(AgencyState.AgenciesPath)
                ? Directory.GetDirectories(AgencyState.AgenciesPath)
                : Array.Empty<string>();
            Assert.AreEqual(0, anySubdirs.Length,
                "No per-agency Kerbals subdir may exist under gate=off (RegisterAgency early-returned).");
        }

        [TestMethod]
        public void RegisterAgency_UnderSandboxGameMode_DoesNotCreateKerbalsSubdir()
        {
            // PerAgencyCareer=true but GameMode=Sandbox -> PerAgencyEnabled is
            // false (Career-only product decision, spec §10 Q-Mode). Same
            // observable result as the gate-off case.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNull(state, "RegisterAgency must return null in Sandbox mode (Career-only).");

            var anySubdirs = Directory.Exists(AgencyState.AgenciesPath)
                ? Directory.GetDirectories(AgencyState.AgenciesPath)
                : Array.Empty<string>();
            Assert.AreEqual(0, anySubdirs.Length,
                "No per-agency Kerbals subdir may exist in Sandbox mode.");
        }

        // ------------------------------------------------------------------
        // 6. Load-backfill — pre-Phase-6.3 agency
        // ------------------------------------------------------------------

        [TestMethod]
        public void LoadAgency_BackfillsKerbalsForPreStage6AgencyFile()
        {
            // Simulate a pre-Phase-6.3 agency on disk: the .txt agency state
            // file exists but the {guid:N}/ folder + Kerbals subdir do not.
            var agencyId = Guid.NewGuid();
            var preExistingState = new AgencyState
            {
                AgencyId = agencyId,
                OwningPlayerName = "PreStage6Player",
                DisplayName = "Pre-Phase-6.3 Agency",
                Funds = 1000f,
                Science = 5f,
                Reputation = 2f,
            };
            var filePath = Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N") + ".txt");
            File.WriteAllText(filePath, preExistingState.Serialize());

            var kerbalsDir = AgencySystem.GetKerbalsPathForAgency(agencyId);
            Assert.IsFalse(Directory.Exists(kerbalsDir),
                "Test premise: pre-Phase-6.3 agency has no Kerbals subdir on disk.");

            // LoadAgency -> LoadAgencyFromFile -> SeedStockKerbalsForAgency
            // backfills the missing subdir + 4 files.
            var loaded = AgencySystem.LoadAgency(agencyId);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(agencyId, loaded.AgencyId);

            Assert.IsTrue(Directory.Exists(kerbalsDir),
                "LoadAgency backfill must create the Kerbals subdir for pre-Phase-6.3 agency files.");
            var files = Directory.GetFiles(kerbalsDir, "*.txt");
            Assert.AreEqual(4, files.Length,
                "Backfill must seed all 4 stock kerbals.");
        }

        // ------------------------------------------------------------------
        // 7. Delete cascade — happy path
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryDeleteAgency_RemovesKerbalsSubdirAndEmptyParentFolder()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            var kerbalsDir = AgencySystem.GetKerbalsPathForAgency(state.AgencyId);
            var parentDir = Path.Combine(AgencyState.AgenciesPath, state.AgencyId.ToString("N"));
            var canonicalPath = state.FilePath;

            Assert.IsTrue(Directory.Exists(kerbalsDir));
            Assert.IsTrue(Directory.Exists(parentDir));
            Assert.IsTrue(File.Exists(canonicalPath));

            var deleted = AgencySystem.TryDeleteAgency(state, out var demotedVesselIds, out var failureReason);

            Assert.IsTrue(deleted, $"TryDeleteAgency failed: {failureReason}");
            Assert.IsFalse(Directory.Exists(kerbalsDir),
                "Cascade must remove the Kerbals subdir.");
            Assert.IsFalse(Directory.Exists(parentDir),
                "Empty parent {guid:N}/ folder must also be removed.");
            Assert.IsFalse(File.Exists(canonicalPath),
                "Existing canonical-file unlink behaviour preserved.");
        }

        // ------------------------------------------------------------------
        // 8. Delete cascade — no-throw when subdir absent
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryDeleteAgency_DoesNotThrowWhenKerbalsSubdirAbsent()
        {
            // Simulate a pre-Phase-6.3 agency with no Kerbals subdir.
            // Equivalent to the operator manually deleting the subdir.
            var agencyId = Guid.NewGuid();
            var state = new AgencyState
            {
                AgencyId = agencyId,
                OwningPlayerName = "PreStage6Player",
                DisplayName = "Pre-Phase-6.3 Agency",
            };
            var filePath = Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N") + ".txt");
            File.WriteAllText(filePath, state.Serialize());
            AgencySystem.Agencies.TryAdd(agencyId, state);
            AgencySystem.AgencyByPlayerName[state.OwningPlayerName] = agencyId;

            // No Kerbals subdir on disk - the FolderExists guard inside the
            // cascade block must protect against the otherwise-thrown
            // DirectoryNotFoundException.
            Assert.IsFalse(Directory.Exists(AgencySystem.GetKerbalsPathForAgency(agencyId)));

            var deleted = AgencySystem.TryDeleteAgency(state, out _, out var failureReason);
            Assert.IsTrue(deleted, $"TryDeleteAgency failed: {failureReason}");
            Assert.IsFalse(File.Exists(filePath));
        }

        // ------------------------------------------------------------------
        // 9. Resource template integrity — bytes parse as a kerbal ConfigNode
        // ------------------------------------------------------------------

        [TestMethod]
        public void SeededJebFile_ParsesAsConfigNodeWithMatchingKerbalName()
        {
            // Guards against accidental corruption of the embedded
            // Resources.*_Kerman templates — round-trip through LunaConfigNode
            // must produce a node whose "name" value equals the filename stem.
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            var jebPath = Path.Combine(
                AgencySystem.GetKerbalsPathForAgency(state.AgencyId),
                "Jebediah Kerman.txt");
            Assert.IsTrue(File.Exists(jebPath));

            var content = File.ReadAllText(jebPath);
            Assert.IsTrue(content.Length > 0);

            // The stock kerbal templates ship in ConfigNode `key = value` form
            // (no outer wrapper). The "name" field must round-trip to the
            // expected literal "Jebediah Kerman".
            Assert.IsTrue(content.Contains("name = Jebediah Kerman"),
                "Seeded Jeb file must contain a 'name = Jebediah Kerman' line — template integrity check.");
        }
    }
}
