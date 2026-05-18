using System;
using System.Collections.Generic;
using LmpClient.Systems.Warp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpClientTest
{
    /// <summary>
    /// Phase 2 / MKS-R1 — pure decision-math coverage for
    /// <see cref="KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace{TVessel}"/>.
    ///
    /// Same "extract to public static for testability" pattern as MKS-R0's
    /// <c>FilterToLocallyOwned&lt;T&gt;</c>, plus the earlier
    /// <c>WarpSystem.ShouldSteadyStateRetry</c> (BUG-051b),
    /// <c>VesselPositionUpdate.ComputeMaxInterpolationDuration</c>
    /// (BUG-003/004), and <c>PqsAlignmentRoutine.NeedsRealignment</c>
    /// (BUG-008 Phase A).
    ///
    /// The production callsite hands real KSP <c>Vessel</c> instances + a
    /// closure over <c>LockSystem.LockQuery.GetUpdateLockOwner</c>, plus
    /// <c>WarpSystem.Subspaces</c> / <c>GetPlayerSubspace</c>. None of that is
    /// reachable from pure-net472 test code, so the helper is parameterised on
    /// each lookup and the test drives them via in-memory maps.
    ///
    /// What these tests pin:
    /// <list type="bullet">
    ///   <item>Predicate truth table per loaded vessel — kolony anchor /
    ///         foreign update lock / tracked subspace / known subspace
    ///         time.</item>
    ///   <item>Tie-break by ordinal owner-name sort so soak logs are
    ///         reproducible.</item>
    ///   <item>"Most advanced wins" — the picker returns the foreign owner
    ///         whose subspace has the highest <c>subspaceTimes</c> value.</item>
    ///   <item>Defensive null-input handling — the routine fires on a 1s
    ///         cadence during scene transitions where dictionaries / lists
    ///         can be transiently null.</item>
    /// </list>
    ///
    /// Per-agency relevance: under <c>PerAgencyCareer=true</c>, Stage 5.17a
    /// rejects cross-agency Update-lock acquires, so a "foreign Update-lock
    /// holder" is equivalent to "foreign agency member" for stamped vessels.
    /// These test cases double as per-agency boundary coverage when the
    /// predicate is wired to the real <c>LockSystem.LockQuery</c> in
    /// production. For Unassigned-sentinel vessels the boundary collapses to
    /// raw lock ownership — also correct.
    /// </summary>
    [TestClass]
    public class Phase2KolonySubspaceJoinDecisionTest
    {
        /// <summary>
        /// Lightweight stand-in for KSP's <c>Vessel</c>. Carries only the
        /// fields the decision math reads (id), plus a debug name + an
        /// MKS-anchor flag so tests can drive the predicate without
        /// constructing real <c>Part</c> / <c>PartModule</c> chains.
        /// </summary>
        private sealed class TestVessel
        {
            public Guid Id;
            public bool HasMks;
            public string DebugName;
            public override string ToString() { return DebugName + "(" + Id + ")"; }
        }

        private static TestVessel V(string name, Guid id, bool hasMks)
        {
            return new TestVessel { Id = id, DebugName = name, HasMks = hasMks };
        }

        // Closures shared across tests — the helper takes them as parameters
        // so production can wire the real KSP/LMP singletons while tests use
        // in-memory maps.
        private static Func<TestVessel, Guid> IdProj => v => v == null ? Guid.Empty : v.Id;
        private static Func<TestVessel, bool> MksProj => v => v != null && v.HasMks;

        [TestMethod]
        public void Decide_NullList_ReturnsNone()
        {
            // Routine fires every 1s; FlightGlobals.VesselsLoaded could be
            // null mid-scene-transition. Helper must not NRE.
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace<TestVessel>(
                Guid.NewGuid(),
                null,
                IdProj,
                MksProj,
                "Alice",
                _ => "Bob",
                _ => 1,
                new Dictionary<int, double>());
            Assert.AreEqual(0, result.SubspaceId);
            Assert.IsNull(result.PlayerName);
        }

        [TestMethod]
        public void Decide_EmptyList_ReturnsNone()
        {
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace<TestVessel>(
                Guid.NewGuid(),
                new List<TestVessel>(),
                IdProj,
                MksProj,
                "Alice",
                _ => "Bob",
                _ => 1,
                new Dictionary<int, double> { { 1, 100d } });
            Assert.AreEqual(0, result.SubspaceId);
            Assert.IsNull(result.PlayerName);
        }

        [TestMethod]
        public void Decide_NullSubspaceTimes_ReturnsNone()
        {
            // Pre-LocksSynced / pre-handshake window where Subspaces is empty.
            // Caller's gate normally blocks this, but the helper must still
            // be safe for the defensive case.
            var aliceId = Guid.NewGuid();
            var bobId = Guid.NewGuid();
            var list = new List<TestVessel> { V("alice-base", aliceId, true), V("bob-base", bobId, true) };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => vid == bobId ? "Bob" : "Alice",
                _ => 1,
                null);
            Assert.AreEqual(0, result.SubspaceId);
            Assert.IsNull(result.PlayerName);
        }

        [TestMethod]
        public void Decide_OnlyOwnActiveVessel_ReturnsNone()
        {
            // List contains only the local active vessel — no foreign target.
            var aliceId = Guid.NewGuid();
            var list = new List<TestVessel> { V("alice-base", aliceId, true) };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                _ => "Alice",
                _ => 5,
                new Dictionary<int, double> { { 5, 1000d } });
            Assert.AreEqual(0, result.SubspaceId);
            Assert.IsNull(result.PlayerName);
        }

        [TestMethod]
        public void Decide_ForeignVesselWithoutMksAnchor_ReturnsNone()
        {
            // Bob's vessel is in physics range but it's not a kolony anchor
            // (no MKSModule). Pins the "kolony anchor required" rule —
            // routine should NOT fire just because another player is nearby.
            var aliceId = Guid.NewGuid();
            var bobId = Guid.NewGuid();
            var list = new List<TestVessel>
            {
                V("alice-craft", aliceId, true),
                V("bob-rocket", bobId, hasMks: false), // no MKS anchor
            };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => vid == bobId ? "Bob" : "Alice",
                _ => 5,
                new Dictionary<int, double> { { 5, 1000d } });
            Assert.AreEqual(0, result.SubspaceId);
            Assert.IsNull(result.PlayerName);
        }

        [TestMethod]
        public void Decide_ForeignMksLocallyUpdateLocked_ReturnsNone()
        {
            // MKS vessel in range, but Alice somehow holds Update on it
            // (transferagency edge case or admin override). Pins the
            // "foreign Update lock required" rule.
            var aliceId = Guid.NewGuid();
            var bobAnchorId = Guid.NewGuid();
            var list = new List<TestVessel>
            {
                V("alice-craft", aliceId, true),
                V("bob-base-now-locked-by-alice", bobAnchorId, true),
            };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                _ => "Alice", // Alice holds every Update lock
                _ => 5,
                new Dictionary<int, double> { { 5, 1000d } });
            Assert.AreEqual(0, result.SubspaceId);
            Assert.IsNull(result.PlayerName);
        }

        [TestMethod]
        public void Decide_ForeignMksNoLockHolder_ReturnsNone()
        {
            // Vessel exists with MKS anchor but no Update lock holder
            // (transient between holders — LMP issues locks aggressively).
            // "No owner ≠ permission" — we don't snap to an orphan vessel.
            var aliceId = Guid.NewGuid();
            var anchorId = Guid.NewGuid();
            var list = new List<TestVessel> { V("alice", aliceId, true), V("orphan", anchorId, true) };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                _ => null, // no lock owner anywhere
                _ => 5,
                new Dictionary<int, double> { { 5, 1000d } });
            Assert.AreEqual(0, result.SubspaceId);
            Assert.IsNull(result.PlayerName);
        }

        [TestMethod]
        public void Decide_ForeignMksOwnerUntrackedSubspace_ReturnsNone()
        {
            // Owner exists but their subspace is 0 (unknown — they may be
            // mid-handshake or in solo subspace). Filter conservatively;
            // wait for next tick.
            var aliceId = Guid.NewGuid();
            var bobAnchorId = Guid.NewGuid();
            var list = new List<TestVessel> { V("alice", aliceId, true), V("bob-base", bobAnchorId, true) };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => vid == bobAnchorId ? "Bob" : null,
                _ => 0, // Bob's subspace unknown / pending
                new Dictionary<int, double> { { 5, 1000d } });
            Assert.AreEqual(0, result.SubspaceId);
            Assert.IsNull(result.PlayerName);
        }

        [TestMethod]
        public void Decide_ForeignMksOwnerMissingFromSubspaceTimesMap_ReturnsNone()
        {
            // Owner is in subspace 7, but our Subspaces dict doesn't carry
            // subspace 7's time offset (network desync, server hasn't broadcast
            // it yet). Defensive — skip rather than snap into an unknown UT.
            var aliceId = Guid.NewGuid();
            var bobAnchorId = Guid.NewGuid();
            var list = new List<TestVessel> { V("alice", aliceId, true), V("bob-base", bobAnchorId, true) };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => vid == bobAnchorId ? "Bob" : null,
                _ => 7,
                new Dictionary<int, double> { { 1, 100d }, { 5, 1000d } }); // no 7
            Assert.AreEqual(0, result.SubspaceId);
            Assert.IsNull(result.PlayerName);
        }

        [TestMethod]
        public void Decide_SingleForeignMksRemoteOwner_ReturnsThatSubspace()
        {
            // The main happy path: Bob has an MKS base, Bob holds Update,
            // Bob is in subspace 5 with UT 1000. Return (5, "Bob").
            var aliceId = Guid.NewGuid();
            var bobAnchorId = Guid.NewGuid();
            var list = new List<TestVessel> { V("alice", aliceId, true), V("bob-base", bobAnchorId, true) };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => vid == bobAnchorId ? "Bob" : null,
                playerName => playerName == "Bob" ? 5 : 0,
                new Dictionary<int, double> { { 5, 1000d } });
            Assert.AreEqual(5, result.SubspaceId);
            Assert.AreEqual("Bob", result.PlayerName);
        }

        [TestMethod]
        public void Decide_MultipleForeignKolonies_PicksMostAdvanced()
        {
            // Alice in subspace 5 (UT=1000), Bob in subspace 3 (UT=500),
            // Carol in subspace 7 (UT=2000). Return Carol's subspace.
            var aliceId = Guid.NewGuid();
            var bobAnchorId = Guid.NewGuid();
            var carolAnchorId = Guid.NewGuid();
            var davidAnchorId = Guid.NewGuid();
            var list = new List<TestVessel>
            {
                V("alice-craft", aliceId, true),
                V("bob-base", bobAnchorId, true),
                V("carol-base", carolAnchorId, true),
                V("david-base", davidAnchorId, true),
            };
            var ownerMap = new Dictionary<Guid, string>
            {
                { bobAnchorId, "Bob" }, { carolAnchorId, "Carol" }, { davidAnchorId, "David" },
            };
            var subspaceMap = new Dictionary<string, int>
            {
                { "Bob", 3 }, { "Carol", 7 }, { "David", 5 },
            };
            var times = new Dictionary<int, double>
            {
                { 3, 500d }, { 5, 1000d }, { 7, 2000d },
            };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => ownerMap.TryGetValue(vid, out var n) ? n : null,
                playerName => subspaceMap.TryGetValue(playerName, out var s) ? s : 0,
                times);
            Assert.AreEqual(7, result.SubspaceId);
            Assert.AreEqual("Carol", result.PlayerName);
        }

        [TestMethod]
        public void Decide_TieOnSubspaceTime_BreaksByOwnerNameOrdinal()
        {
            // Two foreign kolonies in subspaces with identical UT.
            // Deterministic tie-break: ordinal name sort, lowest wins.
            // ("Bob" < "Carol" in ordinal comparison.)
            var aliceId = Guid.NewGuid();
            var bobAnchorId = Guid.NewGuid();
            var carolAnchorId = Guid.NewGuid();
            var list = new List<TestVessel>
            {
                V("alice", aliceId, true),
                V("bob-base", bobAnchorId, true),
                V("carol-base", carolAnchorId, true),
            };
            var ownerMap = new Dictionary<Guid, string>
            {
                { bobAnchorId, "Bob" }, { carolAnchorId, "Carol" },
            };
            var subspaceMap = new Dictionary<string, int> { { "Bob", 3 }, { "Carol", 4 } };
            var times = new Dictionary<int, double> { { 3, 500d }, { 4, 500d } }; // tie
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => ownerMap.TryGetValue(vid, out var n) ? n : null,
                playerName => subspaceMap.TryGetValue(playerName, out var s) ? s : 0,
                times);
            Assert.AreEqual("Bob", result.PlayerName);
            Assert.AreEqual(3, result.SubspaceId);
        }

        [TestMethod]
        public void Decide_NullEntryInList_Skipped()
        {
            // Vessel collection occasionally yields null mid-iteration (KSP
            // can unload a vessel from another thread). Helper must skip
            // nulls without NRE.
            var aliceId = Guid.NewGuid();
            var bobAnchorId = Guid.NewGuid();
            var list = new List<TestVessel>
            {
                V("alice", aliceId, true),
                null,
                V("bob-base", bobAnchorId, true),
            };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => vid == bobAnchorId ? "Bob" : null,
                playerName => playerName == "Bob" ? 5 : 0,
                new Dictionary<int, double> { { 5, 1000d } });
            Assert.AreEqual(5, result.SubspaceId);
            Assert.AreEqual("Bob", result.PlayerName);
        }

        [TestMethod]
        public void Decide_GuidEmptyId_Skipped()
        {
            // Vessel with Guid.Empty (never been stamped) — can't be looked
            // up in LockStore. Skip gracefully, don't snap to a phantom.
            var aliceId = Guid.NewGuid();
            var bobAnchorId = Guid.NewGuid();
            var list = new List<TestVessel>
            {
                V("alice", aliceId, true),
                V("ghost", Guid.Empty, true),
                V("bob-base", bobAnchorId, true),
            };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => vid == bobAnchorId ? "Bob" : null,
                playerName => playerName == "Bob" ? 5 : 0,
                new Dictionary<int, double> { { 5, 1000d } });
            Assert.AreEqual(5, result.SubspaceId);
            Assert.AreEqual("Bob", result.PlayerName);
        }

        [TestMethod]
        public void Decide_OneEligibleAmongDecoys_PicksTheEligible()
        {
            // Mixed list: foreign-MKS-no-owner + foreign-no-MKS + local-MKS
            // + foreign-MKS-eligible. Pin that the decision math correctly
            // filters through all the disqualifier branches and finds the
            // single eligible.
            var aliceId = Guid.NewGuid();
            var ownedByLocalMksId = Guid.NewGuid();
            var foreignNoMksId = Guid.NewGuid();
            var foreignMksOrphanId = Guid.NewGuid();
            var foreignMksOwnedId = Guid.NewGuid();
            var list = new List<TestVessel>
            {
                V("alice-craft", aliceId, true),
                V("alice-rover", ownedByLocalMksId, true),
                V("bob-rocket-no-mks", foreignNoMksId, false),
                V("orphan-base", foreignMksOrphanId, true),
                V("bob-eligible-base", foreignMksOwnedId, true),
            };
            var owners = new Dictionary<Guid, string>
            {
                { ownedByLocalMksId, "Alice" },
                { foreignNoMksId, "Bob" },
                { foreignMksOrphanId, null },
                { foreignMksOwnedId, "Bob" },
            };
            var result = KolonyProximityWarpRoutine.FindMostAdvancedForeignKolonySubspace(
                aliceId, list, IdProj, MksProj, "Alice",
                vid => owners.TryGetValue(vid, out var n) ? n : null,
                playerName => playerName == "Bob" ? 5 : 0,
                new Dictionary<int, double> { { 5, 1000d } });
            Assert.AreEqual(5, result.SubspaceId);
            Assert.AreEqual("Bob", result.PlayerName);
        }
    }
}
