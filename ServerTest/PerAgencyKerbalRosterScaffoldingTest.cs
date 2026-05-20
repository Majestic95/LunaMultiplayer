using LmpCommon.Enums;
using LmpCommon.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.Settings.Definition;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Scenario;
using System;
using System.IO;

namespace ServerTest
{
    /// <summary>
    /// Stage 6 Phase 6.2 scaffolding tests. Pins:
    ///   1. The two new GameplaySettings properties round-trip through XML
    ///      cleanly (default false; non-default values preserved).
    ///   2. The combined gate <see cref="AgencySystem.PerAgencyKerbalRosterEnabled"/>
    ///      composes correctly across PerAgencyCareer, GameMode=Career, and
    ///      PerAgencyKerbalRoster — all three must be on.
    ///   3. The new <c>RefuseStartupIfKerbalHazardWithoutOverride</c> fires on
    ///      legacy <c>Universe/Kerbals/</c> contents and is bypassed by the
    ///      independent override flag.
    ///
    /// Tested through the public <see cref="AgencySystem.LoadExistingAgencies"/>
    /// entry point — mirrors the established pattern from
    /// <see cref="AgencySystemTest"/>'s boot-refusal coverage (lines 540-599).
    /// </summary>
    [TestClass]
    public class PerAgencyKerbalRosterScaffoldingTest
    {
        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(),
                "lmp-stage6-scaffold-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            // Default state: gates off, server running. Each test opts in as needed.
            ServerContext.ServerRunning = true;
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse = false;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyKerbalsOnExistingUniverse = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            AgencySystem.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
            VesselStoreSystem.CurrentVessels.Clear();
        }

        [TestCleanup]
        public void Teardown()
        {
            AgencySystem.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
            VesselStoreSystem.CurrentVessels.Clear();

            ServerContext.ServerRunning = false;
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse = false;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyKerbalsOnExistingUniverse = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, recursive: true);
        }

        // ---------- Settings round-trip ----------

        [TestMethod]
        public void Settings_RoundTrip_PreservesPerAgencyKerbalRoster()
        {
            // Operator hand-writes the two new settings to gameplaysettings.xml.
            // Load + Save through LunaXmlSerializer (matches the SettingsBase.Load
            // -> Save flow on BUG-039) must preserve both values bit-for-bit.
            var path = Path.Combine(ServerContext.UniverseDirectory, $"stage6_round_trip_{Guid.NewGuid():N}.xml");
            try
            {
                var initial =
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                    "<GameplaySettingsDefinition>\n" +
                    "  <PerAgencyKerbalRoster>true</PerAgencyKerbalRoster>\n" +
                    "  <AllowEnablePerAgencyKerbalsOnExistingUniverse>true</AllowEnablePerAgencyKerbalsOnExistingUniverse>\n" +
                    "</GameplaySettingsDefinition>\n";
                File.WriteAllText(path, initial);

                var loaded = LunaXmlSerializer.ReadXmlFromPath<GameplaySettingsDefinition>(path);
                Assert.IsNotNull(loaded);
                Assert.IsTrue(loaded.PerAgencyKerbalRoster,
                    "PerAgencyKerbalRoster must deserialise from operator-written XML.");
                Assert.IsTrue(loaded.AllowEnablePerAgencyKerbalsOnExistingUniverse,
                    "AllowEnablePerAgencyKerbalsOnExistingUniverse must deserialise from operator-written XML.");

                // Server boots, rewrites the file. Both flags must survive.
                LunaXmlSerializer.WriteToXmlFile(loaded, path);
                var rewritten = File.ReadAllText(path);
                Assert.IsTrue(rewritten.Contains("<PerAgencyKerbalRoster>true</PerAgencyKerbalRoster>"),
                    $"PerAgencyKerbalRoster must round-trip through save. Got: {rewritten}");
                Assert.IsTrue(rewritten.Contains("<AllowEnablePerAgencyKerbalsOnExistingUniverse>true</AllowEnablePerAgencyKerbalsOnExistingUniverse>"),
                    $"AllowEnablePerAgencyKerbalsOnExistingUniverse must round-trip through save. Got: {rewritten}");

                // Re-reading still produces the same values.
                var reloaded = LunaXmlSerializer.ReadXmlFromPath<GameplaySettingsDefinition>(path);
                Assert.IsTrue(reloaded.PerAgencyKerbalRoster);
                Assert.IsTrue(reloaded.AllowEnablePerAgencyKerbalsOnExistingUniverse);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void Settings_Defaults_ArePinnedFalse()
        {
            // Default-constructed GameplaySettingsDefinition has both new fields false.
            // Matches the PerAgencyCareer / AllowEnablePerAgencyOnExistingUniverse precedent;
            // operators must opt in explicitly.
            var fresh = new GameplaySettingsDefinition();
            Assert.IsFalse(fresh.PerAgencyKerbalRoster);
            Assert.IsFalse(fresh.AllowEnablePerAgencyKerbalsOnExistingUniverse);
        }

        [TestMethod]
        public void Settings_DifficultyPresets_LeaveBothFalse()
        {
            // All four presets explicitly set both flags to false. Without this, an
            // operator hitting a difficulty preset after enabling per-agency-kerbal
            // would silently reset their kerbal-mode opt-in.
            var def = new GameplaySettingsDefinition
            {
                PerAgencyKerbalRoster = true,
                AllowEnablePerAgencyKerbalsOnExistingUniverse = true,
            };
            def.SetEasy();
            Assert.IsFalse(def.PerAgencyKerbalRoster, "SetEasy must reset PerAgencyKerbalRoster to false.");
            Assert.IsFalse(def.AllowEnablePerAgencyKerbalsOnExistingUniverse,
                "SetEasy must reset AllowEnablePerAgencyKerbalsOnExistingUniverse to false.");

            def.PerAgencyKerbalRoster = true;
            def.AllowEnablePerAgencyKerbalsOnExistingUniverse = true;
            def.SetNormal();
            Assert.IsFalse(def.PerAgencyKerbalRoster);
            Assert.IsFalse(def.AllowEnablePerAgencyKerbalsOnExistingUniverse);

            def.PerAgencyKerbalRoster = true;
            def.AllowEnablePerAgencyKerbalsOnExistingUniverse = true;
            def.SetModerate();
            Assert.IsFalse(def.PerAgencyKerbalRoster);
            Assert.IsFalse(def.AllowEnablePerAgencyKerbalsOnExistingUniverse);

            def.PerAgencyKerbalRoster = true;
            def.AllowEnablePerAgencyKerbalsOnExistingUniverse = true;
            def.SetHard();
            Assert.IsFalse(def.PerAgencyKerbalRoster);
            Assert.IsFalse(def.AllowEnablePerAgencyKerbalsOnExistingUniverse);
        }

        // ---------- Combined gate predicate ----------

        [TestMethod]
        public void PerAgencyKerbalRosterEnabled_RequiresPerAgencyCareer()
        {
            // PerAgencyKerbalRoster alone is not enough — the kerbal partition
            // depends on AgencyByPlayerName which is only populated by the
            // per-agency career runtime.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            Assert.IsFalse(AgencySystem.PerAgencyKerbalRosterEnabled,
                "Combined gate must return false when PerAgencyCareer is off, regardless of PerAgencyKerbalRoster.");
        }

        [TestMethod]
        public void PerAgencyKerbalRosterEnabled_RequiresCareerGameMode()
        {
            // Sandbox / Science: combined gate must inherit the Career-mode-only
            // constraint from PerAgencyEnabled.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            Assert.IsFalse(AgencySystem.PerAgencyKerbalRosterEnabled,
                "Combined gate must return false under non-Career GameMode (Sandbox).");

            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            Assert.IsFalse(AgencySystem.PerAgencyKerbalRosterEnabled,
                "Combined gate must return false under non-Career GameMode (Science).");
        }

        [TestMethod]
        public void PerAgencyKerbalRosterEnabled_AllGatesOn_ReturnsTrue()
        {
            // Full happy path: all three gates on → predicate fires true. This is
            // the only configuration under which Phase 6.4/6.5 handlers will route.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            Assert.IsTrue(AgencySystem.PerAgencyKerbalRosterEnabled,
                "Combined gate must return true when all three preconditions hold (PerAgencyCareer + PerAgencyKerbalRoster + Career mode).");
        }

        // ---------- Boot-refusal ----------

        [TestMethod]
        public void LoadExistingAgencies_RefusesBoot_OnLegacyKerbalsWithoutOverride()
        {
            // The kerbal-roster boot-refusal fires on a directly observable disk
            // condition (legacy Universe/Kerbals/ has *.txt) AND the gate flip,
            // independent of the career-side hazard family. Even with zero
            // vessels / zero accumulated scenario state, the kerbal refusal
            // fires because the disk has legacy data the projector would freeze.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyKerbalsOnExistingUniverse = false;
            // Pristine universe on the career side — no vessels, no scenarios.
            // This isolates the kerbal hazard from the career-side one.
            SeedLegacyKerbalFile("Jebediah Kerman.txt");
            SeedLegacyKerbalFile("Valentina Kerman.txt");

            AgencySystem.LoadExistingAgencies();

            Assert.IsFalse(ServerContext.ServerRunning,
                "RefuseStartupIfKerbalHazardWithoutOverride must flip ServerRunning=false when legacy " +
                "Universe/Kerbals/ has files and PerAgencyKerbalRoster=true without operator override.");
        }

        [TestMethod]
        public void LoadExistingAgencies_RefusesBoot_OnLegacyKerbals_WhenOverrideOn_UntilPhase65Ships()
        {
            // [Phase 6.4 amendment] Operator opt-in via AllowEnablePerAgencyKerbalsOnExistingUniverse=true
            // short-circuits the Phase 6.2 RefuseStartupIfKerbalHazardWithoutOverride
            // predicate — but Phase 6.4 introduces a TEMPORARY second refusal,
            // RefuseStartupIfKerbalWriteRoutingNotYetShipped, that fires
            // unconditionally under PerAgencyKerbalRoster=true regardless of the
            // hazard override. The temporary refusal blocks the half-shipped
            // state where reads route per-agency but writes still pool to
            // Universe/Kerbals/. **When Phase 6.5 ships and removes the temporary
            // refusal, flip this assertion back to IsTrue + restore the original
            // test name.**
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyKerbalsOnExistingUniverse = true;
            SeedLegacyKerbalFile("Jebediah Kerman.txt");

            AgencySystem.LoadExistingAgencies();

            Assert.IsFalse(ServerContext.ServerRunning,
                "Phase 6.4 RefuseStartupIfKerbalWriteRoutingNotYetShipped must fire under " +
                "PerAgencyKerbalRoster=true regardless of override — write-path not yet shipped.");
        }

        [TestMethod]
        public void LoadExistingAgencies_AllowsBoot_OnLegacyKerbals_WhenGateOff()
        {
            // PerAgencyKerbalRoster=false → kerbal refusal short-circuits at the
            // gate check before scanning the directory. Vanilla v7 behaviour:
            // shared-roster mode handles the legacy directory normally.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;
            SeedLegacyKerbalFile("Jebediah Kerman.txt");

            AgencySystem.LoadExistingAgencies();

            Assert.IsTrue(ServerContext.ServerRunning,
                "Kerbal refusal must NOT fire when PerAgencyKerbalRoster=false (dual-mode silence).");
        }

        [TestMethod]
        public void LoadExistingAgencies_RefusesBoot_WhenLegacyKerbalsDirIsEmpty_UntilPhase65Ships()
        {
            // [Phase 6.4 amendment] Empty legacy dir would normally short-circuit
            // the Phase 6.2 hazard refusal (no data to freeze), but the Phase 6.4
            // temporary RefuseStartupIfKerbalWriteRoutingNotYetShipped fires on
            // PerAgencyKerbalRoster=true regardless of legacy state — blocking the
            // half-shipped read-only mode that would silently diverge on fresh-mint
            // universes too (KerbalProto writes still pool to Universe/Kerbals/).
            // **Flip back to IsTrue + restore the test name when Phase 6.5 ships.**
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyKerbalsOnExistingUniverse = false;
            // Create the dir but no files in it.
            Directory.CreateDirectory(KerbalSystem.KerbalsPath);

            AgencySystem.LoadExistingAgencies();

            Assert.IsFalse(ServerContext.ServerRunning,
                "Phase 6.4 RefuseStartupIfKerbalWriteRoutingNotYetShipped must fire on PerAgencyKerbalRoster=true " +
                "even with an empty legacy dir — write-path not yet shipped.");
        }

        [TestMethod]
        public void LoadExistingAgencies_KerbalRefusal_Independent_Of_CareerOverride()
        {
            // Critical orthogonality property: the kerbal override is INDEPENDENT
            // of the career override. An operator who has accepted career
            // migration (AllowEnablePerAgencyOnExistingUniverse=true) but NOT
            // kerbal migration must still hit the kerbal refusal.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse = true;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyKerbalsOnExistingUniverse = false;
            SeedLegacyKerbalFile("Jebediah Kerman.txt");

            AgencySystem.LoadExistingAgencies();

            Assert.IsFalse(ServerContext.ServerRunning,
                "Career override must NOT bypass the kerbal refusal — overrides are orthogonal " +
                "operator decisions per spec §Q-Migration.");
        }

        // ---------- Phase 6.4 temporary refusal ----------

        [TestMethod]
        public void LoadExistingAgencies_RefusesBoot_OnPerAgencyKerbalRosterTrue_RegardlessOfHazardOverride()
        {
            // [Phase 6.4] The temporary RefuseStartupIfKerbalWriteRoutingNotYetShipped
            // predicate fires on PerAgencyKerbalRoster=true REGARDLESS of:
            //   - legacy Kerbals dir state (empty / populated / absent)
            //   - AllowEnablePerAgencyKerbalsOnExistingUniverse override
            //   - AllowEnablePerAgencyOnExistingUniverse override
            //   - GameMode (Career / Sandbox / Science)
            //   - Agencies count
            //
            // The refusal exists because Phase 6.4 only ships read-path per-agency
            // routing; KerbalProto + KerbalRemove writes still pool to legacy.
            // Enabling the gate would cause silent state divergence on EVERY
            // universe shape, not just upgraded ones. **Phase 6.5 removes this
            // predicate; this test must be deleted in the Phase 6.5 commit.**
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            // Maximally-permissive override set: every operator opt-in active.
            GameplaySettings.SettingsStore.AllowEnablePerAgencyKerbalsOnExistingUniverse = true;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse = true;
            // Fresh universe — no legacy Kerbals dir, no agencies on disk.
            Assert.IsFalse(Directory.Exists(KerbalSystem.KerbalsPath),
                "Test precondition: legacy Kerbals dir should not exist before LoadExistingAgencies.");

            AgencySystem.LoadExistingAgencies();

            Assert.IsFalse(ServerContext.ServerRunning,
                "Phase 6.4 RefuseStartupIfKerbalWriteRoutingNotYetShipped must refuse boot even with " +
                "every override on + a pristine universe — write-path not yet shipped.");
        }

        // ---------- Helpers ----------

        private static void SeedLegacyKerbalFile(string fileName)
        {
            var dir = KerbalSystem.KerbalsPath;
            Directory.CreateDirectory(dir);
            // Minimal valid kerbal ConfigNode shape. Content not parsed by the
            // refusal predicate — it only counts *.txt files.
            var stub =
                "KERBAL\n" +
                "{\n" +
                "    name = " + Path.GetFileNameWithoutExtension(fileName) + "\n" +
                "    state = Available\n" +
                "    ToD = 0\n" +
                "}\n";
            File.WriteAllText(Path.Combine(dir, fileName), stub);
        }
    }
}
