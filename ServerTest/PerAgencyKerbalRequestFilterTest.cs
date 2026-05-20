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
using System.Reflection;

namespace ServerTest
{
    /// <summary>
    /// Stage 6 Phase 6.4 — pins <see cref="KerbalSystem.ResolveKerbalsPathForRequester"/>
    /// across every gate-and-state branch the spec calls out (§3 table + §Q-Migration).
    /// The helper is internal; reached here via reflection.
    ///
    /// Six branches covered:
    /// <list type="number">
    ///   <item>Combined gate off (<c>PerAgencyKerbalRoster=false</c>) → legacy path.</item>
    ///   <item>PerAgencyCareer off (combined-gate precondition fails) → legacy path.</item>
    ///   <item>GameMode=Sandbox (Career-only product decision per spec §10 Q-Mode) → legacy.</item>
    ///   <item>Combined gate on AND registered agency → per-agency subdir.</item>
    ///   <item>Combined gate on AND missing <c>AgencyByPlayerName</c> mapping → legacy + Warning
    ///         (defensive fallback per spec §3).</item>
    ///   <item>Combined gate on AND mapping exists AND subdir absent (operator hand-delete) →
    ///         legacy + Warning.</item>
    /// </list>
    ///
    /// <para>No <see cref="MessageQueuer.SendToClient"/> exercised here — that's
    /// the MockClientTest seam. This file pins the pure path-resolution logic.</para>
    /// </summary>
    [TestClass]
    public class PerAgencyKerbalRequestFilterTest
    {
        private static readonly MethodInfo ResolveMethod = typeof(KerbalSystem)
            .GetMethod("ResolveKerbalsPathForRequester",
                BindingFlags.Static | BindingFlags.NonPublic);

        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(),
                "lmp-stage6-reqfilter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            // Career-mode default; tests flip the kerbal-roster gate explicitly.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            GameplaySettings.SettingsStore.StartingFunds = 25_000f;
            GameplaySettings.SettingsStore.StartingScience = 10f;
            GameplaySettings.SettingsStore.StartingReputation = 5f;

            AgencySystem.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();

            // Pre-seed the shared R&D scenario so Phase 6.3's EnsureStartTechSeeded
            // doesn't log a Warning during RegisterAgency — unrelated to this test
            // surface but would clutter the test log otherwise.
            SeedSharedResearchAndDevelopment();
        }

        [TestCleanup]
        public void Teardown()
        {
            AgencySystem.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();

            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;
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

        private static string Resolve(string playerName)
        {
            return (string)ResolveMethod.Invoke(null, new object[] { playerName });
        }

        // ------------------------------------------------------------------
        // 1. Combined gate off — PerAgencyKerbalRoster=false
        // ------------------------------------------------------------------

        [TestMethod]
        public void Resolve_PerAgencyKerbalRosterFlagOff_ReturnsLegacyPath()
        {
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;
            // PerAgencyCareer + Career mode left on — combined gate is FALSE because
            // only one of the two pieces is set. Dual-mode silence.
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state, "Setup precondition: RegisterAgency must succeed under PerAgencyCareer=true.");

            var resolved = Resolve("Majestic95");
            Assert.AreEqual(KerbalSystem.KerbalsPath, resolved,
                "PerAgencyKerbalRoster=false must route every request to the legacy shared Universe/Kerbals/ path, " +
                "regardless of agency-registry state.");
        }

        // ------------------------------------------------------------------
        // 2. PerAgencyCareer off — combined-gate precondition fails
        // ------------------------------------------------------------------

        [TestMethod]
        public void Resolve_PerAgencyCareerOff_ReturnsLegacyPath()
        {
            // Disable the career-side gate while leaving kerbal-roster set. The
            // combined predicate composes via AND of PerAgencyEnabled (which itself
            // requires PerAgencyCareer && Career) — so this branch must be legacy.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;

            var resolved = Resolve("Majestic95");
            Assert.AreEqual(KerbalSystem.KerbalsPath, resolved,
                "PerAgencyCareer=false implies PerAgencyKerbalRosterEnabled=false (precondition).");
        }

        // ------------------------------------------------------------------
        // 3. GameMode=Sandbox — Career-only product decision
        // ------------------------------------------------------------------

        [TestMethod]
        public void Resolve_GameModeSandboxWithBothFlagsOn_ReturnsLegacyPath()
        {
            // Spec §10 Q-Mode signed off: per-agency career (and therefore per-agency
            // kerbal roster) is Career-only. AgencySystem.PerAgencyEnabled returns
            // false under Sandbox even with PerAgencyCareer=true.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            var resolved = Resolve("Majestic95");
            Assert.AreEqual(KerbalSystem.KerbalsPath, resolved,
                "Sandbox mode must bypass per-agency routing even with both kerbal gates on.");
        }

        // ------------------------------------------------------------------
        // 4. Combined gate on + registered agency — happy path
        // ------------------------------------------------------------------

        [TestMethod]
        public void Resolve_CombinedGateOn_RegisteredAgency_ReturnsPerAgencySubdir()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            var expected = AgencySystem.GetKerbalsPathForAgency(state.AgencyId);
            // Phase 6.3 lifecycle hook should have created the subdir at mint time.
            Assert.IsTrue(Directory.Exists(expected),
                "Setup precondition: Phase 6.3 RegisterAgency must seed the Kerbals subdir.");

            var resolved = Resolve("Majestic95");
            Assert.AreEqual(expected, resolved,
                "Combined gate on + registered agency must route to the per-agency subdir.");
            Assert.AreNotEqual(KerbalSystem.KerbalsPath, resolved,
                "Resolved path must not be the legacy shared dir under the happy path.");
        }

        // ------------------------------------------------------------------
        // 5. Combined gate on + missing AgencyByPlayerName mapping — fallback
        // ------------------------------------------------------------------

        [TestMethod]
        public void Resolve_CombinedGateOn_MissingAgencyMapping_FallsBackToLegacy()
        {
            // Defensive fallback per spec §3. The healthy server's handshake order
            // makes this unreachable (RegisterAgency inserts the index entry before
            // HandshakeReply ships) but a torn registry state must not deliver an
            // empty roster.
            Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("ghost-player"),
                "Setup precondition: ghost-player has no agency mapping.");

            var resolved = Resolve("ghost-player");
            Assert.AreEqual(KerbalSystem.KerbalsPath, resolved,
                "Missing AgencyByPlayerName mapping must fall back to legacy path (spec §3).");
        }

        // ------------------------------------------------------------------
        // 6. Combined gate on + mapping present + subdir absent — fallback
        // ------------------------------------------------------------------

        [TestMethod]
        public void Resolve_CombinedGateOn_MappingPresentButSubdirAbsent_FallsBackToLegacy()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            // Simulate operator hand-delete of the Kerbals subdir between sessions.
            // Phase 6.3 backfills on LoadAgencyFromFile but the runtime path here
            // never re-loads from disk — the AgencyByPlayerName entry stays valid
            // but the disk subdir is gone.
            var subdir = AgencySystem.GetKerbalsPathForAgency(state.AgencyId);
            Directory.Delete(subdir, recursive: true);
            Assert.IsFalse(Directory.Exists(subdir),
                "Setup precondition: agency subdir must be gone before resolve.");

            var resolved = Resolve("Majestic95");
            Assert.AreEqual(KerbalSystem.KerbalsPath, resolved,
                "Mapping present + missing subdir must fall back to legacy (bricked-install limp).");
        }

        // ------------------------------------------------------------------
        // 7. Idempotent across mid-session UniverseDirectory rewrite
        // ------------------------------------------------------------------

        [TestMethod]
        public void Resolve_ReResolvesAfterUniverseDirectoryRewrite()
        {
            // The helper composes KerbalsPath (expression-bodied) +
            // GetKerbalsPathForAgency (expression-bodied) — both must flow
            // ServerContext.UniverseDirectory changes through. Validate
            // explicitly so a future refactor that snapshots either into a
            // static-readonly field gets caught.
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            var first = Resolve("Majestic95");

            var newUniverse = Path.Combine(Path.GetTempPath(),
                "lmp-stage6-reqfilter-rere-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(newUniverse);
            try
            {
                ServerContext.UniverseDirectory = newUniverse;
                // No agency-files exist in the new universe; the agency mapping
                // still points at the OLD subdir which doesn't exist under the
                // new root — so the helper falls back to the new universe's
                // (also non-existent but conceptually correct) Universe/Kerbals/.
                var second = Resolve("Majestic95");
                Assert.AreNotEqual(first, second,
                    "Helper must re-resolve UniverseDirectory mid-session.");
                Assert.IsTrue(second.StartsWith(newUniverse),
                    "Re-resolved path must live under the new UniverseDirectory.");
            }
            finally
            {
                Directory.Delete(newUniverse, recursive: true);
            }
        }
    }
}
