using LmpCommon.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System;
using System;
using System.Collections.Concurrent;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the Stage 5.18d slice (c) consumer-lens v1 MUST FIX —
    /// per-(player, vessel, lockType) rate-limit on cross-agency
    /// <c>LockRejectMsgData</c> emission. KSP's auto-acquire paths can fire
    /// many lock attempts per second; without the debounce, every reject
    /// would stack a 5s <c>LunaScreenMsg</c> toast and obscure the playfield.
    /// </summary>
    [TestClass]
    public class LockRejectRateLimitTest
    {
        [TestMethod]
        public void ShouldEmit_FirstCallForKey_ReturnsTrue()
        {
            var dict = new ConcurrentDictionary<(string, Guid, LockType), long>();
            var ok = LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", Guid.NewGuid(), LockType.Control, nowMs: 1000L, minIntervalMs: 5000);
            Assert.IsTrue(ok, "first call for an unseen key must emit");
        }

        [TestMethod]
        public void ShouldEmit_WithinDebounceWindow_ReturnsFalse()
        {
            var dict = new ConcurrentDictionary<(string, Guid, LockType), long>();
            var vesselId = Guid.NewGuid();

            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", vesselId, LockType.Control, nowMs: 1000L, minIntervalMs: 5000));

            // 4900ms after the first emit — still inside the 5000ms window. Drop.
            Assert.IsFalse(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", vesselId, LockType.Control, nowMs: 5900L, minIntervalMs: 5000));
        }

        [TestMethod]
        public void ShouldEmit_AtExactDebounceBoundary_ReturnsTrue()
        {
            // nowMs - lastMs == minIntervalMs → the condition is `< minIntervalMs`,
            // so a delta of EXACTLY minIntervalMs allows the next emit.
            var dict = new ConcurrentDictionary<(string, Guid, LockType), long>();
            var vesselId = Guid.NewGuid();

            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", vesselId, LockType.Control, nowMs: 1000L, minIntervalMs: 5000));

            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", vesselId, LockType.Control, nowMs: 6000L, minIntervalMs: 5000),
                "exact-boundary tick must permit re-emit (the gate is strict <, not <=)");
        }

        [TestMethod]
        public void ShouldEmit_AfterDebounceWindow_ReturnsTrue()
        {
            var dict = new ConcurrentDictionary<(string, Guid, LockType), long>();
            var vesselId = Guid.NewGuid();

            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", vesselId, LockType.Control, nowMs: 1000L, minIntervalMs: 5000));

            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", vesselId, LockType.Control, nowMs: 20_000L, minIntervalMs: 5000));
        }

        [TestMethod]
        public void ShouldEmit_DifferentPlayer_NoCrossKey()
        {
            // Per-key debounce: a different player attempting the same vessel
            // gets their own emission window. Alice's reject doesn't suppress
            // Bob's.
            var dict = new ConcurrentDictionary<(string, Guid, LockType), long>();
            var vesselId = Guid.NewGuid();

            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", vesselId, LockType.Control, nowMs: 1000L, minIntervalMs: 5000));
            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Bob", vesselId, LockType.Control, nowMs: 1500L, minIntervalMs: 5000));
        }

        [TestMethod]
        public void ShouldEmit_DifferentVessel_NoCrossKey()
        {
            var dict = new ConcurrentDictionary<(string, Guid, LockType), long>();

            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", Guid.NewGuid(), LockType.Control, nowMs: 1000L, minIntervalMs: 5000));
            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", Guid.NewGuid(), LockType.Control, nowMs: 1500L, minIntervalMs: 5000));
        }

        [TestMethod]
        public void ShouldEmit_DifferentLockType_NoCrossKey()
        {
            // Same player + same vessel but different lock type (Control vs.
            // Update) — independent debounce windows. Each lock type has
            // its own UI surface and warrants its own toast.
            var dict = new ConcurrentDictionary<(string, Guid, LockType), long>();
            var vesselId = Guid.NewGuid();

            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", vesselId, LockType.Control, nowMs: 1000L, minIntervalMs: 5000));
            Assert.IsTrue(LockSystemSender.ShouldEmitCrossAgencyReject(
                dict, "Alice", vesselId, LockType.Update, nowMs: 1500L, minIntervalMs: 5000));
        }
    }
}
