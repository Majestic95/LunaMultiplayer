using System;
using System.Collections.Generic;
using LmpClient.Harmony;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpClientTest
{
    /// <summary>
    /// Phase 1.5 R0 — pure decision-math coverage for
    /// <see cref="ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned{T}"/>.
    ///
    /// The production callsite hands real <c>Vessel</c> instances to the
    /// helper plus closures over <c>LockSystem.LockQuery.UpdateLockBelongsToPlayer</c>
    /// and <c>SettingsSystem.CurrentSettings.PlayerName</c>. None of that is
    /// reachable from a pure-net472 test process — the helper is generic over
    /// the element type and parameterised on its lock-check predicate exactly
    /// so we can drive it with a lightweight test record + an in-memory map.
    /// This is the same "extract to public static for testability" pattern
    /// established by <c>WarpSystem.ShouldSteadyStateRetry</c> (BUG-051b),
    /// <c>VesselPositionUpdate.ComputeMaxInterpolationDuration</c>
    /// (BUG-003/004), and <c>PqsAlignmentRoutine.NeedsRealignment</c>
    /// (BUG-008 Phase A).
    ///
    /// What these tests pin:
    /// <list type="bullet">
    ///   <item>Predicate truth table across local-holder / remote-holder /
    ///         no-holder / null-element / Guid.Empty cases.</item>
    ///   <item>"No lock owner ≠ permission" — an entry with no lock holder
    ///         is removed, not preserved. The transient-unlocked window
    ///         that LMP's aggressive lock issuance produces must not become
    ///         a free-pump window.</item>
    ///   <item>In-place mutation contract — the helper writes through to the
    ///         caller's list reference; no new list is allocated.</item>
    ///   <item>Predicate invocation count — pinned to one call per surviving
    ///         predicate check so the hot path (FixedUpdate × consumer count
    ///         × warehouse count) doesn't accidentally regress to O(n²).</item>
    /// </list>
    ///
    /// Per-agency relevance: under <c>PerAgencyCareer=true</c>, Stage 5.17a
    /// already structurally enforces "Update lock holder ≡ owning-agency
    /// member" — the predicate is therefore equivalent to a per-agency
    /// boundary check under that gate, and these test cases double as
    /// per-agency boundary coverage when the predicate is wired to the
    /// real <c>LockSystem.LockQuery</c> in production.
    /// </summary>
    [TestClass]
    public class MksR0DepotFilterTest
    {
        /// <summary>
        /// Lightweight stand-in for KSP's <c>Vessel</c>. Carries only the
        /// fields the filter consults (id) plus a stable display value for
        /// debugging failed asserts.
        /// </summary>
        private sealed class TestVessel
        {
            public Guid Id;
            public string DebugName;
            public override string ToString() { return DebugName + "(" + Id + ")"; }
        }

        private static TestVessel V(string name, Guid id)
        {
            return new TestVessel { Id = id, DebugName = name };
        }

        // The helper takes (item -> Guid) and (Guid -> bool). The first projects
        // the id; the second answers "is the local player holding Update on
        // this vessel id?". For tests we drive both with simple lambdas over
        // an in-memory set.
        private static Func<TestVessel, Guid> Id { get { return v => v == null ? Guid.Empty : v.Id; } }

        [TestMethod]
        public void Filter_NullList_NoThrow()
        {
            // Defensive: a Harmony postfix can run after the original method
            // returned null (e.g. mod-mod interaction earlier in the postfix
            // chain). The helper must short-circuit, not NRE.
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned<TestVessel>(
                null, Id, _ => true);
            // Test passes if no exception. Asserting reach-of-line:
            Assert.IsTrue(true);
        }

        [TestMethod]
        public void Filter_EmptyList_StaysEmpty()
        {
            var list = new List<TestVessel>();
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(list, Id, _ => true);
            Assert.AreEqual(0, list.Count);
        }

        [TestMethod]
        public void Filter_SingleLocalVessel_Preserved()
        {
            // The local player holds Update on their own vessel by definition —
            // USI's "consume from yourself first" path must remain intact.
            var alice = Guid.NewGuid();
            var list = new List<TestVessel> { V("alice-craft", alice) };
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(
                list, Id, id => id == alice);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(alice, list[0].Id);
        }

        [TestMethod]
        public void Filter_SingleRemoteVessel_Removed()
        {
            // The core of the R0 fix: a vessel whose Update lock is held by
            // a remote player must be removed so MKS' downstream FetchResources
            // never writes pr.amount on it.
            var bob = Guid.NewGuid();
            var list = new List<TestVessel> { V("bob-warehouse", bob) };
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(
                list, Id, _ => false);
            Assert.AreEqual(0, list.Count);
        }

        [TestMethod]
        public void Filter_MixedList_OnlyLocalSurvives()
        {
            // Realistic mid-game scene: own craft + remote warehouse +
            // remote craft all in physics range. Only the local Update-lock
            // holder survives the postfix.
            var alice = Guid.NewGuid();
            var bob = Guid.NewGuid();
            var carol = Guid.NewGuid();
            var locallyHeld = new HashSet<Guid> { alice };

            var list = new List<TestVessel>
            {
                V("alice-craft", alice),
                V("bob-warehouse", bob),
                V("carol-warehouse", carol),
            };
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(
                list, Id, id => locallyHeld.Contains(id));

            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(alice, list[0].Id);
        }

        [TestMethod]
        public void Filter_UnownedVessel_Removed()
        {
            // "No lock owner ≠ permission": a vessel whose Update lock does
            // not exist must be removed, not preserved. LockQuery's
            // UpdateLockBelongsToPlayer returns false in that case — this
            // test pins that we honor that return value as "not me".
            var orphan = Guid.NewGuid();
            var list = new List<TestVessel> { V("orphan", orphan) };
            // The predicate returns false for every id (simulates "lock entry absent"
            // — same return value as UpdateLockBelongsToPlayer's lock-missing path).
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(
                list, Id, _ => false);
            Assert.AreEqual(0, list.Count);
        }

        [TestMethod]
        public void Filter_GuidEmptyVessel_Removed()
        {
            // The Guid.Empty short-circuit is explicit in the helper — a vessel
            // without a valid id can't be looked up in LockStore.UpdateLocks.
            // We pin the early-exit so the predicate doesn't even get invoked
            // for empty ids (Filter_PredicateInvokedOncePerNonEmpty covers
            // the invocation-count side of the same contract).
            var predicateCalls = 0;
            var list = new List<TestVessel> { V("empty-id", Guid.Empty) };
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(
                list, Id, _ => { predicateCalls++; return true; });
            Assert.AreEqual(0, list.Count);
            Assert.AreEqual(0, predicateCalls, "Guid.Empty must short-circuit before invoking the lock predicate.");
        }

        [TestMethod]
        public void Filter_NullElementInList_Removed()
        {
            // KSP collection types in modded scenes occasionally yield null
            // entries (e.g. a Vessel just unloaded mid-iteration on the calling
            // thread). The helper must treat null as "remove", not NRE.
            var alice = Guid.NewGuid();
            var list = new List<TestVessel> { V("alice", alice), null, V("alice2", alice) };
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(
                list, Id, _ => true);
            Assert.AreEqual(2, list.Count, "Null entry should be removed; the two valid entries should remain.");
            Assert.IsNotNull(list[0]);
            Assert.IsNotNull(list[1]);
        }

        [TestMethod]
        public void Filter_AllLocal_Preserved()
        {
            // No-op case for the postfix — every nearby warehouse already
            // belongs to the local player (a "solo player physics bubble").
            // List must be left unchanged in both length and order.
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var c = Guid.NewGuid();
            var list = new List<TestVessel>
            {
                V("a", a),
                V("b", b),
                V("c", c),
            };
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(
                list, Id, _ => true);
            Assert.AreEqual(3, list.Count);
            Assert.AreEqual(a, list[0].Id);
            Assert.AreEqual(b, list[1].Id);
            Assert.AreEqual(c, list[2].Id);
        }

        [TestMethod]
        public void Filter_AllRemote_Empty()
        {
            // Hostile-case bookend: every nearby warehouse is held by someone
            // else. Local player gets nothing — which is correct; their
            // consumer should idle until they take a lock or land their own
            // warehouse.
            var list = new List<TestVessel>
            {
                V("bob", Guid.NewGuid()),
                V("carol", Guid.NewGuid()),
                V("dave", Guid.NewGuid()),
            };
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(
                list, Id, _ => false);
            Assert.AreEqual(0, list.Count);
        }

        [TestMethod]
        public void Filter_PredicateInvokedOncePerNonEmptyNonNullEntry()
        {
            // Hot-path perf contract: the predicate (which dispatches into
            // LockSystem.LockQuery in production) must fire exactly once per
            // non-null, non-empty-id entry. A regression to O(n²) — e.g. by
            // calling RemoveAll twice or by re-querying inside the iteration —
            // would compound at FixedUpdate cadence × N consumers × M warehouses.
            // Null entries and Guid.Empty entries are short-circuited and
            // must NOT invoke the predicate.
            var alice = Guid.NewGuid();
            var bob = Guid.NewGuid();
            var carol = Guid.NewGuid();
            var locallyHeld = new HashSet<Guid> { alice };

            var list = new List<TestVessel>
            {
                V("alice", alice),
                null,                          // short-circuit, no predicate call
                V("empty", Guid.Empty),        // short-circuit, no predicate call
                V("bob", bob),
                V("carol", carol),
            };

            var callCount = 0;
            var calledFor = new List<Guid>();
            ModuleLogisticsConsumer_DepotListPostfix.FilterToLocallyOwned(
                list, Id, id => { callCount++; calledFor.Add(id); return locallyHeld.Contains(id); });

            Assert.AreEqual(3, callCount, "Predicate must fire exactly once per valid id (alice, bob, carol). Null and Guid.Empty short-circuit.");
            CollectionAssert.AreEquivalent(new List<Guid> { alice, bob, carol }, calledFor);
            Assert.AreEqual(1, list.Count, "Only alice survives.");
            Assert.AreEqual(alice, list[0].Id);
        }
    }
}
