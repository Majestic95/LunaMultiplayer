using LmpCommon.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Server;
using Server.System;
using System;
using System.Linq;

namespace ServerTest
{
    /// <summary>
    /// Phase 3 of server-side-offload — exercises <see cref="MessageQueuer.ShouldRelayPositionByCadence"/>,
    /// the pure helper that drives the per-vessel cadence throttle for unpiloted vessels.
    /// Pins the multiplier disable branch, the time-window arithmetic, AND the
    /// lock-present bypass (per <see cref="LmpCommon.Locks.LockQuery.ControlLockExists"/>) per
    /// the [[feedback-negative-assertions-lock-in-bugs]] discipline — every "X is throttled
    /// when condition is false" needs a paired "X is NOT throttled when condition is true"
    /// test, otherwise a refactor that breaks the lock check would not be caught.
    ///
    /// Spec: docs/research/11-server-side-offload-spec.md §5.
    /// </summary>
    [TestClass]
    public class CadenceThrottleTest
    {
        private static readonly Guid TestVesselId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        //Fixed Guid (not Guid.NewGuid) for determinism.

        [TestInitialize]
        public void Setup()
        {
            //Mirror LockSystemTest.Setup — LockSystem state is process-global and tests
            //don't have inherent isolation. Clear all locks so unrelated test state from
            //prior test runs doesn't leak into our throttle assertions.
            foreach (var l in LockSystem.LockQuery.GetAllLocks().ToList())
                LockSystem.ReleaseLock(l);
        }

        [TestCleanup]
        public void Teardown()
        {
            foreach (var l in LockSystem.LockQuery.GetAllLocks().ToList())
                LockSystem.ReleaseLock(l);
        }

        [TestMethod]
        public void ShouldRelayPositionByCadence_MultiplierOne_AlwaysTrue()
        {
            //Multiplier <= 1 is the "throttle off" gate. Operators set it to 1 to revert
            //to pre-Phase-3 behaviour. Must always relay regardless of nowMs / lastMs /
            //interval — otherwise the off-switch silently throttles.
            Assert.IsTrue(MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 0, nowMs: 0, secondaryIntervalMs: 150, multiplier: 1));
            Assert.IsTrue(MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 1000, nowMs: 1001, secondaryIntervalMs: 150, multiplier: 1));
            Assert.IsTrue(MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 0, nowMs: long.MaxValue, secondaryIntervalMs: 1, multiplier: 0));
            Assert.IsTrue(MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 0, nowMs: 0, secondaryIntervalMs: 1, multiplier: -5));
        }

        [TestMethod]
        public void ShouldRelayPositionByCadence_NoControlLock_FirstMessageRelays()
        {
            //First inbound for a vessel: LastRelayedPositionMs starts at 0 (struct default).
            //Even at minimum throttle window, (nowMs - 0) >= 150 trivially true — so first
            //message ALWAYS relays. Important: a stamp-the-timestamp side-effect must happen
            //at the caller (RelayPositionMessage), not the helper, so the timestamp drives
            //the gate-window for subsequent inbounds.
            var relayed = MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 0, nowMs: 1000, secondaryIntervalMs: 150, multiplier: 5);
            Assert.IsTrue(relayed);
        }

        [TestMethod]
        public void ShouldRelayPositionByCadence_NoControlLock_WithinThrottleWindow_False()
        {
            //Default operator dial: multiplier=5, secondaryInterval=150ms → 750ms window.
            //If only 100ms has passed since last relay, drop.
            var relayed = MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 10000, nowMs: 10100, secondaryIntervalMs: 150, multiplier: 5);
            Assert.IsFalse(relayed);
        }

        [TestMethod]
        public void ShouldRelayPositionByCadence_NoControlLock_AtThrottleBoundary_True()
        {
            //Exact boundary: nowMs - lastMs == minIntervalMs → relay. >= comparison, not >.
            //Pinning the boundary semantics prevents a future refactor from accidentally
            //flipping it to > and silently doubling effective throttle time.
            var relayed = MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 10000, nowMs: 10750, secondaryIntervalMs: 150, multiplier: 5);
            Assert.IsTrue(relayed);
        }

        [TestMethod]
        public void ShouldRelayPositionByCadence_NoControlLock_AfterThrottleWindow_True()
        {
            //Well past the throttle window — relay.
            var relayed = MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 10000, nowMs: 12000, secondaryIntervalMs: 150, multiplier: 5);
            Assert.IsTrue(relayed);
        }

        [TestMethod]
        public void ShouldRelayPositionByCadence_HighMultiplier_LongerWindow()
        {
            //Aggressive multiplier=20 at 150ms baseline = 3000ms window. 2999ms is too soon.
            var relayed = MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 0, nowMs: 2999, secondaryIntervalMs: 150, multiplier: 20);
            Assert.IsFalse(relayed);
            //3001ms passes.
            relayed = MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 0, nowMs: 3001, secondaryIntervalMs: 150, multiplier: 20);
            Assert.IsTrue(relayed);
        }

        [TestMethod]
        public void ShouldRelayPositionByCadence_OperatorTightenedSecondaryInterval_StillThrottles()
        {
            //If operator sets SecondaryVesselUpdatesMsInterval=50 (matching primary) with
            //multiplier=5 → 250ms window. Verify the multiplier-secondaryInterval product
            //is what drives the gate, not the default 150ms baseline.
            var relayed = MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 1000, nowMs: 1200, secondaryIntervalMs: 50, multiplier: 5);
            Assert.IsFalse(relayed);
            relayed = MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 1000, nowMs: 1260, secondaryIntervalMs: 50, multiplier: 5);
            Assert.IsTrue(relayed);
        }

        [TestMethod]
        public void ShouldRelayPositionByCadence_LongIdlePeriod_NoOverflow()
        {
            //Vessel had no inbound for a very long time (server uptime hours / days).
            //(nowMs - 0) is huge; the comparison uses long arithmetic so no int overflow.
            //Casting (long)secondaryIntervalMs * multiplier protects against the multiply
            //overflowing when operator sets multiplier near INT_MAX (defensive but cheap).
            var relayed = MessageQueuer.ShouldRelayPositionByCadence(TestVesselId, lastRelayedMs: 0, nowMs: long.MaxValue / 2, secondaryIntervalMs: 150, multiplier: 5);
            Assert.IsTrue(relayed);
        }

        [TestMethod]
        public void ShouldRelayPositionByCadence_ControlLockHeld_BypassesThrottle()
        {
            //[[feedback-negative-assertions-lock-in-bugs]] discipline: paired positive
            //assertion to the throttle's "lock-absent → throttled" cases above. Acquire
            //Control lock for TestVesselId → ShouldRelayPositionByCadence MUST return
            //true even well inside the throttle window. Without this test, a refactor
            //that accidentally inverts the LockQuery.ControlLockExists check (typo:
            //flipping ! or removing it) would not be caught by the no-lock branch tests.
            var lockDef = new LockDefinition(LockType.Control, "throttle-test-pilot", TestVesselId);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _),
                "Test harness pre-condition: expected to acquire fresh Control lock on a unique vessel id");

            //Throttle window deeply not yet elapsed (10ms into a 750ms window) — would
            //normally drop, but the lock-present branch overrides.
            var relayed = MessageQueuer.ShouldRelayPositionByCadence(
                TestVesselId, lastRelayedMs: 10000, nowMs: 10010, secondaryIntervalMs: 150, multiplier: 5);
            Assert.IsTrue(relayed, "Vessel under active Control lock must relay at full cadence (throttle bypassed)");
        }
    }
}
