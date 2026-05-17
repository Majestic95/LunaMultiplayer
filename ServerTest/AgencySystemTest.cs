using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the Stage 5.15a <see cref="AgencySystem"/> lifecycle — register on
    /// player auth, load on boot, save through <see cref="FileHandler.WriteAtomic"/>, plus
    /// the dual-mode gate that keeps the shared-agency code path bit-identical when
    /// <see cref="GameplaySettings.SettingsStore"/>'s <c>PerAgencyCareer</c> is false.
    ///
    /// `SettingsStore` is a process-global singleton; the per-test setup overwrites the
    /// relevant fields and the teardown restores defaults so successive test classes
    /// don't inherit our state. `UniverseDirectory` likewise — we land a fresh temp dir
    /// per test and delete it on cleanup.
    /// </summary>
    [TestClass]
    public class AgencySystemTest
    {
        [TestInitialize]
        public void Setup()
        {
            // Per-test temp universe; the Agencies folder lives under it.
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(), "lmp-agencysystem-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            // Default-on for tests; specific tests flip the gate when proving the off path.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.StartingFunds = 25_000f;
            GameplaySettings.SettingsStore.StartingScience = 10f;
            GameplaySettings.SettingsStore.StartingReputation = 5f;

            AgencySystem.Reset();
        }

        [TestCleanup]
        public void Teardown()
        {
            AgencySystem.Reset();

            // Restore the singleton so adjacent test classes don't inherit our overrides.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GameplaySettings.SettingsStore.StartingFunds = 0f;
            GameplaySettings.SettingsStore.StartingScience = 0f;
            GameplaySettings.SettingsStore.StartingReputation = 0f;

            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, recursive: true);
        }

        [TestMethod]
        public void RegisterAgency_CreatesStateSeededFromSettings()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");

            Assert.IsNotNull(state);
            Assert.AreNotEqual(Guid.Empty, state.AgencyId);
            Assert.AreEqual("Majestic95", state.OwningPlayerName);
            Assert.AreEqual("Majestic95 Space Agency", state.DisplayName);
            Assert.AreEqual(25_000d, state.Funds);
            Assert.AreEqual(10d, state.Science);
            Assert.AreEqual(5d, state.Reputation);
        }

        [TestMethod]
        public void RegisterAgency_PersistsToDisk()
        {
            var state = AgencySystem.RegisterAgency("Majestic95");

            Assert.IsTrue(File.Exists(state.FilePath), $"expected {state.FilePath} to exist after RegisterAgency");

            // Round-trip through the actual file to prove the persisted content reflects
            // the new state, not a stale write from earlier in the suite.
            var fromDisk = AgencyState.Parse(File.ReadAllText(state.FilePath));
            Assert.AreEqual(state.AgencyId, fromDisk.AgencyId);
            Assert.AreEqual(state.DisplayName, fromDisk.DisplayName);
        }

        [TestMethod]
        public void RegisterAgency_IsIdempotent_ForSamePlayer()
        {
            var first = AgencySystem.RegisterAgency("Majestic95");
            var second = AgencySystem.RegisterAgency("Majestic95");

            Assert.AreSame(first, second, "second RegisterAgency for the same player must return the existing instance");
            Assert.AreEqual(1, AgencySystem.Agencies.Count);
            Assert.AreEqual(1, AgencySystem.AgencyByPlayerName.Count);
        }

        [TestMethod]
        public void RegisterAgency_DistinctPlayers_GetDistinctAgencies()
        {
            var a = AgencySystem.RegisterAgency("PlayerA");
            var b = AgencySystem.RegisterAgency("PlayerB");

            Assert.AreNotEqual(a.AgencyId, b.AgencyId);
            Assert.AreEqual(2, AgencySystem.Agencies.Count);
        }

        [TestMethod]
        public void RegisterAgency_NullOrEmptyPlayerName_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() => AgencySystem.RegisterAgency(null));
            Assert.ThrowsException<ArgumentException>(() => AgencySystem.RegisterAgency(string.Empty));
        }

        [TestMethod]
        public void LoadAgency_ReturnsFromRegistry_WhenAlreadyLoaded()
        {
            var registered = AgencySystem.RegisterAgency("Majestic95");

            var loaded = AgencySystem.LoadAgency(registered.AgencyId);

            Assert.AreSame(registered, loaded, "LoadAgency must return the registry instance, not a re-parsed copy");
        }

        [TestMethod]
        public void LoadAgency_FallsBackToDisk_WhenRegistryEmpty()
        {
            var registered = AgencySystem.RegisterAgency("Majestic95");
            var savedId = registered.AgencyId;

            // Wipe the in-memory registry; the file on disk remains.
            AgencySystem.Reset();

            var loaded = AgencySystem.LoadAgency(savedId);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(savedId, loaded.AgencyId);
            Assert.AreEqual("Majestic95", loaded.OwningPlayerName);

            // Disk fallback should populate the registry so subsequent reads are O(1).
            Assert.IsTrue(AgencySystem.Agencies.ContainsKey(savedId));
            Assert.IsTrue(AgencySystem.AgencyByPlayerName.ContainsKey("Majestic95"));
        }

        [TestMethod]
        public void LoadAgency_ReturnsNull_ForUnknownGuid()
        {
            Assert.IsNull(AgencySystem.LoadAgency(Guid.NewGuid()));
        }

        [TestMethod]
        public void LoadExistingAgencies_PopulatesRegistryFromDisk()
        {
            // Seed three agencies, drop the registry, then prove LoadExistingAgencies
            // recovers all three from disk.
            var alpha = AgencySystem.RegisterAgency("Alpha");
            var beta = AgencySystem.RegisterAgency("Beta");
            var gamma = AgencySystem.RegisterAgency("Gamma");

            AgencySystem.Reset();
            Assert.AreEqual(0, AgencySystem.Agencies.Count);

            AgencySystem.LoadExistingAgencies();

            Assert.AreEqual(3, AgencySystem.Agencies.Count);
            Assert.IsTrue(AgencySystem.Agencies.ContainsKey(alpha.AgencyId));
            Assert.IsTrue(AgencySystem.Agencies.ContainsKey(beta.AgencyId));
            Assert.IsTrue(AgencySystem.Agencies.ContainsKey(gamma.AgencyId));
            Assert.AreEqual("Alpha", AgencySystem.Agencies[alpha.AgencyId].OwningPlayerName);
        }

        [TestMethod]
        public void LoadExistingAgencies_SkipsCorruptFilesWithoutAbortingTheRest()
        {
            // Spec §3 isolation principle — one bad file must not block the rest of the
            // universe from booting. Same shape as the per-contract isolation Stage 5.17b
            // commits to. Land a deliberately-broken file alongside a good one and assert
            // the good one still loads.
            var good = AgencySystem.RegisterAgency("Good");
            AgencySystem.Reset();

            File.WriteAllText(
                Path.Combine(AgencyState.AgenciesPath, "deadbeef000000000000000000000000.txt"),
                "this is not a valid AgencyState payload");

            AgencySystem.LoadExistingAgencies();

            Assert.AreEqual(1, AgencySystem.Agencies.Count);
            Assert.IsTrue(AgencySystem.Agencies.ContainsKey(good.AgencyId));
        }

        [TestMethod]
        public void LoadExistingAgencies_HandlesEmptyDirectory()
        {
            // Fresh universe, no agencies yet. LoadExistingAgencies must be a clean no-op,
            // not throw, not log an error.
            AgencySystem.LoadExistingAgencies();

            Assert.AreEqual(0, AgencySystem.Agencies.Count);
        }

        [TestMethod]
        public void LoadAgency_HealsCanonicalPath_AfterBakOnlyRecovery()
        {
            // Closes the 5.14c deferred CONSIDER: when ReadAtomic falls back to .bak,
            // subsequent reads would re-log the warning forever. AgencySystem heals the
            // canonical path on first surfaced recovery so the warning fires once, not
            // on every read.
            var registered = AgencySystem.RegisterAgency("Majestic95");
            var savedId = registered.AgencyId;
            var canonicalPath = registered.FilePath;

            // Stage the post-crash-window state: only .bak exists, canonical is missing.
            // Simulate this by writing once (creates canonical), writing again with the
            // same content (rotates canonical to .bak), then deleting canonical.
            AgencySystem.SaveAgency(savedId); // second write — rotates the first to .bak
            File.Delete(canonicalPath);
            Assert.IsTrue(File.Exists(canonicalPath + ".bak"), "test setup invariant: .bak must exist");

            AgencySystem.Reset();

            var loaded = AgencySystem.LoadAgency(savedId);

            Assert.IsNotNull(loaded);
            Assert.IsTrue(File.Exists(canonicalPath),
                "after LoadAgency recovers from .bak it must heal the canonical path so subsequent reads don't re-warn");
        }

        [TestMethod]
        public void SaveAgency_AfterFieldMutation_PersistsNewValues()
        {
            // Stage 5.17b will route Share* mutations through SaveAgency. Pin the
            // basic contract: mutate a field, save, read back fresh — value persists.
            var state = AgencySystem.RegisterAgency("Majestic95");
            state.Funds = 99_999d;

            AgencySystem.SaveAgency(state.AgencyId);

            var fromDisk = AgencyState.Parse(File.ReadAllText(state.FilePath));
            Assert.AreEqual(99_999d, fromDisk.Funds);
        }

        [TestMethod]
        public void SaveAgency_UnknownGuid_IsSilentNoOp()
        {
            // SaveAgency must not throw on a stale Guid — Stage 5.17b broadcast handlers
            // may race a disconnect that drops the agency from the registry; bare-throw
            // there would crash a Lidgren callback thread (charter anti-pattern).
            AgencySystem.SaveAgency(Guid.NewGuid());
        }

        [TestMethod]
        public void AllOperations_AreNoOps_WhenPerAgencyCareerIsDisabled()
        {
            // Dual-mode acceptance gate (spec §11). With the setting off, AgencySystem
            // must have ZERO observable effect: no registry entries, no disk writes,
            // no exceptions.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;

            var register = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsNull(register, "RegisterAgency must return null when the gate is off");

            var load = AgencySystem.LoadAgency(Guid.NewGuid());
            Assert.IsNull(load, "LoadAgency must return null when the gate is off");

            AgencySystem.SaveAgency(Guid.NewGuid());
            AgencySystem.OnPlayerAuthenticated("Majestic95");
            AgencySystem.LoadExistingAgencies();

            Assert.AreEqual(0, AgencySystem.Agencies.Count);
            Assert.AreEqual(0, AgencySystem.AgencyByPlayerName.Count);

            // Crucially: no agency file should have been written.
            Assert.AreEqual(0, Directory.GetFiles(AgencyState.AgenciesPath).Length);
        }

        [TestMethod]
        public void OnPlayerAuthenticated_DelegatesToRegisterAgency()
        {
            // The HandshakeSystem hook calls OnPlayerAuthenticated, not RegisterAgency
            // directly. Pin that the indirection actually creates the agency rather
            // than silently dropping the call.
            AgencySystem.OnPlayerAuthenticated("Majestic95");

            Assert.AreEqual(1, AgencySystem.Agencies.Count);
            Assert.IsTrue(AgencySystem.AgencyByPlayerName.ContainsKey("Majestic95"));
        }

        [TestMethod]
        public void LoadExistingAgencies_RestoresAgencyByPlayerNameIndex()
        {
            // The player-name → guid index must be rebuilt on boot, otherwise a
            // reconnecting player would get a fresh agency instead of their saved one.
            var registered = AgencySystem.RegisterAgency("Majestic95");
            AgencySystem.Reset();

            AgencySystem.LoadExistingAgencies();

            Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue("Majestic95", out var lookedUpId));
            Assert.AreEqual(registered.AgencyId, lookedUpId);
        }

        [TestMethod]
        public void RegisterAgency_ConcurrentCallsForSameName_ProduceSingleAgency()
        {
            // Closes the pass-1 [SHOULD FIX] race window: two OnPlayerAuthenticated calls
            // for the same player name arriving on separate Lidgren receive threads must
            // serialize through the per-name lock and converge on a single agency. Without
            // the lock, both threads miss the lookups, both mint distinct Guids, both
            // TryAdd succeed (different keys), and one agency is orphaned on disk.
            const int parallelCallers = 16;
            var registered = new AgencyState[parallelCallers];

            Parallel.For(0, parallelCallers, i =>
            {
                registered[i] = AgencySystem.RegisterAgency("ConcurrentAlice");
            });

            // All callers must observe the same AgencyState instance.
            for (var i = 1; i < parallelCallers; i++)
            {
                Assert.AreSame(registered[0], registered[i],
                    $"caller {i} received a different AgencyState — race window not closed");
            }

            Assert.AreEqual(1, AgencySystem.Agencies.Count, "exactly one agency must be registered");
            Assert.AreEqual(1, AgencySystem.AgencyByPlayerName.Count);
            Assert.AreEqual(1, Directory.GetFiles(AgencyState.AgenciesPath, "*.txt").Length,
                "exactly one agency file must exist on disk — no orphans from a losing race");
        }

        [TestMethod]
        public void LoadExistingAgencies_IgnoresNonTxtFiles()
        {
            // The folder may end up with .tmp / .bak siblings from WriteAtomic's rotation
            // and (in operator workflows) hand-dropped notes. LoadExistingAgencies must
            // skip everything that isn't a *.txt agency file. .bak in particular: it's
            // a sibling, not a primary, and consuming it would create a duplicate
            // registry entry for an in-flight write that hasn't completed.
            AgencySystem.RegisterAgency("Majestic95"); // creates {guid}.txt
            File.WriteAllText(Path.Combine(AgencyState.AgenciesPath, "notes.md"), "operator scratch");
            File.WriteAllText(Path.Combine(AgencyState.AgenciesPath, "stale.bak"), "leftover .bak");

            AgencySystem.Reset();
            AgencySystem.LoadExistingAgencies();

            Assert.AreEqual(1, AgencySystem.Agencies.Count,
                "non-.txt files must be ignored; only one agency was actually persisted");
        }
    }
}
