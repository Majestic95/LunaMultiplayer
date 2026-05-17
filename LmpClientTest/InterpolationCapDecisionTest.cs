using LmpClient.Systems.VesselPositionSys;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpClientTest
{
    /// <summary>
    /// Stage 4.10 / BUG-003/004. Pure decision-math coverage for the
    /// future-subspace interpolation cap.
    ///
    /// The original behaviour multiplied the secondary-vessel update interval by
    /// <c>* 2</c> for past/equal subspaces and by <c>double.MaxValue</c> for the
    /// future side, which produced ~50N-frame interpolation windows for a target
    /// N seconds ahead and made distant-subspace vessels appear to crawl at
    /// 1/(50N) of normal speed. The fix caps the future side at
    /// <see cref="VesselPositionUpdate.MaxFutureInterpolationMultiplier"/> so the
    /// vessel visibly skips to the latest known pose instead of inching toward it.
    ///
    /// These tests pin:
    ///   * the multiplier-selection branch (past/equal vs future),
    ///   * the named constants (so a careless edit to either multiplier is loud),
    ///   * a representative cadence (default <c>SecondaryVesselUpdatesMsInterval</c>
    ///     of 1000ms) so the regression's symptom-rate stays bounded.
    /// </summary>
    [TestClass]
    public class InterpolationCapDecisionTest
    {
        [TestMethod]
        public void MaxFutureMultiplier_Pinned_To_10()
        {
            Assert.AreEqual(10, VesselPositionUpdate.MaxFutureInterpolationMultiplier,
                "BUG-003/004 cap multiplier moved — a careless bump back to MaxValue would resurrect the frozen-vessel symptom.");
        }

        [TestMethod]
        public void PastOrEqualMultiplier_Pinned_To_2()
        {
            Assert.AreEqual(2, VesselPositionUpdate.PastOrEqualInterpolationMultiplier,
                "Past/equal multiplier is the historical pre-fix value; treat any change as protocol-class.");
        }

        [TestMethod]
        public void ComputeMaxInterpolationDuration_PastSubspace_AppliesPastMultiplier()
        {
            // 1000ms interval, past/equal subspace -> 1s * 2 = 2s.
            var duration = VesselPositionUpdate.ComputeMaxInterpolationDuration(1000, subspaceIsEqualOrInThePast: true);
            Assert.AreEqual(2.0, duration, 0.0001);
        }

        [TestMethod]
        public void ComputeMaxInterpolationDuration_FutureSubspace_AppliesFutureMultiplier()
        {
            // 1000ms interval, future subspace -> 1s * 10 = 10s. The pre-fix code returned
            // ~double.MaxValue here, hence the frozen-vessel symptom.
            var duration = VesselPositionUpdate.ComputeMaxInterpolationDuration(1000, subspaceIsEqualOrInThePast: false);
            Assert.AreEqual(10.0, duration, 0.0001);
        }

        [TestMethod]
        public void ComputeMaxInterpolationDuration_ScalesLinearlyWithInterval()
        {
            // 500ms interval, future -> 0.5s * 10 = 5s
            // 2000ms interval, future -> 2s * 10 = 20s
            // Confirms the helper is a straight-line product of interval-in-seconds and multiplier.
            Assert.AreEqual(5.0, VesselPositionUpdate.ComputeMaxInterpolationDuration(500, false), 0.0001);
            Assert.AreEqual(20.0, VesselPositionUpdate.ComputeMaxInterpolationDuration(2000, false), 0.0001);
        }

        [TestMethod]
        public void ComputeMaxInterpolationDuration_FuturePathIsFiveTimesPast()
        {
            // 1000ms, both branches: future / past ratio = MaxFutureInterpolationMultiplier / PastOrEqualInterpolationMultiplier = 5.
            // This invariant matters because if someone tightens the future multiplier and forgets
            // the past one (or vice versa), the relative behaviour shifts and reports of "vessels
            // skip more aggressively now" or "past-subspace looks too smooth" become hard to root-cause.
            var past = VesselPositionUpdate.ComputeMaxInterpolationDuration(1000, true);
            var future = VesselPositionUpdate.ComputeMaxInterpolationDuration(1000, false);
            Assert.AreEqual(5.0, future / past, 0.0001);
        }

        [TestMethod]
        public void ComputeMaxInterpolationDuration_ZeroInterval_ReturnsZero()
        {
            // Defensive: a zero/negative SecondaryVesselUpdatesMsInterval would mean the
            // settings layer is broken, but the helper itself should not multiply through
            // to a NaN or negative duration.
            Assert.AreEqual(0.0, VesselPositionUpdate.ComputeMaxInterpolationDuration(0, true), 0.0001);
            Assert.AreEqual(0.0, VesselPositionUpdate.ComputeMaxInterpolationDuration(0, false), 0.0001);
        }
    }
}
