using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.Message;
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
            // [Stage 5.17e-1] AgencySystem.PerAgencyEnabled gates on PerAgencyCareer AND
            // GameMode=Career (Career-only product decision, spec §10 Q-Mode). Tests that
            // exercise the on-path must set both.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
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
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox; // matches the GeneralSettingsDefinition default
            GameplaySettings.SettingsStore.StartingFunds = 0f;
            GameplaySettings.SettingsStore.StartingScience = 0f;
            GameplaySettings.SettingsStore.StartingReputation = 0f;
            // Session-19 review-finding A.1 tests seed scenarios + vessels + flip
            // ServerRunning. Wipe them here so adjacent test classes (LockSystemAgencyTest
            // etc. already self-clean vessels, but ScenarioStoreSystem.CurrentScenarios
            // has no other clear-on-teardown) don't observe our leftovers.
            ScenarioStoreSystem.CurrentScenarios.Clear();
            VesselStoreSystem.CurrentVessels.Clear();
            ServerContext.ServerRunning = false;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse = false;

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
        public void RegisterAgency_HealsStaleAgencyByPlayerName_WhenAgenciesRegistryMisses()
        {
            // Round-3 review regression test for the stale-index defensive heal in
            // RegisterAgency. If AgencyByPlayerName points at a Guid that the Agencies
            // registry doesn't have AND the disk doesn't have either, the new lookup
            // unbinds the stale entry and mints a fresh agency rather than silently
            // returning a missing agency or shadowing the orphan in future vessel stamps.
            var staleGuid = Guid.NewGuid();
            AgencySystem.AgencyByPlayerName["StaleAlice"] = staleGuid;
            // Note: AgencySystem.Agencies does NOT contain staleGuid, and no on-disk file
            // exists at Universe/Agencies/{staleGuid:N}.txt either. This is the exact
            // post-crash state the heal path is designed for.

            var freshState = AgencySystem.RegisterAgency("StaleAlice");

            Assert.IsNotNull(freshState);
            Assert.AreNotEqual(staleGuid, freshState.AgencyId,
                "RegisterAgency must mint a NEW Guid when the stale-index target is missing from both registry and disk.");
            Assert.AreEqual(freshState.AgencyId, AgencySystem.AgencyByPlayerName["StaleAlice"],
                "Index must now point at the freshly minted agency, not the stale orphan.");
            Assert.IsTrue(AgencySystem.Agencies.ContainsKey(freshState.AgencyId),
                "Fresh agency must be registered in the Agencies dictionary.");
            Assert.IsTrue(File.Exists(freshState.FilePath),
                "RegisterAgency must persist to disk before the index flip — file should exist after return.");
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
        public void AllOperations_AreNoOps_WhenGameModeIsNotCareer()
        {
            // [Stage 5.17e-1, spec §10 Q-Mode Career-only sign-off] PerAgencyEnabled
            // gates on BOTH PerAgencyCareer=true AND GameMode==Career. Even with the
            // setting on, a non-Career game mode collapses the per-agency surface to
            // the same dual-mode silence as PerAgencyCareer=false. Pin Science here
            // (Career-but-no-Funding-Instance product hazard) and Sandbox below as a
            // separate assertion path.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;

            Assert.IsFalse(AgencySystem.PerAgencyEnabled,
                "PerAgencyEnabled must be false in Science mode regardless of PerAgencyCareer");

            Assert.IsNull(AgencySystem.RegisterAgency("Majestic95"));
            Assert.IsNull(AgencySystem.LoadAgency(Guid.NewGuid()));
            AgencySystem.SaveAgency(Guid.NewGuid());
            AgencySystem.OnPlayerAuthenticated("Majestic95");
            AgencySystem.LoadExistingAgencies();

            Assert.AreEqual(0, AgencySystem.Agencies.Count, "No registry entries should land under Science mode");
            Assert.AreEqual(0, AgencySystem.AgencyByPlayerName.Count);
            Assert.AreEqual(0, Directory.GetFiles(AgencyState.AgenciesPath).Length,
                "No agency file should be written when GameMode != Career");

            // Sandbox path — independent assertion in case Science and Sandbox diverge
            // in a future regression.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            Assert.IsFalse(AgencySystem.PerAgencyEnabled);
            Assert.IsNull(AgencySystem.RegisterAgency("AnotherPlayer"));
            AgencySystem.OnPlayerAuthenticated("AnotherPlayer");

            Assert.AreEqual(0, AgencySystem.Agencies.Count, "No registry entries should land under Sandbox mode");
        }

        [TestMethod]
        public void PerAgencyEnabled_TrueOnlyWhenBothPerAgencyCareerAndCareerMode()
        {
            // Pure-property pin so future refactors don't accidentally drop one of the
            // two conditions. The product decision is documented on the property itself
            // (spec §10 Q-Mode) and on AgencySystem class XML.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            Assert.IsTrue(AgencySystem.PerAgencyEnabled);

            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            Assert.IsFalse(AgencySystem.PerAgencyEnabled);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            Assert.IsFalse(AgencySystem.PerAgencyEnabled);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            Assert.IsFalse(AgencySystem.PerAgencyEnabled);
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

        // ---- Stage 5.15c: AgencyMsgReader.ValidateDisplayName ----
        // The reader's HandleMessage path is exercised end-to-end in Stage 5.16a's
        // MockClient harness; here we pin the pure-data validator independently. Each
        // case maps to one rejection reason a client UI would see (Stage 5.18c).

        [TestMethod]
        public void ValidateDisplayName_AcceptsTypicalName()
        {
            Assert.IsTrue(AgencyMsgReader.ValidateDisplayName("Cool Space Co", out var reason));
            Assert.AreEqual(string.Empty, reason);
        }

        [TestMethod]
        public void ValidateDisplayName_RejectsNullOrEmpty()
        {
            Assert.IsFalse(AgencyMsgReader.ValidateDisplayName(null, out var nullReason));
            StringAssert.Contains(nullReason, "empty");

            Assert.IsFalse(AgencyMsgReader.ValidateDisplayName(string.Empty, out var emptyReason));
            StringAssert.Contains(emptyReason, "empty");
        }

        [TestMethod]
        public void ValidateDisplayName_RejectsWhitespaceOnly()
        {
            // Whitespace-only names would render as blank labels in the tracking
            // station — disallow at the validator so the client never sees an
            // "anonymous" agency in the public summary.
            Assert.IsFalse(AgencyMsgReader.ValidateDisplayName("   ", out var reason));
            StringAssert.Contains(reason, "empty");
        }

        [TestMethod]
        public void ValidateDisplayName_RejectsAtMaxPlusOne()
        {
            // Pin the cap exactly. Names at the boundary pass; one over fails.
            var atCap = new string('a', AgencyMsgReader.MaxDisplayNameLength);
            var overCap = new string('a', AgencyMsgReader.MaxDisplayNameLength + 1);

            Assert.IsTrue(AgencyMsgReader.ValidateDisplayName(atCap, out _));
            Assert.IsFalse(AgencyMsgReader.ValidateDisplayName(overCap, out var reason));
            StringAssert.Contains(reason, "64");
        }

        [TestMethod]
        public void ValidateDisplayName_RejectsConfigNodeHostileCharacters()
        {
            // Bare '=' / '{' / '}' / newline would corrupt the LunaConfigNode key=value
            // disk format AgencyState writes through. Same rationale as the BUG-013
            // reaction-wheel locale fix — the storage format is fragile, sanitise at
            // the wire boundary.
            string[] hostile = { "Foo=Bar", "Foo{Bar}", "Foo\nBar", "Foo\rBar" };
            foreach (var name in hostile)
            {
                Assert.IsFalse(AgencyMsgReader.ValidateDisplayName(name, out var reason),
                    $"expected '{name}' to be rejected");
                StringAssert.Contains(reason, "illegal");
            }
        }

        [TestMethod]
        public void ValidateDisplayName_RejectsControlCharacters()
        {
            // char.IsControl catches the whole bash-history-corruption class of inputs
            // (\t, \0, \b, etc) without us having to enumerate.
            Assert.IsFalse(AgencyMsgReader.ValidateDisplayName("Foo\tBar", out var tabReason));
            StringAssert.Contains(tabReason, "illegal");

            Assert.IsFalse(AgencyMsgReader.ValidateDisplayName("Foo\0Bar", out var nullReason));
            StringAssert.Contains(nullReason, "illegal");
        }

        [TestMethod]
        public void ValidateDisplayName_AcceptsUnicodeAndSpaces()
        {
            // Cyrillic + emoji + multi-word names are all valid (under the 64-char cap).
            // Pin that the validator doesn't accidentally constrain to ASCII.
            Assert.IsTrue(AgencyMsgReader.ValidateDisplayName("Майор95 Space Agency", out _));
            Assert.IsTrue(AgencyMsgReader.ValidateDisplayName("🚀 Rocket Co", out _));
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

        [TestMethod]
        public void LoadExistingAgencies_RefusesBoot_OnProgressOnlyUpgradeHazard()
        {
            // [Stage 5.17e-9 review-finding A.1] The boot refusal must fire on any of
            // the three accumulated-shared-state hazards the Warn helper counts:
            // strategies, world-firsts (ProgressTracking), or facility upgrades.
            // Pre-fix, only StrategySystem was checked — a universe with ONLY
            // ProgressTracking entries booted with a warning and then silently
            // stripped the data on first per-agency client connect. Pin the
            // ProgressTracking branch end-to-end through the public LoadExistingAgencies
            // entry point.
            SetupBootRefusalScenario();
            SeedProgressTrackingHazard();

            AgencySystem.LoadExistingAgencies();

            Assert.IsFalse(ServerContext.ServerRunning,
                "ServerRunning must be flipped to false when ProgressTracking shared entries exist " +
                "and AllowEnablePerAgencyOnExistingUniverse=false.");
            Assert.AreEqual(0, AgencySystem.Agencies.Count,
                "No agencies should be loaded — the refusal fires after the would-be-load path is " +
                "drained by Reset() in Setup(), so the registry stays empty.");
        }

        [TestMethod]
        public void LoadExistingAgencies_RefusesBoot_OnFacilityOnlyUpgradeHazard()
        {
            // [Stage 5.17e-9 review-finding A.1] Sibling of the ProgressTracking test:
            // ScenarioUpgradeableFacilities hazard alone must trigger refusal. The
            // facility branch sweeps the same known-KSC-facility-key list as the
            // WarnAboutSharedProgressFacilityOnUpgrade helper and treats lvl>0 as
            // accumulated upgrade state.
            SetupBootRefusalScenario();
            SeedFacilityUpgradeHazard();

            AgencySystem.LoadExistingAgencies();

            Assert.IsFalse(ServerContext.ServerRunning,
                "ServerRunning must be flipped to false when a known facility has lvl>0 and " +
                "AllowEnablePerAgencyOnExistingUniverse=false.");
            Assert.AreEqual(0, AgencySystem.Agencies.Count);
        }

        [TestMethod]
        public void LoadExistingAgencies_AllowsBoot_OnProgressHazard_WhenOverrideOn()
        {
            // Operator opt-in: the override flag suppresses the refusal so the
            // projector's strip-on-first-connect is the documented contract. Pin
            // that the override actually works — without this, the refusal would
            // be impossible to bypass and pre-0.31 upgraders couldn't continue.
            SetupBootRefusalScenario();
            SeedProgressTrackingHazard();
            GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse = true;
            try
            {
                AgencySystem.LoadExistingAgencies();

                Assert.IsTrue(ServerContext.ServerRunning,
                    "ServerRunning must remain true when the operator has explicitly opted in via " +
                    "AllowEnablePerAgencyOnExistingUniverse=true.");
            }
            finally
            {
                GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse = false;
            }
        }

        private void SetupBootRefusalScenario()
        {
            // Common prep for the three refusal tests: gate already on via Setup();
            // ensure pre-conditions for the refusal — server marked running, override
            // off (default), no agencies loaded (already true from Reset), and at
            // least one vessel so the universe is non-pristine.
            ServerContext.ServerRunning = true;
            GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse = false;

            // Wipe scenario / vessel store state so adjacent tests in this class
            // don't interfere. (No existing test in AgencySystemTest seeds these,
            // so a clean slate is the correct starting point.)
            ScenarioStoreSystem.CurrentScenarios.Clear();
            VesselStoreSystem.CurrentVessels.Clear();

            // Non-pristine universe: any vessel makes IsEmpty=false.
            VesselStoreSystem.CurrentVessels.TryAdd(Guid.NewGuid(), LoadSampleVessel());
        }

        private static void SeedProgressTrackingHazard()
        {
            // Minimum hazard shape: ProgressTracking { Progress { Kerbin { … } } }.
            var scenario = new LunaConfigNode.CfgNode.ConfigNode("") { Name = "ProgressTracking" };
            var progress = new LunaConfigNode.CfgNode.ConfigNode("") { Name = "Progress" };
            scenario.AddNode(progress);
            var kerbin = new LunaConfigNode.CfgNode.ConfigNode("") { Name = "Kerbin" };
            progress.AddNode(kerbin);
            ScenarioStoreSystem.CurrentScenarios.TryAdd("ProgressTracking", scenario);
        }

        private static void SeedFacilityUpgradeHazard()
        {
            // Minimum hazard shape: ScenarioUpgradeableFacilities { SpaceCenter/LaunchPad { lvl = 2 } }.
            // The 5.17e-9 sweep treats any known-KSC-facility with lvl>0 as a hazard.
            // Parse the inner lvl value via the ConfigNode(text) constructor — matches
            // the AgencyScenarioProjectorTest pattern (Project tests build scenarios
            // the same way) and works around MixedCollection.Update's update-only
            // semantics for values that don't exist yet.
            var scenario = new LunaConfigNode.CfgNode.ConfigNode("") { Name = "ScenarioUpgradeableFacilities" };
            var launchPad = new LunaConfigNode.CfgNode.ConfigNode("lvl = 2\n") { Name = "SpaceCenter/LaunchPad" };
            scenario.AddNode(launchPad);
            ScenarioStoreSystem.CurrentScenarios.TryAdd("ScenarioUpgradeableFacilities", scenario);
        }

        private static Server.System.Vessel.Classes.Vessel LoadSampleVessel()
        {
            // Reused from LockSystemAgencyTest's pattern — the XmlExampleFiles/Others
            // folder is copied into the ServerTest output directory by the csproj.
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");
            return new Server.System.Vessel.Classes.Vessel(
                File.ReadAllText(Directory.GetFiles(dir).OrderBy(p => p, StringComparer.Ordinal).First()));
        }

        // --- TryResolveAgencyToken — Stage 5.18d shared resolver ---------------
        // Backs setagency (slice f) / transferagency (slice e) / deleteagency
        // (slice g) admin commands. Accepts both Guid-string and OwningPlayerName
        // forms so operators can use either /listagencies output or the player
        // handle directly.

        [TestMethod]
        public void TryResolveAgencyToken_ByGuidN_ReturnsState()
        {
            var alice = AgencySystem.RegisterAgency("Alice");
            Assert.IsTrue(AgencySystem.TryResolveAgencyToken(alice.AgencyId.ToString("N"), out var resolved));
            Assert.AreSame(alice, resolved);
        }

        [TestMethod]
        public void TryResolveAgencyToken_ByGuidD_ReturnsState()
        {
            // Operators copy-pasting from a vessel file (where lmpOwningAgency is
            // emitted as "N") OR from Guid.ToString() default ("D" hyphenated)
            // should both work. Guid.TryParse accepts all .NET formats.
            var alice = AgencySystem.RegisterAgency("Alice");
            Assert.IsTrue(AgencySystem.TryResolveAgencyToken(alice.AgencyId.ToString("D"), out var resolved));
            Assert.AreSame(alice, resolved);
        }

        [TestMethod]
        public void TryResolveAgencyToken_ByOwnerName_ReturnsState()
        {
            var alice = AgencySystem.RegisterAgency("Alice");
            Assert.IsTrue(AgencySystem.TryResolveAgencyToken("Alice", out var resolved));
            Assert.AreSame(alice, resolved);
        }

        [TestMethod]
        public void TryResolveAgencyToken_OwnerNameCaseSensitive()
        {
            // AgencyByPlayerName uses StringComparer.Ordinal. An operator typing
            // "alice" when the registered owner is "Alice" must fail-soft to "not
            // found" rather than silently match — typo discipline.
            AgencySystem.RegisterAgency("Alice");
            Assert.IsFalse(AgencySystem.TryResolveAgencyToken("alice", out var resolved));
            Assert.IsNull(resolved);
        }

        [TestMethod]
        public void TryResolveAgencyToken_UnknownGuid_ReturnsFalse()
        {
            // A Guid that parses but isn't in the registry (e.g. operator typo, or
            // the agency was deleted by a future deleteagency).
            var nonexistent = Guid.NewGuid();
            Assert.IsFalse(AgencySystem.TryResolveAgencyToken(nonexistent.ToString("N"), out var resolved));
            Assert.IsNull(resolved);
        }

        [TestMethod]
        public void TryResolveAgencyToken_UnknownName_ReturnsFalse()
        {
            // No agency for the player handle.
            Assert.IsFalse(AgencySystem.TryResolveAgencyToken("NotARegisteredPlayer", out var resolved));
            Assert.IsNull(resolved);
        }

        [TestMethod]
        public void TryResolveAgencyToken_EmptyOrNullToken_ReturnsFalse()
        {
            Assert.IsFalse(AgencySystem.TryResolveAgencyToken(null, out _));
            Assert.IsFalse(AgencySystem.TryResolveAgencyToken(string.Empty, out _));
        }

        [TestMethod]
        public void TryResolveAgencyToken_GateOff_ReturnsFalse()
        {
            // Stage 5.18d admin commands MUST refuse loudly under gate-off; the
            // resolver short-circuits to false so the caller's error message is
            // about the gate, not about a stale registry lookup.
            AgencySystem.RegisterAgency("Alice");
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            Assert.IsFalse(AgencySystem.TryResolveAgencyToken("Alice", out _));
        }

        [TestMethod]
        public void TryResolveAgencyToken_GuidParseTakesPrecedenceOverNameLookup()
        {
            // Pathological case: operator registers a second player under a name
            // that happens to match the first agency's Guid hex form. The Guid
            // path commits to the registry-by-id lookup; when it hits, that
            // wins — the name-shaped second player never gets considered for
            // this resolve.
            var alice = AgencySystem.RegisterAgency("Alice");
            var weird = AgencySystem.RegisterAgency(alice.AgencyId.ToString("N"));
            Assert.IsNotNull(weird);
            Assert.IsTrue(AgencySystem.TryResolveAgencyToken(alice.AgencyId.ToString("N"), out var resolved));
            Assert.AreSame(alice, resolved, "Guid form resolves by registry id, not by name-coincidence.");
        }

        // --- TryDeleteAgency — Stage 5.18d slice (g) /deleteagency -----------

        [TestMethod]
        public void TryDeleteAgency_RemovesRegistryEntryAndIndex()
        {
            var alice = AgencySystem.RegisterAgency("Alice");
            var id = alice.AgencyId;

            Assert.IsTrue(AgencySystem.TryDeleteAgency(alice, out var demoted, out var reason));
            Assert.AreEqual(string.Empty, reason);
            Assert.AreEqual(0, demoted.Count, "no vessels in test universe = nothing to demote");

            Assert.IsFalse(AgencySystem.Agencies.ContainsKey(id));
            Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("Alice"));
        }

        [TestMethod]
        public void TryDeleteAgency_DeletesCanonicalAndBakFiles()
        {
            var alice = AgencySystem.RegisterAgency("Alice");
            var canonicalPath = alice.FilePath;
            var bakPath = canonicalPath + ".bak";

            // Persist twice so both canonical + .bak exist (WriteAtomic rotates).
            AgencySystem.SaveAgency(alice.AgencyId);
            AgencySystem.SaveAgency(alice.AgencyId);
            Assert.IsTrue(File.Exists(canonicalPath));
            Assert.IsTrue(File.Exists(bakPath));

            Assert.IsTrue(AgencySystem.TryDeleteAgency(alice, out _, out _));

            Assert.IsFalse(File.Exists(canonicalPath), "canonical file deleted");
            Assert.IsFalse(File.Exists(bakPath), ".bak file deleted");
        }

        [TestMethod]
        public void TryDeleteAgency_NullSource_FailsDefensively()
        {
            Assert.IsFalse(AgencySystem.TryDeleteAgency(null, out var demoted, out var reason));
            StringAssert.Contains(reason, "null");
            Assert.AreEqual(0, demoted.Count);
        }

        [TestMethod]
        public void TryDeleteAgency_GateOff_FailsWithReason()
        {
            var alice = AgencySystem.RegisterAgency("Alice");
            GameplaySettings.SettingsStore.PerAgencyCareer = false;

            Assert.IsFalse(AgencySystem.TryDeleteAgency(alice, out _, out var reason));
            StringAssert.Contains(reason, "Per-agency career is not active");
        }

        [TestMethod]
        public void TryDeleteAgency_AfterReload_AgencyStaysDeleted()
        {
            // Persistence: a fresh Reset + LoadExistingAgencies after the delete
            // must NOT reconstruct the agency. Disk is the source of truth; if
            // the file is gone, the agency is gone.
            var alice = AgencySystem.RegisterAgency("Alice");
            var id = alice.AgencyId;
            AgencySystem.TryDeleteAgency(alice, out _, out _);

            AgencySystem.Reset();
            AgencySystem.LoadExistingAgencies();

            Assert.IsFalse(AgencySystem.Agencies.ContainsKey(id));
            Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("Alice"));
        }

        [TestMethod]
        public void TryDeleteAgency_AfterDelete_OwnerReconnectMintsFreshAgency()
        {
            // The prior owner's RegisterAgency on next "connect" (test invokes it
            // directly here) hits the no-mapping branch and mints a fresh agency
            // with the configured StartingFunds/Science/Reputation seeds. Pins the
            // post-delete reconnect UX documented on DeleteAgencyCommand.
            var alice = AgencySystem.RegisterAgency("Alice");
            var originalId = alice.AgencyId;

            AgencySystem.TryDeleteAgency(alice, out _, out _);

            var fresh = AgencySystem.RegisterAgency("Alice");

            Assert.IsNotNull(fresh);
            Assert.AreNotEqual(originalId, fresh.AgencyId, "fresh agency mints a new Guid");
            Assert.AreEqual(25_000d, fresh.Funds);
            Assert.AreEqual(10d, fresh.Science);
            Assert.AreEqual(5d, fresh.Reputation);
        }

        // --- TryRenameAgencyOwner — Stage 5.18d slice (e) /transferagency ----
        // Renames the OwningPlayerName on an existing AgencyState; vessel
        // OwningAgencyId stamps are unaffected. Pins the atomic mutation
        // (state field + AgencyByPlayerName index + disk persistence) +
        // collision detection + lock ordering.

        [TestMethod]
        public void TryRenameAgencyOwner_RenamesStateAndIndex()
        {
            var alice = AgencySystem.RegisterAgency("Alice");
            Assert.IsTrue(AgencySystem.TryRenameAgencyOwner(alice, "Bob", out var reason));
            Assert.AreEqual(string.Empty, reason);

            // State field updated.
            Assert.AreEqual("Bob", alice.OwningPlayerName);
            // Index: old name removed, new name maps to same id.
            Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("Alice"));
            Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue("Bob", out var idAfter));
            Assert.AreEqual(alice.AgencyId, idAfter);
            // Disk persisted with the new name.
            var roundTripped = AgencyState.Parse(File.ReadAllText(alice.FilePath));
            Assert.AreEqual("Bob", roundTripped.OwningPlayerName);
            Assert.AreEqual(alice.AgencyId, roundTripped.AgencyId);
        }

        [TestMethod]
        public void TryRenameAgencyOwner_SameName_IdempotentNoOp()
        {
            // Operator scripts may re-issue the same transferagency twice; the
            // second call returns success without churning disk / lock state.
            var alice = AgencySystem.RegisterAgency("Alice");
            Assert.IsTrue(AgencySystem.TryRenameAgencyOwner(alice, "Alice", out _));
            Assert.AreEqual("Alice", alice.OwningPlayerName);
            Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue("Alice", out var id));
            Assert.AreEqual(alice.AgencyId, id);
        }

        [TestMethod]
        public void TryRenameAgencyOwner_NewNameCollision_FailsWithReason()
        {
            // Both Alice and Bob have agencies. Renaming Alice's agency to "Bob"
            // would collide with Bob's existing agency in AgencyByPlayerName —
            // refuse and surface the operator-facing reason.
            var alice = AgencySystem.RegisterAgency("Alice");
            var bob = AgencySystem.RegisterAgency("Bob");

            Assert.IsFalse(AgencySystem.TryRenameAgencyOwner(alice, "Bob", out var reason));
            StringAssert.Contains(reason, "already owns another agency");

            // Both indexes intact post-refusal.
            Assert.AreEqual(alice.AgencyId, AgencySystem.AgencyByPlayerName["Alice"]);
            Assert.AreEqual(bob.AgencyId, AgencySystem.AgencyByPlayerName["Bob"]);
            Assert.AreEqual("Alice", alice.OwningPlayerName);
        }

        [TestMethod]
        public void TryRenameAgencyOwner_EmptyOrWhitespaceNewName_FailsWithReason()
        {
            var alice = AgencySystem.RegisterAgency("Alice");

            Assert.IsFalse(AgencySystem.TryRenameAgencyOwner(alice, "", out var reason));
            StringAssert.Contains(reason, "non-empty");

            Assert.IsFalse(AgencySystem.TryRenameAgencyOwner(alice, "   ", out reason));
            StringAssert.Contains(reason, "non-empty");

            Assert.IsFalse(AgencySystem.TryRenameAgencyOwner(alice, null, out reason));
            StringAssert.Contains(reason, "non-empty");

            // No state mutation under failure.
            Assert.AreEqual("Alice", alice.OwningPlayerName);
        }

        [TestMethod]
        public void TryRenameAgencyOwner_NullSourceState_FailsDefensively()
        {
            Assert.IsFalse(AgencySystem.TryRenameAgencyOwner(null, "Bob", out var reason));
            StringAssert.Contains(reason, "null");
        }

        [TestMethod]
        public void TryRenameAgencyOwner_GateOff_FailsWithReason()
        {
            var alice = AgencySystem.RegisterAgency("Alice");
            GameplaySettings.SettingsStore.PerAgencyCareer = false;

            Assert.IsFalse(AgencySystem.TryRenameAgencyOwner(alice, "Bob", out var reason));
            StringAssert.Contains(reason, "Per-agency career is not active");
        }

        [TestMethod]
        public void TryRenameAgencyOwner_DiskRoundTripsOnReload()
        {
            // Persistence-before-index ordering: a fresh AgencySystem.Reset +
            // LoadExistingAgencies after the rename must pick up the renamed
            // owner from disk, not a stale registry image.
            var alice = AgencySystem.RegisterAgency("Alice");
            AgencySystem.TryRenameAgencyOwner(alice, "Bob", out _);

            AgencySystem.Reset();
            AgencySystem.LoadExistingAgencies();

            Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue("Bob", out var idAfter));
            Assert.AreEqual(alice.AgencyId, idAfter);
            Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("Alice"),
                "old owner name must not survive in the rebuilt index — disk is canonical.");
        }

        [TestMethod]
        public void TryResolveAgencyToken_GuidParseMisses_DoesNotFallThroughToNameLookup()
        {
            // Server-systems-review v1 SS-1: the resolver commits to the Guid path
            // on a successful Guid.TryParse. A Guid that parses but misses the
            // registry returns FALSE; it does NOT fall through to a name lookup
            // that might silently match a hex-string-shaped LMP handle. Without
            // this commitment, an operator with a player named after some random
            // Guid + a typo'd target agency id would silently mutate the wrong
            // agency.
            var alice = AgencySystem.RegisterAgency("Alice");
            var nonexistentButValidGuid = Guid.NewGuid();
            Assert.AreNotEqual(alice.AgencyId, nonexistentButValidGuid);

            // Register a player whose LMP handle is the hex form of the non-existent
            // Guid. If the resolver falls through, it would match this player's
            // agency — silently mutating the wrong agency from the operator's POV.
            var shadow = AgencySystem.RegisterAgency(nonexistentButValidGuid.ToString("N"));
            Assert.IsNotNull(shadow);

            Assert.IsFalse(AgencySystem.TryResolveAgencyToken(nonexistentButValidGuid.ToString("N"), out var resolved),
                "Guid form that parses + misses MUST NOT fall through to the name lookup.");
            Assert.IsNull(resolved);
        }
    }
}
