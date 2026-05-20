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
using System.Text;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the 2026-05-20 <c>EnsureStartTechSeeded</c> fix that closes the
    /// per-agency starter-parts gap. KSP auto-unlocks the <c>start</c> Tech node at
    /// Career-game-creation time without going through the wire path
    /// <see cref="AgencyTechRouter.TryRoute"/> intercepts, so a fresh agency under
    /// <see cref="AgencySystem.PerAgencyEnabled"/> ends up with empty
    /// <see cref="AgencyState.TechNodes"/> and no basic parts on first launch.
    /// The helper backfills <c>start</c> from the shared
    /// <c>ResearchAndDevelopment</c> scenario on both the new-mint path
    /// (<see cref="AgencySystem.RegisterAgency"/>) and the load path
    /// (<see cref="AgencySystem.LoadAgency"/>, including boot-time
    /// <see cref="AgencySystem.LoadExistingAgencies"/>).
    /// </summary>
    [TestClass]
    public class AgencyStartTechSeedingTest
    {
        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(),
                "lmp-startechseed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            GameplaySettings.SettingsStore.StartingFunds = 25_000f;
            GameplaySettings.SettingsStore.StartingScience = 10f;
            GameplaySettings.SettingsStore.StartingReputation = 5f;

            AgencySystem.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
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

        /// <summary>
        /// Inject a minimal `ResearchAndDevelopment` scenario containing a stock-shape
        /// `start` Tech node with the typical Career starter parts. Mirrors what
        /// `ScenarioStoreSystem.LoadExistingScenarios` would have produced for a fresh
        /// Career universe — the actual production input shape, not a hand-rolled
        /// fixture.
        /// </summary>
        private static void SeedSharedScenarioWithStartNode(params string[] parts)
        {
            var rd = new ConfigNode("") { Name = "ResearchAndDevelopment" };
            rd.CreateValue(new CfgNodeValue<string, string>("sci", "0"));

            var start = new ConfigNode("") { Name = "Tech" };
            start.CreateValue(new CfgNodeValue<string, string>("id", "start"));
            start.CreateValue(new CfgNodeValue<string, string>("state", "Available"));
            start.CreateValue(new CfgNodeValue<string, string>("cost", "0"));
            foreach (var p in parts)
                start.CreateValue(new CfgNodeValue<string, string>("part", p));

            rd.AddNode(start);

            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = rd;
        }

        [TestMethod]
        public void RegisterAgency_UnderPerAgencyCareer_SeedsStartTechAndParts()
        {
            SeedSharedScenarioWithStartNode("mk1pod", "parachuteSingle", "basicFin");

            var state = AgencySystem.RegisterAgency("Majestic95");

            Assert.IsNotNull(state);
            Assert.IsTrue(state.TechNodes.ContainsKey("start"),
                "freshly-minted agency must have 'start' tech seeded from shared scenario");

            var entry = state.TechNodes["start"];
            Assert.AreEqual("start", entry.TechId);
            Assert.IsTrue(entry.NumBytes > 0 && entry.Data.Length >= entry.NumBytes);

            // Bytes round-trip — re-parse and confirm the start payload is recognisable.
            var decoded = Encoding.UTF8.GetString(entry.Data, 0, entry.NumBytes);
            StringAssert.Contains(decoded, "id = start");
            StringAssert.Contains(decoded, "part = mk1pod");

            // Format guard — the initial 2026-05-20 build serialised via
            // startTech.ToString() which (on a child Tech ConfigNode) included the
            // outer "Tech\n{...\n}" wrapper. The projector then double-wrapped
            // those bytes with "Tech" and KSP's GetValue("id") returned null —
            // start parts silently invisible. The bare key=value format below
            // matches the basicRocketry/etc. router-written shape exactly.
            Assert.IsFalse(decoded.StartsWith("Tech", StringComparison.Ordinal),
                "seed bytes must NOT include the outer 'Tech' wrapper — that's the v0 bug that " +
                "produced double-wrapping when the projector re-wrapped via ParseClientConfigNode. " +
                "Decoded bytes:\n" + decoded);

            // Round-trip through ParseClientConfigNode (the projector's parse path)
            // to assert the result is a Tech node whose top-level id is "start".
            // This is the wire-shape contract the rest of the projector relies on.
            var parsed = ScenarioDataUpdater.ParseClientConfigNode(entry.Data, entry.NumBytes, "Tech");
            Assert.AreEqual("start", parsed.GetValue("id")?.Value,
                "round-tripped Tech node's top-level id must be 'start' — if this fails, the projector " +
                "splice produces a Tech node KSP cannot recognise + start parts go unavailable to the player.");

            // PurchasedParts seeded with every part from the start node so the player
            // doesn't need to spend funds to use them on first launch.
            Assert.IsTrue(state.PurchasedParts.TryGetValue("start", out var partSet));
            CollectionAssert.AreEquivalent(
                new[] { "mk1pod", "parachuteSingle", "basicFin" },
                partSet.ToArray());
        }

        [TestMethod]
        public void RegisterAgency_SeededState_PersistsThroughSaveLoad()
        {
            SeedSharedScenarioWithStartNode("mk1pod", "parachuteSingle");

            var registered = AgencySystem.RegisterAgency("Majestic95");
            var savedId = registered.AgencyId;

            // Wipe in-memory state — disk file remains; this is the "server restart"
            // simulation. The R&D-keep-across-restart contract that drove this fix
            // (user concern 2026-05-20) is the load-path round-trip below.
            AgencySystem.Reset();

            var reloaded = AgencySystem.LoadAgency(savedId);

            Assert.IsNotNull(reloaded);
            Assert.IsTrue(reloaded.TechNodes.ContainsKey("start"),
                "reloaded agency must STILL have 'start' tech — seeded bytes survive disk round-trip");
            Assert.IsTrue(reloaded.PurchasedParts.TryGetValue("start", out var partSet));
            CollectionAssert.AreEquivalent(new[] { "mk1pod", "parachuteSingle" }, partSet.ToArray());
        }

        [TestMethod]
        public void LoadAgency_PreSeedFileWithoutStart_BackfillsOnLoad()
        {
            // Build an agency file with TechNodes that lacks 'start' — mimics the
            // Melaus situation from the live soak that triggered this fix.
            SeedSharedScenarioWithStartNode("mk1pod", "parachuteSingle");
            var preFix = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "Melaus",
                DisplayName = "Melaus Space Agency",
                Funds = 0d,
                Science = 0d,
                Reputation = 0d,
            };
            // Pre-populate a NON-start tech node so we can confirm backfill ADDS
            // start without mutating existing entries.
            preFix.TechNodes["basicRocketry"] = new AgencyTechNodeEntry
            {
                TechId = "basicRocketry",
                Data = Encoding.UTF8.GetBytes("id = basicRocketry\ncost = 5\nstate = Available"),
                NumBytes = 0,
            };
            preFix.TechNodes["basicRocketry"].NumBytes = preFix.TechNodes["basicRocketry"].Data.Length;
            File.WriteAllText(preFix.FilePath, preFix.Serialize());

            var loaded = AgencySystem.LoadAgency(preFix.AgencyId);

            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.TechNodes.ContainsKey("start"),
                "load path must backfill the start tech seed for pre-fix agencies");
            Assert.IsTrue(loaded.TechNodes.ContainsKey("basicRocketry"),
                "backfill must not disturb pre-existing tech entries");
            Assert.IsTrue(loaded.PurchasedParts.TryGetValue("start", out var partSet));
            CollectionAssert.AreEquivalent(new[] { "mk1pod", "parachuteSingle" }, partSet.ToArray());
        }

        [TestMethod]
        public void LoadAgency_PreSeedFileWithoutStart_PersistsBackfillToDisk()
        {
            // Regression test for the review-caught load-path SaveAgency no-op:
            // SaveAgency short-circuits when the state isn't yet in the Agencies
            // dictionary, and both LoadAgencyFromFile callers add to Agencies
            // AFTER it returns. The fix uses an inline FileHandler.WriteAtomic
            // (matching the heal-on-bak block) so the seed reaches disk on the
            // load path the same way it does on the RegisterAgency path. Without
            // this assertion, the existing BackfillsOnLoad test gave false
            // confidence — the in-memory state was correct but every server
            // reboot would silently redo the seeding for the same agency.
            SeedSharedScenarioWithStartNode("mk1pod", "parachuteSingle");
            var preFix = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "Melaus",
                DisplayName = "Melaus Space Agency",
                Funds = 0d,
                Science = 0d,
                Reputation = 0d,
            };
            File.WriteAllText(preFix.FilePath, preFix.Serialize());
            var preFixDiskContent = File.ReadAllText(preFix.FilePath);
            Assert.IsFalse(preFixDiskContent.Contains("Id = start"),
                "precondition: disk file must not yet contain a start tech entry");
            Assert.IsFalse(preFixDiskContent.Contains("Part = mk1pod"),
                "precondition: disk file must not yet contain the start parts list");

            var loaded = AgencySystem.LoadAgency(preFix.AgencyId);

            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.TechNodes.ContainsKey("start"),
                "in-memory state must carry the seeded start tech post-load");

            // The disk file must ALSO now reflect the seed — otherwise next reboot
            // re-runs the backfill against the same pre-fix file forever.
            //
            // The on-disk format renders TechNodes entries as `TECH { Id = ...; Data
            // = <base64> }` inside a TECHTREE root, and PurchasedParts entries as
            // `TECH { Id = ...; Part = ... }` inside a PURCHASED_PARTS root. We
            // assert both surfaces — the tech node itself (via the unique start-id
            // marker) and the part list (which is the load-bearing payload for the
            // VAB "Unavailable Experimental Parts" fix).
            //
            // Use Assert.IsTrue + .Contains() instead of StringAssert.Contains: the
            // 3-arg overload's message is treated as a composite format string by
            // some MSTest builds, and the haystack here contains literal `{` / `}`
            // braces from the ConfigNode wire format which crash String.Format.
            var postFixDiskContent = File.ReadAllText(preFix.FilePath);
            Assert.IsTrue(postFixDiskContent.Contains("Id = start"),
                "disk file must be persisted with the backfilled start tech entry");
            Assert.IsTrue(postFixDiskContent.Contains("Part = mk1pod"),
                "disk file must carry the seeded part list");
        }

        [TestMethod]
        public void EnsureStartTechSeeded_AlreadyPresent_IsIdempotent()
        {
            SeedSharedScenarioWithStartNode("mk1pod", "parachuteSingle");

            var registered = AgencySystem.RegisterAgency("Majestic95");
            var originalBytes = registered.TechNodes["start"].Data;

            // Mutate the seeded entry to a sentinel so a second seed call would
            // visibly overwrite. RegisterAgency for the same player short-circuits
            // and returns the existing instance; the helper inside still runs the
            // idempotency guard. We assert the sentinel survives — proving the
            // ContainsKey("start") guard short-circuits.
            registered.TechNodes["start"].Data = Encoding.UTF8.GetBytes("sentinel");
            registered.TechNodes["start"].NumBytes = "sentinel".Length;

            var second = AgencySystem.RegisterAgency("Majestic95");
            Assert.AreSame(registered, second);
            Assert.AreEqual("sentinel",
                Encoding.UTF8.GetString(second.TechNodes["start"].Data, 0, second.TechNodes["start"].NumBytes),
                "second RegisterAgency on existing seeded agency must not re-overwrite TechNodes[start]");
        }

        [TestMethod]
        public void EnsureStartTechSeeded_SharedScenarioMissing_NoOp()
        {
            // No SeedSharedScenarioWithStartNode call — CurrentScenarios is empty.
            var state = AgencySystem.RegisterAgency("Majestic95");

            Assert.IsNotNull(state, "RegisterAgency must succeed even when start seed cannot be sourced");
            Assert.IsFalse(state.TechNodes.ContainsKey("start"),
                "no shared scenario → no seed; helper defensively warns + returns");
            // Persistence still succeeded (the bare agency is on disk).
            Assert.IsTrue(File.Exists(state.FilePath));
        }

        [TestMethod]
        public void EnsureStartTechSeeded_SharedScenarioPresentButNoStartChild_NoOp()
        {
            // Inject ResearchAndDevelopment but with only a non-start Tech (e.g.
            // an operator-hand-edited universe where start was deleted).
            var rd = new ConfigNode("") { Name = "ResearchAndDevelopment" };
            var nonStart = new ConfigNode("") { Name = "Tech" };
            nonStart.CreateValue(new CfgNodeValue<string, string>("id", "basicRocketry"));
            nonStart.CreateValue(new CfgNodeValue<string, string>("state", "Available"));
            nonStart.CreateValue(new CfgNodeValue<string, string>("cost", "5"));
            rd.AddNode(nonStart);
            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = rd;

            var state = AgencySystem.RegisterAgency("Majestic95");
            Assert.IsFalse(state.TechNodes.ContainsKey("start"));
            Assert.IsFalse(state.PurchasedParts.ContainsKey("start"));
        }

        [TestMethod]
        public void LoadAgency_PreFixWithRouterAddedTech_BackfillPreservesBoth()
        {
            // Mirrors the actual Melaus production trajectory: an agency with
            // router-written tech entries (basicRocketry was unlocked normally
            // via ShareProgressTechnologyMsgData → AgencyTechRouter.TryRoute)
            // but missing the auto-unlocked `start` node. Pins the interaction
            // between the router's prior writes and the load-time backfill.
            SeedSharedScenarioWithStartNode("mk1pod", "parachuteSingle");

            // First: register an agency normally + add a router-style tech entry
            // by hand to simulate post-mint pre-fix state. This goes through
            // RegisterAgency (which under the fix DOES seed start), so manually
            // strip the start entry to recreate the Melaus situation precisely.
            var registered = AgencySystem.RegisterAgency("MelausLikePlayer");
            var savedId = registered.AgencyId;
            registered.TechNodes["basicRocketry"] = new AgencyTechNodeEntry
            {
                TechId = "basicRocketry",
                Data = Encoding.UTF8.GetBytes("id = basicRocketry\ncost = 5\nstate = Available\npart = fuelTankSmallFlat"),
                NumBytes = 0,
            };
            registered.TechNodes["basicRocketry"].NumBytes = registered.TechNodes["basicRocketry"].Data.Length;
            registered.TechNodes.Remove("start");                       // simulate pre-fix
            registered.PurchasedParts.Remove("start");                  // simulate pre-fix
            AgencySystem.SaveAgency(savedId);

            // Wipe in-memory state — disk file has basicRocketry but no start.
            AgencySystem.Reset();

            var reloaded = AgencySystem.LoadAgency(savedId);

            Assert.IsNotNull(reloaded);
            Assert.IsTrue(reloaded.TechNodes.ContainsKey("start"),
                "backfill must add start on load");
            Assert.IsTrue(reloaded.TechNodes.ContainsKey("basicRocketry"),
                "backfill must preserve the pre-existing router-written tech entry");

            // Round-trip the router-written entry to confirm content unchanged.
            var basicBytes = Encoding.UTF8.GetString(
                reloaded.TechNodes["basicRocketry"].Data, 0, reloaded.TechNodes["basicRocketry"].NumBytes);
            StringAssert.Contains(basicBytes, "part = fuelTankSmallFlat",
                "router-written tech entry must round-trip unchanged through backfill");

            // PurchasedParts: start was added; basicRocketry's parts (if any
            // were tracked separately) remain undisturbed. In this fixture we
            // only assert that start was seeded — basicRocketry's part list
            // lives inside its Tech bytes, not in PurchasedParts.
            Assert.IsTrue(reloaded.PurchasedParts.TryGetValue("start", out var startParts));
            CollectionAssert.AreEquivalent(new[] { "mk1pod", "parachuteSingle" }, startParts.ToArray());
        }
    }
}
