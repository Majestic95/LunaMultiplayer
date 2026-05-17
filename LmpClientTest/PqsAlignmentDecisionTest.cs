using LmpClient.VesselUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpClientTest
{
    /// <summary>
    /// Stage 3.8 / BUG-008 Phase A. Exhaustive coverage of the pure decision math in
    /// <see cref="PqsAlignmentRoutine"/>. The KSP-bound coroutine that drives polling
    /// and snaps the vessel pose cannot be unit-tested here (no PQS at test time);
    /// these tests pin down the two pure helpers that all coroutine state hinges on:
    ///   * <c>NeedsRealignment</c> — should we kick off the poll at all?
    ///   * <c>IsStable</c> — has the poll converged?
    /// Together they shape every observable behaviour of the routine.
    /// </summary>
    [TestClass]
    public class PqsAlignmentDecisionTest
    {
        // ---------- NeedsRealignment ----------

        [TestMethod]
        public void NeedsRealignment_DeltaBelowThreshold_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.NeedsRealignment(100.0, 100.5, 1.0));
        }

        [TestMethod]
        public void NeedsRealignment_DeltaExactlyAtThreshold_False()
        {
            // Strictly greater-than-threshold triggers realignment; equality does not.
            Assert.IsFalse(PqsAlignmentRoutine.NeedsRealignment(100.0, 101.0, 1.0));
        }

        [TestMethod]
        public void NeedsRealignment_DeltaAboveThreshold_True()
        {
            Assert.IsTrue(PqsAlignmentRoutine.NeedsRealignment(100.0, 105.5, 1.0));
        }

        [TestMethod]
        public void NeedsRealignment_NegativeDeltaAboveThreshold_True()
        {
            // Absolute value — direction does not matter.
            Assert.IsTrue(PqsAlignmentRoutine.NeedsRealignment(100.0, 90.0, 1.0));
        }

        [TestMethod]
        public void NeedsRealignment_NaNStored_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.NeedsRealignment(double.NaN, 100.0, 1.0));
        }

        [TestMethod]
        public void NeedsRealignment_NaNPqs_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.NeedsRealignment(100.0, double.NaN, 1.0));
        }

        [TestMethod]
        public void NeedsRealignment_NegativeThreshold_False()
        {
            // A negative threshold is nonsensical — treat it as "never realign" rather than
            // a sentinel that always realigns.
            Assert.IsFalse(PqsAlignmentRoutine.NeedsRealignment(100.0, 150.0, -1.0));
        }

        [TestMethod]
        public void NeedsRealignment_ZeroThreshold_TriggersOnAnyNonZeroDelta()
        {
            Assert.IsTrue(PqsAlignmentRoutine.NeedsRealignment(100.0, 100.0001, 0.0));
            Assert.IsFalse(PqsAlignmentRoutine.NeedsRealignment(100.0, 100.0, 0.0));
        }

        [TestMethod]
        public void NeedsRealignment_DefaultThresholdIsOneMetre()
        {
            // Pinning the named constant — operators read the threshold off this value.
            Assert.AreEqual(1.0, PqsAlignmentRoutine.DefaultThresholdMeters);
        }

        // ---------- IsStable ----------

        [TestMethod]
        public void IsStable_SamplesWithinDelta_True()
        {
            Assert.IsTrue(PqsAlignmentRoutine.IsStable(100.0, 100.05, 0.1));
        }

        [TestMethod]
        public void IsStable_SamplesExactlyAtDelta_True()
        {
            // Inclusive at the boundary — once we're within delta we're done polling.
            Assert.IsTrue(PqsAlignmentRoutine.IsStable(100.0, 100.1, 0.1));
        }

        [TestMethod]
        public void IsStable_SamplesAboveDelta_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.IsStable(100.0, 100.2, 0.1));
        }

        [TestMethod]
        public void IsStable_NaNPrevious_False()
        {
            // First poll has no previous sample; NaN must NOT short-circuit to stable.
            Assert.IsFalse(PqsAlignmentRoutine.IsStable(double.NaN, 100.0, 0.1));
        }

        [TestMethod]
        public void IsStable_NaNCurrent_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.IsStable(100.0, double.NaN, 0.1));
        }

        [TestMethod]
        public void IsStable_NegativeDelta_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.IsStable(100.0, 100.01, -0.1));
        }

        [TestMethod]
        public void IsStable_DefaultsAreOneTenthMetreAndFiveSeconds()
        {
            // Pinning the named constants so a careless edit doesn't quietly change cadence.
            Assert.AreEqual(0.1, PqsAlignmentRoutine.DefaultStabilityDeltaMeters);
            Assert.AreEqual(5f, PqsAlignmentRoutine.MaxPollSeconds);
        }

        // ---------- IsSaneAltitudeSample ----------

        [TestMethod]
        public void IsSaneAltitudeSample_TypicalKerbinTerrain_True()
        {
            // Stock KSP terrain altitudes are in the ±10 km range; the sanity envelope is
            // 1000 km, generous enough to never reject a legitimate sample.
            Assert.IsTrue(PqsAlignmentRoutine.IsSaneAltitudeSample(0.0));
            Assert.IsTrue(PqsAlignmentRoutine.IsSaneAltitudeSample(6_764.0));   // top of Mt. Everest-equivalent on Kerbin
            Assert.IsTrue(PqsAlignmentRoutine.IsSaneAltitudeSample(-50.0));     // underwater
        }

        [TestMethod]
        public void IsSaneAltitudeSample_OffByBodyRadius_False()
        {
            // The load-bearing wrong-API case: GetSurfaceHeight returning altitude-above-sea
            // directly (no body.Radius to subtract) produces roughly -600 km for a vessel
            // landed on Kerbin (`altitude - body.Radius` ~= `~0 - 600_000`). The 100 km
            // envelope must reject this. Tested for both sign conventions.
            Assert.IsFalse(PqsAlignmentRoutine.IsSaneAltitudeSample(-600_000.0));
            Assert.IsFalse(PqsAlignmentRoutine.IsSaneAltitudeSample(600_000.0));
            // Mun-class (~200 km wrong) and Duna-class (~320 km wrong) too.
            Assert.IsFalse(PqsAlignmentRoutine.IsSaneAltitudeSample(-200_000.0));
            Assert.IsFalse(PqsAlignmentRoutine.IsSaneAltitudeSample(-320_000.0));
        }

        [TestMethod]
        public void IsSaneAltitudeSample_NaN_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.IsSaneAltitudeSample(double.NaN));
        }

        [TestMethod]
        public void IsSaneAltitudeSample_Infinity_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.IsSaneAltitudeSample(double.PositiveInfinity));
            Assert.IsFalse(PqsAlignmentRoutine.IsSaneAltitudeSample(double.NegativeInfinity));
        }

        [TestMethod]
        public void IsSaneAltitudeSample_ExactlyAtEnvelope_True()
        {
            // Inclusive at the boundary.
            Assert.IsTrue(PqsAlignmentRoutine.IsSaneAltitudeSample(PqsAlignmentRoutine.SanityMaxAbsAltitudeMeters));
            Assert.IsTrue(PqsAlignmentRoutine.IsSaneAltitudeSample(-PqsAlignmentRoutine.SanityMaxAbsAltitudeMeters));
        }
    }
}
