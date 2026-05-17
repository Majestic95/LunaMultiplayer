using System;
using System.Collections;
using UnityEngine;

namespace LmpClient.VesselUtilities
{
    /// <summary>
    /// BUG-008 Phase A — defensive client-side PQS-vs-stored-altitude reconciliation.
    /// When a vessel proto is loaded from the server, KSP places it at the proto's
    /// stored latitude/longitude/altitude. If the high-LOD PQS terrain at that
    /// lat/lng has not yet streamed in, KSP's placement disagrees with the eventual
    /// rendered surface by metres to tens of metres — the vessel then intersects
    /// the high-LOD mesh, collider resolution explodes parts, and polygons scramble.
    /// See docs/research/02-analysis/bug-008-pqs-spawn-altitude.md.
    ///
    /// The fix waits for PQS to stabilise, snaps the vessel onto the high-LOD
    /// surface, and only then fires LMP's <c>onLmpVesselLoaded</c> event so the
    /// rest of the systems see the corrected pose. Per-load latency budget is
    /// roughly one second on warm PQS, capped at <see cref="MaxPollSeconds"/>
    /// for the cold-cache case.
    ///
    /// The decision math (does this vessel need re-alignment? is a PQS poll
    /// sample stable?) is split out as pure static helpers so the regression
    /// suite in <c>LmpClientTest</c> can exhaustively cover the edge cases
    /// (NaN inputs, exact threshold, etc.) without standing up KSP.
    /// </summary>
    public static class PqsAlignmentRoutine
    {
        /// <summary>Default delta in metres above which a vessel is considered mis-placed and
        /// must be re-aligned. 1.0 m is the smallest value the reported repros for BUG-008
        /// produced; typical visible cases are 5-50 m.</summary>
        public const double DefaultThresholdMeters = 1.0;

        /// <summary>The PQS poll is considered stable when two consecutive samples are
        /// within this many metres of each other.</summary>
        public const double DefaultStabilityDeltaMeters = 0.1;

        /// <summary>Hard cap on the poll loop. After this many seconds we give up and
        /// align with whatever PQS is currently reporting, logging a warning so an
        /// operator can investigate whether Phase B (server-side stored terrainAltitude)
        /// is needed for that body / lat-lng.</summary>
        public const float MaxPollSeconds = 5f;

        /// <summary>Pure: returns true when the absolute difference between the stored and
        /// PQS-reported altitudes exceeds <paramref name="thresholdMeters"/>. NaN inputs
        /// always return false (no realignment) — we never act on incomplete data.</summary>
        public static bool NeedsRealignment(double storedAltitude, double pqsSurfaceHeight, double thresholdMeters)
        {
            if (double.IsNaN(storedAltitude) || double.IsNaN(pqsSurfaceHeight) || double.IsNaN(thresholdMeters))
                return false;
            if (thresholdMeters < 0)
                return false;
            return Math.Abs(storedAltitude - pqsSurfaceHeight) > thresholdMeters;
        }

        /// <summary>Pure: returns true when the two PQS samples are within
        /// <paramref name="stabilityDeltaMeters"/> of each other. NaN inputs always return
        /// false (a NaN sample is not stable).</summary>
        public static bool IsStable(double previousSample, double currentSample, double stabilityDeltaMeters)
        {
            if (double.IsNaN(previousSample) || double.IsNaN(currentSample) || double.IsNaN(stabilityDeltaMeters))
                return false;
            if (stabilityDeltaMeters < 0)
                return false;
            return Math.Abs(currentSample - previousSample) <= stabilityDeltaMeters;
        }

        /// <summary>
        /// Runtime entry. Inspects the freshly-loaded vessel, runs the PQS-alignment
        /// coroutine when needed, and invokes <paramref name="onAligned"/> exactly once
        /// when the vessel is safe to expose to the rest of LMP's systems. On any KSP
        /// API failure or non-applicable situation (orbiting vessel, body without PQS),
        /// the callback fires immediately — bug-fix code must never block a successful
        /// load just because the auxiliary alignment couldn't run.
        /// </summary>
        public static void AlignAndThen(Vessel vessel, Action onAligned)
        {
            if (vessel == null)
            {
                onAligned?.Invoke();
                return;
            }

            try
            {
                if (!IsSurfaceSituation(vessel.situation) || vessel.mainBody == null || vessel.mainBody.pqsController == null)
                {
                    onAligned?.Invoke();
                    return;
                }

                var pqs = vessel.mainBody.pqsController;
                var pqsSurfaceHeight = QueryPqsSurfaceHeight(vessel.mainBody, pqs, vessel.latitude, vessel.longitude);
                var stored = vessel.terrainAltitude;

                if (!NeedsRealignment(stored, pqsSurfaceHeight, DefaultThresholdMeters))
                {
                    onAligned?.Invoke();
                    return;
                }

                LunaLog.Log($"[fix:BUG-008] vessel {vessel.id} needs PQS re-alignment " +
                            $"(stored terrain={stored:F2} m, pqs={pqsSurfaceHeight:F2} m, delta={Math.Abs(stored - pqsSurfaceHeight):F2} m); polling for stability");

                MainSystem.Singleton.StartCoroutine(StabiliseAndAlignCoroutine(vessel, onAligned));
            }
            catch (Exception ex)
            {
                // KSP API surfaces (PQS in particular) have changed across versions and
                // a wrong shape here would otherwise hang every vessel load. Log + fall
                // through to firing the load event so the user sees the bug instead of a
                // permanently-pending load.
                LunaLog.LogError($"[fix:BUG-008] PQS alignment threw on vessel {vessel.id}; falling through. Details: {ex}");
                onAligned?.Invoke();
            }
        }

        private static bool IsSurfaceSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.LANDED
                || situation == Vessel.Situations.SPLASHED
                || situation == Vessel.Situations.PRELAUNCH;
        }

        /// <summary>
        /// KSP idiom for getting the high-LOD terrain altitude at a lat/lng: pull the unit
        /// radial outward from the body centre via <c>CelestialBody.GetRelSurfaceNVector</c>,
        /// feed it to <c>PQS.GetSurfaceHeight</c> to get the radial distance to the rendered
        /// mesh, then subtract <c>body.Radius</c> for altitude above sea level.
        /// </summary>
        private static double QueryPqsSurfaceHeight(CelestialBody body, PQS pqs, double latitude, double longitude)
        {
            var radial = body.GetRelSurfaceNVector(latitude, longitude);
            return pqs.GetSurfaceHeight(radial) - body.Radius;
        }

        private static IEnumerator StabiliseAndAlignCoroutine(Vessel vessel, Action onAligned)
        {
            var startTime = Time.realtimeSinceStartup;
            var previousSample = double.NaN;
            var pqs = vessel.mainBody.pqsController;

            while (true)
            {
                double currentSample;
                try
                {
                    currentSample = QueryPqsSurfaceHeight(vessel.mainBody, pqs, vessel.latitude, vessel.longitude);
                }
                catch (Exception ex)
                {
                    LunaLog.LogError($"[fix:BUG-008] PQS poll threw on vessel {vessel.id}; firing load event without alignment. Details: {ex}");
                    onAligned?.Invoke();
                    yield break;
                }

                var stable = IsStable(previousSample, currentSample, DefaultStabilityDeltaMeters);
                var timedOut = Time.realtimeSinceStartup - startTime > MaxPollSeconds;

                if (stable || timedOut)
                {
                    if (timedOut && !stable)
                        LunaLog.Log($"[fix:BUG-008] PQS alignment timed out after {MaxPollSeconds}s for vessel {vessel.id}; applying best-effort snap at {currentSample:F2} m");
                    else
                        LunaLog.Log($"[fix:BUG-008] PQS stabilised for vessel {vessel.id} at {currentSample:F2} m; snapping vessel");

                    TrySnapToSurface(vessel, currentSample);
                    onAligned?.Invoke();
                    yield break;
                }

                previousSample = currentSample;
                yield return null;
            }
        }

        private static void TrySnapToSurface(Vessel vessel, double pqsSurfaceHeight)
        {
            try
            {
                // Preserve the vessel's height above terrain (the proto's intent); only re-base
                // the terrain reference itself. Then re-sync the orbit driver so KSP's cached
                // position is consistent with the new altitude.
                var heightAboveTerrain = vessel.altitude - vessel.terrainAltitude;
                var newAltitude = pqsSurfaceHeight + heightAboveTerrain;

                vessel.altitude = newAltitude;
                vessel.terrainAltitude = pqsSurfaceHeight;

                if (vessel.orbitDriver != null)
                    vessel.orbitDriver.updateFromParameters();
            }
            catch (Exception ex)
            {
                LunaLog.LogError($"[fix:BUG-008] snap-to-surface threw on vessel {vessel.id}; continuing without snap. Details: {ex}");
            }
        }
    }
}
