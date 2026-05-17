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

        // ---------- IsSurfaceSituation (BUG-008 item 4a) ----------
        // KSP's Vessel.Situations enum values, pinned: LANDED=1, SPLASHED=2,
        // FLYING=8 (gap), PRELAUNCH=4, SUB_ORBITAL=16, ORBITING=32, ESCAPING=64,
        // DOCKED=128. The three surface-state values used by the pack path are
        // 1 / 2 / 4. Passing the int form of the enum keeps the helper KSP-DLL-free
        // so this test project does not need to reference Assembly-CSharp.

        [TestMethod]
        public void IsSurfaceSituation_Landed_True()
        {
            Assert.IsTrue(PqsAlignmentRoutine.IsSurfaceSituation(1));
        }

        [TestMethod]
        public void IsSurfaceSituation_Splashed_True()
        {
            Assert.IsTrue(PqsAlignmentRoutine.IsSurfaceSituation(2));
        }

        [TestMethod]
        public void IsSurfaceSituation_Prelaunch_True()
        {
            Assert.IsTrue(PqsAlignmentRoutine.IsSurfaceSituation(4));
        }

        [TestMethod]
        public void IsSurfaceSituation_Flying_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.IsSurfaceSituation(8));
        }

        [TestMethod]
        public void IsSurfaceSituation_SubOrbital_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.IsSurfaceSituation(16));
        }

        [TestMethod]
        public void IsSurfaceSituation_Orbiting_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.IsSurfaceSituation(32));
        }

        [TestMethod]
        public void IsSurfaceSituation_Escaping_False()
        {
            Assert.IsFalse(PqsAlignmentRoutine.IsSurfaceSituation(64));
        }

        [TestMethod]
        public void IsSurfaceSituation_Docked_False()
        {
            // DOCKED — host vessel may be physically on the ground but the situation flag
            // is not LANDED/SPLASHED/PRELAUNCH, so the pack path doesn't fire. Out-of-scope
            // for BUG-008 4a; if a surface-docked rover assembly ever exhibits a PQS-race
            // in practice we'd revisit.
            Assert.IsFalse(PqsAlignmentRoutine.IsSurfaceSituation(128));
        }

        [TestMethod]
        public void IsSurfaceSituation_Zero_False()
        {
            // Defensive: zero is not a defined Vessel.Situations value but represents
            // "uninitialised" in some KSP code paths.
            Assert.IsFalse(PqsAlignmentRoutine.IsSurfaceSituation(0));
        }

        [TestMethod]
        public void IsSurfaceSituation_Negative_False()
        {
            // Defensive: a corrupted proto or signed/unsigned cast error could leak a
            // negative value through (int)vessel.situation. The helper must not assert
            // the value is positive — just return false.
            Assert.IsFalse(PqsAlignmentRoutine.IsSurfaceSituation(-1));
        }

        // ---------- ShouldPackForLoad (BUG-008 item 4a) ----------
        // One case per AND-chain guard plus the happy path, so a regression flipping the
        // sign or dropping a clause is caught individually rather than masked by another
        // clause coincidentally returning false.

        [TestMethod]
        public void ShouldPackForLoad_SurfaceRemoteLoadedWithPqs_True()
        {
            // The exact case the pack path is here to handle: a remote player's landed
            // vessel arrived in our physics bubble (packed==false) on a PQS body.
            Assert.IsTrue(PqsAlignmentRoutine.ShouldPackForLoad(
                isSurfaceSituation: true, isActiveVessel: false,
                hasPqsController: true, currentlyPacked: false));
        }

        [TestMethod]
        public void ShouldPackForLoad_ActiveVessel_False()
        {
            // Active vessel never gets packed — would judder the camera. The snap-only
            // path is the partial mitigation for the active-vessel reconnect case.
            Assert.IsFalse(PqsAlignmentRoutine.ShouldPackForLoad(
                isSurfaceSituation: true, isActiveVessel: true,
                hasPqsController: true, currentlyPacked: false));
        }

        [TestMethod]
        public void ShouldPackForLoad_NoPqsController_False()
        {
            // Body without PQS (Kerbol) cannot have the spawn-altitude race; pack would
            // be a no-op wait.
            Assert.IsFalse(PqsAlignmentRoutine.ShouldPackForLoad(
                isSurfaceSituation: true, isActiveVessel: false,
                hasPqsController: false, currentlyPacked: false));
        }

        [TestMethod]
        public void ShouldPackForLoad_AlreadyPacked_False()
        {
            // Vessel arrived out of physics range; physics frozen, no collider race, no
            // reason to burn a FixedUpdate. The snap path still fires on this branch when
            // NeedsRealignment returns true.
            Assert.IsFalse(PqsAlignmentRoutine.ShouldPackForLoad(
                isSurfaceSituation: true, isActiveVessel: false,
                hasPqsController: true, currentlyPacked: true));
        }

        [TestMethod]
        public void ShouldPackForLoad_NonSurface_False()
        {
            // Orbital / flying / docked all fall through here — the call site passes
            // false for isSurfaceSituation (typically computed via IsSurfaceSituation).
            Assert.IsFalse(PqsAlignmentRoutine.ShouldPackForLoad(
                isSurfaceSituation: false, isActiveVessel: false,
                hasPqsController: true, currentlyPacked: false));
        }
    }
}
