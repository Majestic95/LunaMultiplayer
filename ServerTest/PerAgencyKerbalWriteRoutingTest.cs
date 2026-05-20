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
using System.Text;

namespace ServerTest
{
    /// <summary>
    /// Stage 6 Phase 6.5 — pins the per-agency write path for KerbalProto +
    /// KerbalRemove via the directly-testable pure helpers
    /// <see cref="KerbalSystem.TryWriteKerbalProtoPerAgency"/> and
    /// <see cref="KerbalSystem.TryDeleteKerbalPerAgency"/>. The full
    /// <see cref="KerbalSystem.HandleKerbalProto"/> /
    /// <see cref="KerbalSystem.HandleKerbalRemove"/> handlers (which require a
    /// live <see cref="Server.Client.ClientStructure"/> for the relay step) are
    /// covered end-to-end in MockClientTest's <c>PerAgencyKerbalWriteRoutingE2eTest</c>.
    ///
    /// Branches covered:
    /// <list type="number">
    ///   <item>Happy path: gate on + registered agency → write lands in agency subdir.</item>
    ///   <item>Mapping miss (torn registry): no AgencyByPlayerName entry → DROP, no file
    ///         written anywhere.</item>
    ///   <item>Cascade race: AgencyByPlayerName has the mapping but Agencies doesn't (the
    ///         narrow window <see cref="AgencySystem.TryDeleteAgency"/> opens between its
    ///         <c>Agencies.TryRemove</c> and <c>AgencyByPlayerName.TryRemove</c>) → DROP
    ///         under the lock, no file written.</item>
    ///   <item>Delete happy path: per-agency file present → deleted.</item>
    ///   <item>Delete on missing file: idempotent no-op via FileHandler.FileDelete.</item>
    ///   <item>Delete cascade race: same shape as proto, DROP.</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class PerAgencyKerbalWriteRoutingTest
    {
        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(),
                "lmp-stage6-write-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            GameplaySettings.SettingsStore.StartingFunds = 25_000f;
            GameplaySettings.SettingsStore.StartingScience = 10f;
            GameplaySettings.SettingsStore.StartingReputation = 5f;

            AgencySystem.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
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

        private static byte[] BytesForKerbal(string name)
        {
            // Minimal valid kerbal ConfigNode body. KerbalSystem doesn't parse
            // the bytes — it just writes them verbatim. Content shape mirrors
            // Resources.Jebediah_Kerman.txt so any future parser hitting these
            // doesn't trip.
            return Encoding.UTF8.GetBytes($"name = {name}\nstate = Available\nToD = 0\n");
        }

        // ------------------------------------------------------------------
        // 1. Proto happy path
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryWriteKerbalProtoPerAgency_HappyPath_WritesToAgencySubdir()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            var bytes = BytesForKerbal("Aurora Test-Kerman");
            var ok = KerbalSystem.TryWriteKerbalProtoPerAgency(
                "Majestic95", "Aurora Test-Kerman", bytes, bytes.Length, out var senderAgencyId);

            Assert.IsTrue(ok, "Happy-path write must return true so caller relays.");
            Assert.AreEqual(state.AgencyId, senderAgencyId);

            var expectedPath = Path.Combine(
                AgencySystem.GetKerbalsPathForAgency(state.AgencyId),
                "Aurora Test-Kerman.txt");
            Assert.IsTrue(File.Exists(expectedPath),
                "Kerbal file must land in the agency's per-agency subdir.");
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(expectedPath),
                "Disk bytes must equal the payload bytes (numBytes-exact write).");

            // Legacy path must remain untouched — gate-on writes never land there.
            var legacyPath = Path.Combine(KerbalSystem.KerbalsPath, "Aurora Test-Kerman.txt");
            Assert.IsFalse(File.Exists(legacyPath),
                "Per-agency write must NOT also write to the legacy shared path.");
        }

        // ------------------------------------------------------------------
        // 2. Proto: no AgencyByPlayerName mapping → DROP
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryWriteKerbalProtoPerAgency_NoAgencyMapping_DropsWithoutWritingAnywhere()
        {
            // Don't register an agency for this player. AgencyByPlayerName miss.
            Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("ghost-player"));

            var bytes = BytesForKerbal("Ghost Kerman");
            var ok = KerbalSystem.TryWriteKerbalProtoPerAgency(
                "ghost-player", "Ghost Kerman", bytes, bytes.Length, out var senderAgencyId);

            Assert.IsFalse(ok, "Mapping miss must return false so caller skips relay.");
            Assert.AreEqual(Guid.Empty, senderAgencyId,
                "On mapping miss, out param must be Guid.Empty (caller cannot resolve a relay scope).");

            // No file should exist in legacy NOR in any agency subdir — the DROP
            // is the gate=on-equivalent of /dev/null (write is dropped audibly,
            // not silently rerouted to legacy where it would be unreadable).
            var legacyPath = Path.Combine(KerbalSystem.KerbalsPath, "Ghost Kerman.txt");
            Assert.IsFalse(File.Exists(legacyPath),
                "DROP must NOT fall back to writing legacy under gate=on.");
            // Even if some subdir existed, no agency was registered so there's
            // nothing to check beyond legacy.
        }

        // ------------------------------------------------------------------
        // 3. Proto: cascade race (Agencies removed but AgencyByPlayerName still has stale entry)
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryWriteKerbalProtoPerAgency_CascadeRace_AgencyEvictedFromAgencies_DropsWithoutWriting()
        {
            // Simulate the window in TryDeleteAgency between Agencies.TryRemove
            // (line 2211) and AgencyByPlayerName.TryRemove (line 2213). The
            // sender's mapping points at an agency-id that's no longer in the
            // Agencies dictionary. The helper must acquire the lock + re-check
            // ContainsKey + DROP rather than create a file in the orphan subdir.
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            // Simulate the cascade racing our write: remove from Agencies but
            // leave AgencyByPlayerName intact. (In production this window is
            // microseconds wide and protected by the lock; we exploit the
            // ConcurrentDictionary's direct mutability to reach the state.)
            Assert.IsTrue(AgencySystem.Agencies.TryRemove(state.AgencyId, out _));
            Assert.IsTrue(AgencySystem.AgencyByPlayerName.ContainsKey("Majestic95"),
                "Test precondition: AgencyByPlayerName still has stale mapping.");

            var bytes = BytesForKerbal("Aurora Test-Kerman");
            var ok = KerbalSystem.TryWriteKerbalProtoPerAgency(
                "Majestic95", "Aurora Test-Kerman", bytes, bytes.Length, out var senderAgencyId);

            Assert.IsFalse(ok, "Cascade-race branch must return false.");
            Assert.AreEqual(state.AgencyId, senderAgencyId,
                "Cascade-race branch resolved the agency-id before the ContainsKey check; out param reflects the resolved id (caller would have used it for relay if write succeeded).");

            // Pre-existing seeded stock 4 files might still be in the subdir
            // (RegisterAgency seeded them before we evicted), but our specific
            // Aurora file must NOT have been written.
            var auroraPath = Path.Combine(
                AgencySystem.GetKerbalsPathForAgency(state.AgencyId),
                "Aurora Test-Kerman.txt");
            Assert.IsFalse(File.Exists(auroraPath),
                "Cascade-race must DROP the write entirely, not write to the orphan subdir.");
        }

        // ------------------------------------------------------------------
        // 4. Proto: write overwrites existing file (atomic rotate)
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryWriteKerbalProtoPerAgency_OverwritesExistingFile_AtomicRotate()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            // First write
            var firstBytes = BytesForKerbal("Aurora Test-Kerman");
            KerbalSystem.TryWriteKerbalProtoPerAgency(
                "Majestic95", "Aurora Test-Kerman", firstBytes, firstBytes.Length, out _);

            // Second write with different content
            var secondBytes = Encoding.UTF8.GetBytes($"name = Aurora Test-Kerman\nstate = Killed\nToD = 100\n");
            KerbalSystem.TryWriteKerbalProtoPerAgency(
                "Majestic95", "Aurora Test-Kerman", secondBytes, secondBytes.Length, out _);

            var path = Path.Combine(
                AgencySystem.GetKerbalsPathForAgency(state.AgencyId),
                "Aurora Test-Kerman.txt");
            CollectionAssert.AreEqual(secondBytes, File.ReadAllBytes(path),
                "Second write must replace first via WriteAtomic rotate-and-rename.");

            // WriteAtomic leaves .bak from the rotation; assert it carries
            // first-write content (proves atomicity contract is honored).
            var bakPath = path + ".bak";
            Assert.IsTrue(File.Exists(bakPath),
                "Second WriteAtomic must rotate the previous version to .bak.");
            CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(bakPath),
                ".bak must contain the previous-good content for crash-recovery.");
        }

        // ------------------------------------------------------------------
        // 5. Remove happy path
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryDeleteKerbalPerAgency_HappyPath_DeletesFromAgencySubdir()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            // Seed a kerbal file in the agency subdir.
            var bytes = BytesForKerbal("Aurora Test-Kerman");
            KerbalSystem.TryWriteKerbalProtoPerAgency(
                "Majestic95", "Aurora Test-Kerman", bytes, bytes.Length, out _);
            var path = Path.Combine(
                AgencySystem.GetKerbalsPathForAgency(state.AgencyId),
                "Aurora Test-Kerman.txt");
            Assert.IsTrue(File.Exists(path));

            var ok = KerbalSystem.TryDeleteKerbalPerAgency(
                "Majestic95", "Aurora Test-Kerman", out var senderAgencyId);
            Assert.IsTrue(ok);
            Assert.AreEqual(state.AgencyId, senderAgencyId);
            Assert.IsFalse(File.Exists(path),
                "Remove must delete the agency-subdir kerbal file.");
        }

        // ------------------------------------------------------------------
        // 6. Remove: no mapping → DROP
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryDeleteKerbalPerAgency_NoAgencyMapping_DropsWithoutDeleting()
        {
            Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("ghost-player"));

            var ok = KerbalSystem.TryDeleteKerbalPerAgency(
                "ghost-player", "Aurora Test-Kerman", out var senderAgencyId);
            Assert.IsFalse(ok);
            Assert.AreEqual(Guid.Empty, senderAgencyId);
        }

        // ------------------------------------------------------------------
        // 7. Remove: cascade race
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryDeleteKerbalPerAgency_CascadeRace_DropsWithoutDeleting()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);
            // Seed file before evicting agency from Agencies dict.
            var bytes = BytesForKerbal("Aurora Test-Kerman");
            KerbalSystem.TryWriteKerbalProtoPerAgency(
                "Majestic95", "Aurora Test-Kerman", bytes, bytes.Length, out _);
            var path = Path.Combine(
                AgencySystem.GetKerbalsPathForAgency(state.AgencyId),
                "Aurora Test-Kerman.txt");
            Assert.IsTrue(File.Exists(path));

            // Simulate cascade race
            Assert.IsTrue(AgencySystem.Agencies.TryRemove(state.AgencyId, out _));

            var ok = KerbalSystem.TryDeleteKerbalPerAgency(
                "Majestic95", "Aurora Test-Kerman", out _);
            Assert.IsFalse(ok, "Cascade-race must DROP the delete.");
            Assert.IsTrue(File.Exists(path),
                "Cascade-race delete must NOT delete the file — TryDeleteAgency's cascade owns the cleanup.");
        }

        // ------------------------------------------------------------------
        // 8. Remove: deleting a missing file is a benign no-op
        // ------------------------------------------------------------------

        [TestMethod]
        public void TryDeleteKerbalPerAgency_MissingFile_NoThrow()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNotNull(state);

            // No prior write; deleting a non-existent kerbal file should
            // succeed (FileHandler.FileDelete is existence-checked) and
            // return true so the caller relays the remove to peers. Mirrors
            // the legacy KerbalSystem.HandleKerbalRemove behaviour.
            var ok = KerbalSystem.TryDeleteKerbalPerAgency(
                "Majestic95", "Nonexistent Kerman", out _);
            Assert.IsTrue(ok,
                "Delete of a missing file must still return true (caller relays).");
        }
    }
}
