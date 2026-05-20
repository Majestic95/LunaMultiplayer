using LmpClient.Systems.Agency;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading;

namespace LmpClientTest
{
    /// <summary>
    /// [Phase 4 Slice B-3] Pins the latest-wins debounce contract for
    /// <see cref="WolfDepotDebouncer"/>. The debouncer guards the wire against
    /// MKS' Negotiate-driven resource-stream mutations firing every
    /// <c>FixedUpdate</c> on an active depot.
    ///
    /// <para><b>Test surface.</b> Production calls
    /// <c>EnqueueAndMaybeFlush</c> from the Negotiate postfix path. Tests inject
    /// a no-op sender via <see cref="WolfDepotDebouncer.SendOverride"/> so the
    /// LmpClient network stack does not need to be brought up. The
    /// <c>ResetForTests</c> helper restores per-test isolation (the static
    /// <c>_pending</c> dict + Stopwatch otherwise survive across test methods
    /// in MSTest's default mode).</para>
    ///
    /// <para>Five cases cover the load-bearing semantics: latest-wins keying,
    /// per-key isolation across distinct (Body, Biome) tuples, manual flush
    /// draining the dict in one batch, null/empty input rejection, and the
    /// idempotent-on-empty flush guard.</para>
    /// </summary>
    [TestClass]
    public class WolfDepotDebouncerTest
    {
        // Per-test gate state; tests that exercise the gate-off branch flip
        // it locally. ResetForTests below clears the GateResolver, so we
        // re-install ours on each TestInitialize.
        private static bool _gateOn;

        [TestInitialize]
        public void ResetBetweenCases()
        {
            WolfDepotDebouncer.ResetForTests();
            _gateOn = true;
            WolfDepotDebouncer.GateResolver = () => _gateOn;
        }

        [TestMethod]
        public void Enqueue_SameKey_LatestWins_OnFlush()
        {
            List<AgencyWolfDepotEntry> captured = null;
            WolfDepotDebouncer.SendOverride = batch => captured = new List<AgencyWolfDepotEntry>(batch);

            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry
            {
                Body = "Kerbin",
                Biome = "Shores",
                IsEstablished = false,
                IsSurveyed = false
            });
            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry
            {
                Body = "Kerbin",
                Biome = "Shores",
                IsEstablished = true,
                IsSurveyed = true
            });

            Assert.AreEqual(1, WolfDepotDebouncer.PendingCount, "Same-key enqueue should replace, not stack.");

            WolfDepotDebouncer.Flush();

            Assert.IsNotNull(captured, "SendOverride should fire on non-empty Flush.");
            Assert.AreEqual(1, captured.Count);
            Assert.IsTrue(captured[0].IsEstablished, "Latest-wins should retain IsEstablished=true from the second enqueue.");
            Assert.IsTrue(captured[0].IsSurveyed);
        }

        [TestMethod]
        public void Enqueue_DistinctKeys_BothFlushed()
        {
            List<AgencyWolfDepotEntry> captured = null;
            WolfDepotDebouncer.SendOverride = batch => captured = new List<AgencyWolfDepotEntry>(batch);

            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Kerbin", Biome = "Shores" });
            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Kerbin", Biome = "Grasslands" });
            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" });

            Assert.AreEqual(3, WolfDepotDebouncer.PendingCount);

            WolfDepotDebouncer.Flush();

            Assert.IsNotNull(captured);
            Assert.AreEqual(3, captured.Count, "Distinct (Body, Biome) keys should each flush as a separate entry.");
        }

        [TestMethod]
        public void Flush_DrainsPendingDict()
        {
            WolfDepotDebouncer.SendOverride = _ => { };

            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Kerbin", Biome = "Shores" });
            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" });
            Assert.AreEqual(2, WolfDepotDebouncer.PendingCount);

            WolfDepotDebouncer.Flush();

            Assert.AreEqual(0, WolfDepotDebouncer.PendingCount, "Flush must clear the pending dict.");
        }

        [TestMethod]
        public void Enqueue_NullEntry_NoOp()
        {
            var sendCalls = 0;
            WolfDepotDebouncer.SendOverride = _ => sendCalls++;

            WolfDepotDebouncer.EnqueueAndMaybeFlush(null);
            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "", Biome = "Shores" });
            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Kerbin", Biome = null });

            Assert.AreEqual(0, WolfDepotDebouncer.PendingCount, "Null entry + empty/null Body/Biome must not enqueue.");

            WolfDepotDebouncer.Flush();
            Assert.AreEqual(0, sendCalls, "Empty pending state must not fire SendOverride.");
        }

        [TestMethod]
        public void Flush_OnEmptyDict_DoesNotInvokeSender()
        {
            var sendCalls = 0;
            WolfDepotDebouncer.SendOverride = _ => sendCalls++;

            WolfDepotDebouncer.Flush();
            WolfDepotDebouncer.Flush();

            Assert.AreEqual(0, sendCalls, "Idempotent flush on empty must be a true no-op (zero sender invocations).");
        }

        [TestMethod]
        public void Flush_UnderGateOff_DiscardsPendingAndSkipsSend()
        {
            // Pin the consumer/integration review finding: a Flush() that
            // lands under gate=off (mid-session flip, future
            // graceful-disconnect cleanup) must NOT emit pre-flip buffered
            // snapshots and must clear the dict so a subsequent on-flip
            // doesn't carry stale state forward.
            var sendCalls = 0;
            WolfDepotDebouncer.SendOverride = _ => sendCalls++;

            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Kerbin", Biome = "Shores" });
            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" });
            Assert.AreEqual(2, WolfDepotDebouncer.PendingCount, "Setup: two entries should be pending under gate=on.");

            _gateOn = false;
            WolfDepotDebouncer.Flush();

            Assert.AreEqual(0, sendCalls, "Flush under gate=off must NOT call the sender.");
            Assert.AreEqual(0, WolfDepotDebouncer.PendingCount, "Flush under gate=off must clear pending snapshots.");
        }

        [TestMethod]
        public void EnqueueAndMaybeFlush_AfterIntervalElapses_AutoFlushes()
        {
            // Pin the load-bearing branch: the whole reason WolfDepotDebouncer
            // exists is the 1s timer-driven auto-flush at the entry-point
            // check (`_sinceLastFlush.ElapsedMilliseconds >= FlushIntervalMs`).
            // None of the prior cases exercise it — they all call Flush()
            // manually. This case waits past the interval, then a second
            // Enqueue should drain BOTH entries inline.
            List<AgencyWolfDepotEntry> captured = null;
            WolfDepotDebouncer.SendOverride = batch => captured = new List<AgencyWolfDepotEntry>(batch);

            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Kerbin", Biome = "Shores" });
            Assert.IsNull(captured, "First enqueue should not have triggered auto-flush (interval not elapsed).");

            // 100 ms over the 1000 ms interval. The integration cost is 1.1 s
            // per test run — acceptable for pinning the debouncer's reason
            // for existing.
            Thread.Sleep(WolfDepotDebouncer.FlushIntervalMs + 100);

            WolfDepotDebouncer.EnqueueAndMaybeFlush(new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" });

            Assert.IsNotNull(captured, "Second enqueue after interval elapse should have auto-flushed.");
            Assert.AreEqual(2, captured.Count, "Auto-flush snapshot should contain both pending entries.");
            Assert.AreEqual(0, WolfDepotDebouncer.PendingCount, "Auto-flush should have drained the dict.");
        }
    }
}
