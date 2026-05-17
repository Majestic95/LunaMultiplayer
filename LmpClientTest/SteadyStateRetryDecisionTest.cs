using LmpClient.Systems.Warp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpClientTest
{
    /// <summary>
    /// Stage 4.10 / BUG-051b. Pure decision-math coverage for the
    /// stuck-at-warp steady-state retry predicate.
    ///
    /// <see cref="WarpSystem.ShouldSteadyStateRetry"/> decides whether to resend
    /// a <c>WarpNewSubspaceMsgData</c> with the in-flight request sequence. The
    /// production caller (<c>CheckSteadyStateRetry</c>) reads the values straight
    /// from <c>TimeWarp.CurrentRateIndex</c> / <c>TimeWarp.CurrentRate</c> /
    /// instance fields, so the helper is the only test-reachable surface for the
    /// retry contract. Every entrypoint in the AND-chain has its own test so a
    /// careless flip of any single guard fires loud.
    ///
    /// Pairs with BUG-051a server-side dedup (`WarpRequestCacheTest`): the
    /// server returns the same subspace assignment for a repeated
    /// <c>(player, seq)</c> pair, so the resends this predicate gates are safe
    /// to be aggressive about.
    /// </summary>
    [TestClass]
    public class SteadyStateRetryDecisionTest
    {
        // Reference "all signals say retry" state used as the baseline for each test below.
        // The CheckSteadyStateRetry call site reads exactly these five values; any new
        // input added to the predicate should expand this baseline + add its own case.
        private const int StuckSubspace = -1;
        private const bool Waiting = true;
        private const int RateIndex0 = 0;
        private const float Rate1x = 1.0f;
        private const uint LiveSeq = 7u;

        [TestMethod]
        public void ShouldRetry_AllSignalsAlign_ReturnsTrue()
        {
            Assert.IsTrue(WarpSystem.ShouldSteadyStateRetry(
                StuckSubspace, Waiting, RateIndex0, Rate1x, LiveSeq));
        }

        [TestMethod]
        public void ShouldRetry_HasSubspace_ReturnsFalse()
        {
            // A non-(-1) CurrentSubspace means the server already assigned us
            // a subspace — the stuck-at-warp condition is not present and any
            // retry would mint orphan work or collide with a fresh request.
            Assert.IsFalse(WarpSystem.ShouldSteadyStateRetry(
                currentSubspace: 5, Waiting, RateIndex0, Rate1x, LiveSeq));
        }

        [TestMethod]
        public void ShouldRetry_NotWaiting_ReturnsFalse()
        {
            // Not waiting on the server -> no in-flight request to resend. The
            // CheckStuckAtWarp 15s path handles cold-start cases; this predicate
            // only chases the live waiting cycle.
            Assert.IsFalse(WarpSystem.ShouldSteadyStateRetry(
                StuckSubspace, waitingSubspaceIdFromServer: false, RateIndex0, Rate1x, LiveSeq));
        }

        [TestMethod]
        public void ShouldRetry_NonZeroRateIndex_ReturnsFalse()
        {
            // KSP TimeWarp.CurrentRateIndex > 0 means actively warping. The
            // server's subspace assignment will arrive at warp end; resending
            // mid-warp would mint a subspace for a state we're about to leave.
            Assert.IsFalse(WarpSystem.ShouldSteadyStateRetry(
                StuckSubspace, Waiting, timeWarpRateIndex: 1, Rate1x, LiveSeq));
        }

        [TestMethod]
        public void ShouldRetry_RateNot1x_ReturnsFalse()
        {
            // "Warp to next morning" leaves RateIndex at 0 while the
            // CurrentRate decays — the predicate must catch that too,
            // otherwise we'd retry during the unwinding phase. 0.1f tolerance
            // matches CheckWarpStopped's stock-KSP-corroborating guard.
            Assert.IsFalse(WarpSystem.ShouldSteadyStateRetry(
                StuckSubspace, Waiting, RateIndex0, timeWarpRate: 10.0f, LiveSeq));
        }

        [TestMethod]
        public void ShouldRetry_RateJustBelowTolerance_ReturnsTrue()
        {
            // 0.95 is within the 0.1 tolerance of 1.0 — predicate must accept.
            Assert.IsTrue(WarpSystem.ShouldSteadyStateRetry(
                StuckSubspace, Waiting, RateIndex0, timeWarpRate: 0.95f, LiveSeq));
        }

        [TestMethod]
        public void ShouldRetry_RateJustAboveTolerance_ReturnsFalse()
        {
            // 1.11 is outside the 0.1 tolerance of 1.0 — predicate must reject.
            Assert.IsFalse(WarpSystem.ShouldSteadyStateRetry(
                StuckSubspace, Waiting, RateIndex0, timeWarpRate: 1.11f, LiveSeq));
        }

        [TestMethod]
        public void ShouldRetry_RequestSeqZero_ReturnsFalse()
        {
            // Sentinel: seq 0 means no request is in flight (RequestNewSubspace
            // allocates a fresh seq before sending). Retrying with seq 0 would
            // hit the BUG-051a server-side dedup cache's seq-0-always-mints path
            // and create an orphan subspace.
            Assert.IsFalse(WarpSystem.ShouldSteadyStateRetry(
                StuckSubspace, Waiting, RateIndex0, Rate1x, currentRequestSeq: 0u));
        }

        [TestMethod]
        public void ShouldRetry_RequestSeqOne_ReturnsTrue()
        {
            // Lowest non-sentinel value — must pass the seq guard.
            Assert.IsTrue(WarpSystem.ShouldSteadyStateRetry(
                StuckSubspace, Waiting, RateIndex0, Rate1x, currentRequestSeq: 1u));
        }
    }
}
