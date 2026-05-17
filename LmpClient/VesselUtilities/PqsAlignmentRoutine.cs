using LmpClient.Systems.VesselRemoveSys;
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
    /// Phase A has two pieces:
    ///   * <b>Snap</b> — wait for PQS to stabilise, then snap the vessel onto the
    ///     high-LOD surface. Always active. Driven by <see cref="NeedsRealignment"/>.
    ///   * <b>Pack-on-load</b> (BUG-008 item 4a) — for surface vessels that arrived
    ///     <i>loaded</i> (i.e. in physics range of the local camera, packed==false
    ///     after <c>ProtoVessel.Load</c>) we additionally call
    ///     <c>Vessel.GoOnRails()</c> on entry, run the poll regardless of whether
    ///     the immediate sample agrees with stored altitude, yield one
    ///     FixedUpdate after the snap, and then call <c>Vessel.GoOffRails()</c>.
    ///     The PQS stream may not have caught up yet even when the first sample
    ///     looks fine; physics frozen during the wait keeps the collider race from
    ///     scrambling polygons. The active vessel is never packed (camera judder);
    ///     already-packed vessels skip the pack path (no physics → no race).
    ///     Driven by <see cref="ShouldPackForLoad"/>.
    ///
    /// Per-load latency budget is roughly one second on warm PQS, capped at
    /// <see cref="MaxPollSeconds"/> for the cold-cache case. The pack path adds
    /// one FixedUpdate (~20 ms) of artificial freeze.
    ///
    /// The decision math (does this vessel need re-alignment? is a PQS poll
    /// sample stable? should this vessel be packed for the load wait?) is split
    /// out as pure static helpers so the regression suite in <c>LmpClientTest</c>
    /// can exhaustively cover the edge cases (NaN inputs, exact threshold, etc.)
    /// without standing up KSP.
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

        /// <summary>Sanity threshold on a single PQS sample. Stock KSP bodies have terrain
        /// peaks well under 10 km; a sample whose magnitude exceeds this almost certainly
        /// indicates a KSP API mismatch (e.g. <c>PQS.GetSurfaceHeight</c> returning
        /// altitude-above-sea directly rather than the radial distance we subtract
        /// <c>body.Radius</c> from — that produces ~-600 km for Kerbin-class bodies).
        /// 100 km is generous for any real terrain, tight enough to catch the
        /// off-by-radius case for every PQS-bearing body in the stock system except Gilly
        /// and Minmus (60 km radius and below). Small-moon mistakes would slip through
        /// here; in-KSP soak is the backstop.</summary>
        public const double SanityMaxAbsAltitudeMeters = 100_000d;

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

        /// <summary>Pure: returns true when a single PQS altitude sample is within the
        /// sanity envelope. NaN or out-of-range values fail the check so the runtime
        /// driver can bail before snapping a vessel to a physically-impossible position.</summary>
        public static bool IsSaneAltitudeSample(double altitudeAboveSea)
        {
            if (double.IsNaN(altitudeAboveSea) || double.IsInfinity(altitudeAboveSea))
                return false;
            return Math.Abs(altitudeAboveSea) <= SanityMaxAbsAltitudeMeters;
        }

        /// <summary>Pure: returns true when the freshly-loaded vessel should be packed
        /// (<c>GoOnRails</c>) for the PQS-stabilise wait. The decision exists as a separate
        /// helper from <see cref="NeedsRealignment"/> because the pack path triggers on
        /// <i>where</i> the vessel arrived (in physics range, loaded), not on whether the
        /// immediate PQS sample looks wrong; an immediately-correct sample can become
        /// wrong half a second later when the high-LOD mesh streams in.
        ///
        /// <para>Skip conditions, in order:
        /// <list type="bullet">
        ///   <item><description><b>Active vessel</b> — packing would judder the camera. The
        ///     existing snap-only path is the partial mitigation for the active-vessel
        ///     reconnect case; full coverage is BUG-008 item 4c (phantom-force suppression)
        ///     or 4d (hard landed-pin), not this slice.</description></item>
        ///   <item><description><b>No PQS controller</b> — bodies without PQS (Kerbol, the
        ///     sun) can't have the spawn-altitude race; pack would be a no-op wait.</description></item>
        ///   <item><description><b>Already packed</b> — vessel arrived out of physics range
        ///     (loaded packed by stock KSP). Physics frozen, no collider race, no need to
        ///     burn a FixedUpdate. The snap path still fires on this branch via
        ///     <see cref="NeedsRealignment"/>.</description></item>
        ///   <item><description><b>Non-surface situation</b> — the race only manifests for
        ///     <c>LANDED</c>/<c>SPLASHED</c>/<c>PRELAUNCH</c> vessels colliding with
        ///     streamed-in terrain. Orbital vessels are unaffected. Compute the bool via
        ///     <see cref="IsSurfaceSituation(int)"/>.</description></item>
        /// </list>
        /// </para>
        ///
        /// <para>Takes a pre-computed <paramref name="isSurfaceSituation"/> bool rather than
        /// the <c>Vessel.Situations</c> enum so this helper stays KSP-DLL-free at compile
        /// time. <see cref="IsSurfaceSituation(int)"/> is the matching helper to compute
        /// the bool; both are independently testable in <c>LmpClientTest</c> without
        /// pulling Assembly-CSharp into the test project.</para>
        /// </summary>
        public static bool ShouldPackForLoad(bool isSurfaceSituation, bool isActiveVessel,
                                             bool hasPqsController, bool currentlyPacked)
        {
            if (isActiveVessel) return false;
            if (!hasPqsController) return false;
            if (currentlyPacked) return false;
            return isSurfaceSituation;
        }

        /// <summary>Pure: returns true when the situation value (read off
        /// <c>Vessel.Situations</c> at the call site) is one of the three surface states
        /// the pack/snap path treats as "on the ground". Takes the int form of the enum so
        /// the test project does not need to reference Assembly-CSharp.
        ///
        /// <para>The numeric values pinned here (<c>LANDED == 1</c>, <c>SPLASHED == 2</c>,
        /// <c>PRELAUNCH == 4</c>) are part of KSP's on-disk vessel format: ConfigNode's
        /// <c>sit = LANDED</c> serialises to/from the enum value and the int form is what
        /// ends up in the persistent save. The mapping is therefore stable across the
        /// targeting pack we build for. <c>Vessel.Situations</c> is not <c>[Flags]</c> —
        /// each vessel holds exactly one situation value — so this is a value-set test,
        /// not a bitmask test.</para>
        /// </summary>
        public static bool IsSurfaceSituation(int situationValue)
        {
            // LANDED = 1, SPLASHED = 2, PRELAUNCH = 4 in KSP 1.12.5's Vessel.Situations.
            return situationValue == 1 || situationValue == 2 || situationValue == 4;
        }

        /// <summary>
        /// Runtime entry. Inspects the freshly-loaded vessel, runs the PQS-alignment
        /// coroutine when needed, and invokes <paramref name="onAligned"/> exactly once
        /// when the vessel is safe to expose to the rest of LMP's systems. On any KSP
        /// API failure or non-applicable situation (orbiting vessel, body without PQS),
        /// the callback fires immediately — bug-fix code must never block a successful
        /// load just because the auxiliary alignment couldn't run.
        ///
        /// <para>Note: the callback may fire up to <see cref="MaxPollSeconds"/> after this
        /// returns when an alignment is required. Subscribers to
        /// <c>VesselLoadEvent.onLmpVesselLoaded</c> must remain idempotent.</para>
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
                if (!IsSurfaceSituation((int)vessel.situation) || vessel.mainBody == null || vessel.mainBody.pqsController == null)
                {
                    onAligned?.Invoke();
                    return;
                }

                // 4a pack path: surface vessel that arrived loaded (in physics range, packed==false)
                // gets packed for the PQS wait, regardless of whether the immediate sample looks
                // correct. The mesh may stream in mid-flight-tick AFTER an initially-agreeing
                // sample and explode the collider before our existing snap path notices.
                //
                // EVA short-circuit: VesselLoader.cs:LoadVesselIntoGame already calls
                // GoOnRails on EVAs before returning. We trust that pack and stay on the
                // snap-only path here — packing again risks double-firing the EVA fsm
                // (the documented NRE source on KerbalEVA.fsm when GoOnRails runs twice
                // in quick succession). The snap path is the right home for EVA-side
                // alignment work; the pack lifecycle is owned by VesselLoader.
                //
                // Active-vessel check uses BOTH FlightGlobals.ActiveVessel.id and
                // vessel.isActiveVessel because LMP's reconnect path calls
                // FlightGlobals.ForceSetActiveVessel inside VesselLoader.LoadVesselIntoGame
                // and the two flags can briefly disagree while KSP propagates the change.
                // Either-true is enough to skip pack.
                if (vessel.isEVA)
                {
                    LunaLog.Log($"[fix:BUG-008-pack] vessel {vessel.id} is EVA on {vessel.mainBody.bodyName}; deferring to VesselLoader's existing GoOnRails. Continuing on snap-only path.");
                }
                var isActiveVessel = vessel.isActiveVessel
                                     || (FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.id == vessel.id);
                if (!vessel.isEVA && ShouldPackForLoad(isSurfaceSituation: true, isActiveVessel, hasPqsController: true, currentlyPacked: vessel.packed))
                {
                    LunaLog.Log($"[fix:BUG-008-pack] vessel {vessel.id} arrived loaded on {vessel.mainBody.bodyName} " +
                                $"({vessel.situation}); packing for PQS stabilise wait to prevent collider race.");
                    MainSystem.Singleton.StartCoroutine(PackStabiliseAndAlignCoroutine(vessel, onAligned));
                    return;
                }

                var pqs = vessel.mainBody.pqsController;
                var pqsSurfaceHeight = QueryPqsSurfaceHeight(vessel.mainBody, pqs, vessel.latitude, vessel.longitude);

                if (!IsSaneAltitudeSample(pqsSurfaceHeight))
                {
                    LunaLog.LogError($"[fix:BUG-008] PQS sample for vessel {vessel.id} on body {vessel.mainBody.bodyName} " +
                                     $"is outside the sanity envelope ({pqsSurfaceHeight} m); skipping alignment. " +
                                     $"This usually means the KSP PQS API returns altitude-above-sea directly — the `- body.Radius` step is wrong for this build.");
                    onAligned?.Invoke();
                    return;
                }

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

        /// <summary>Returns true when the vessel is still alive and not queued for
        /// removal. Unity's destroyed-object operator overload makes <c>vessel == null</c>
        /// return true post-destroy, so the check is reliable; the kill-list check covers
        /// the in-progress case where <c>VesselRemoveSystem</c> has flagged the vessel
        /// but hasn't yet destroyed the GameObject.</summary>
        private static bool IsAlive(Vessel vessel, Guid vesselId)
        {
            if (vessel == null) return false;
            if (VesselRemoveSystem.Singleton != null && VesselRemoveSystem.Singleton.VesselWillBeKilled(vesselId)) return false;
            return true;
        }

        /// <summary>
        /// 4a pack-and-stabilise path. Packs the vessel on entry (caller already verified
        /// <see cref="ShouldPackForLoad"/>), polls PQS until two consecutive samples agree
        /// or the hard timeout fires, snaps if a delta crossed
        /// <see cref="DefaultThresholdMeters"/>, yields one FixedUpdate to let KSP run its
        /// <c>Vessel.UpdateCaches</c> with the new placement, and unpacks. Then fires the
        /// load callback.
        ///
        /// <para>Unpack is conditional on <c>vessel.packed</c> still being true at exit:
        /// stock KSP physics-range logic may have re-evaluated and packed/unpacked the
        /// vessel underneath us mid-wait (e.g. the player walks away in EVA). Calling
        /// <c>GoOffRails</c> in that state would force-load a vessel KSP intentionally
        /// kept packed.</para>
        /// </summary>
        private static IEnumerator PackStabiliseAndAlignCoroutine(Vessel vessel, Action onAligned)
        {
            var vesselId = vessel.id;
            var weCalledPack = false;
            try
            {
                vessel.GoOnRails();
                weCalledPack = true;
            }
            catch (Exception ex)
            {
                // If KSP refuses to pack (unusual but not impossible — IVA-occupied, mid-staging,
                // etc.) fall through to the existing snap-only coroutine rather than block the
                // load. The snap path is still a strict improvement over the legacy no-PQS-check
                // behaviour. The recovery StartCoroutine is itself wrapped — if MainSystem is
                // null or the MonoBehaviour is destroyed mid-scene-transition we still owe the
                // caller an onAligned fire so subscribers don't hang.
                LunaLog.LogError($"[fix:BUG-008-pack] GoOnRails threw on vessel {vesselId}; falling back to snap-only path. Details: {ex}");
                try
                {
                    MainSystem.Singleton.StartCoroutine(StabiliseAndAlignCoroutine(vessel, onAligned));
                }
                catch (Exception startEx)
                {
                    LunaLog.LogError($"[fix:BUG-008-pack] Fallback StartCoroutine also threw on vessel {vesselId}; firing onAligned without alignment. Details: {startEx}");
                    onAligned?.Invoke();
                }
                yield break;
            }

            var startTime = Time.realtimeSinceStartup;
            var previousSample = double.NaN;
            var snapped = false;

            while (true)
            {
                if (!IsAlive(vessel, vesselId))
                {
                    // Don't leave the vessel orphaned-packed if VesselRemoveSystem only
                    // flagged-but-didn't-destroy it (kill can be canceled or deferred).
                    // SafeGoOffRailsIfWePacked re-checks IsAlive internally and try/catches
                    // the GoOffRails call, so it is safe to invoke on a mid-destruction
                    // vessel too. Load event deliberately NOT fired — subscribers like
                    // KerbalEvents.OnVesselLoaded would dereference a destroyed object.
                    LunaLog.Log($"[fix:BUG-008-pack] vessel {vesselId} removed during pack-wait; unpacking and abandoning without firing load event");
                    SafeGoOffRailsIfWePacked(vessel, vesselId, weCalledPack);
                    yield break;
                }

                double currentSample;
                try
                {
                    currentSample = QueryPqsSurfaceHeight(vessel.mainBody, vessel.mainBody.pqsController, vessel.latitude, vessel.longitude);
                }
                catch (Exception ex)
                {
                    LunaLog.LogError($"[fix:BUG-008-pack] PQS poll threw on vessel {vesselId}; firing load event without alignment. Details: {ex}");
                    SafeGoOffRailsIfWePacked(vessel, vesselId, weCalledPack);
                    onAligned?.Invoke();
                    yield break;
                }

                if (!IsSaneAltitudeSample(currentSample))
                {
                    LunaLog.LogError($"[fix:BUG-008-pack] PQS sample for vessel {vesselId} outside sanity envelope ({currentSample} m); aborting alignment");
                    SafeGoOffRailsIfWePacked(vessel, vesselId, weCalledPack);
                    onAligned?.Invoke();
                    yield break;
                }

                var stable = IsStable(previousSample, currentSample, DefaultStabilityDeltaMeters);
                var timedOut = Time.realtimeSinceStartup - startTime > MaxPollSeconds;

                if (stable || timedOut)
                {
                    if (!IsAlive(vessel, vesselId))
                    {
                        LunaLog.Log($"[fix:BUG-008-pack] vessel {vesselId} removed just before snap; unpacking and abandoning");
                        SafeGoOffRailsIfWePacked(vessel, vesselId, weCalledPack);
                        yield break;
                    }

                    var stored = vessel.terrainAltitude;
                    if (NeedsRealignment(stored, currentSample, DefaultThresholdMeters))
                    {
                        if (timedOut && !stable)
                            LunaLog.Log($"[fix:BUG-008-pack] PQS alignment timed out after {MaxPollSeconds}s for vessel {vesselId}; applying best-effort snap (stored={stored:F2} m, pqs={currentSample:F2} m)");
                        else
                            LunaLog.Log($"[fix:BUG-008-pack] PQS stabilised for vessel {vesselId} at {currentSample:F2} m; snapping (stored={stored:F2} m)");
                        TrySnapToSurface(vessel, currentSample);
                        snapped = true;
                    }
                    else
                    {
                        LunaLog.Log($"[fix:BUG-008-pack] PQS stabilised for vessel {vesselId} at {currentSample:F2} m, within threshold of stored {stored:F2} m; no snap required");
                    }
                    break;
                }

                previousSample = currentSample;
                yield return null;
            }

            // One physics tick post-pack/snap so KSP's UpdateCaches sees the corrected pose
            // before any inbound flight-state update or unpack-time physics reseed runs.
            yield return new WaitForFixedUpdate();

            if (!IsAlive(vessel, vesselId))
            {
                LunaLog.Log($"[fix:BUG-008-pack] vessel {vesselId} removed during post-snap settle; unpacking and abandoning without firing load event");
                SafeGoOffRailsIfWePacked(vessel, vesselId, weCalledPack);
                yield break;
            }

            SafeGoOffRailsIfWePacked(vessel, vesselId, weCalledPack);

            if (snapped)
                LunaLog.Log($"[fix:BUG-008-pack] vessel {vesselId} unpacked after PQS alignment");

            onAligned?.Invoke();
        }

        private static void SafeGoOffRailsIfWePacked(Vessel vessel, Guid vesselId, bool weCalledPack)
        {
            if (!weCalledPack) return;
            if (!IsAlive(vessel, vesselId)) return;
            try
            {
                // KSP will re-evaluate pack state on the next physics-range tick anyway, so
                // it's fine if the vessel ends up packed again (player walked away during
                // the wait). The point of the explicit GoOffRails is to undo our
                // intervention so the vessel resumes physics on the stabilised terrain
                // when it should.
                vessel.GoOffRails();
            }
            catch (Exception ex)
            {
                LunaLog.LogError($"[fix:BUG-008-pack] GoOffRails threw on vessel {vesselId}; vessel left packed (KSP will unpack on next physics-range check). Details: {ex}");
            }
        }

        private static IEnumerator StabiliseAndAlignCoroutine(Vessel vessel, Action onAligned)
        {
            var startTime = Time.realtimeSinceStartup;
            var previousSample = double.NaN;
            var pqs = vessel.mainBody.pqsController;
            var vesselId = vessel.id;

            while (true)
            {
                // Vessel may have been killed (out of safety bubble, ownership transfer,
                // VesselRemoveSystem.KillVessel) while we were polling. If so, abandon
                // without firing the load event — subscribers like KerbalEvents.OnVesselLoaded
                // would try to dereference a destroyed Unity object and throw.
                if (!IsAlive(vessel, vesselId))
                {
                    LunaLog.Log($"[fix:BUG-008] vessel {vesselId} was removed during PQS poll; abandoning alignment without firing load event");
                    yield break;
                }

                double currentSample;
                try
                {
                    currentSample = QueryPqsSurfaceHeight(vessel.mainBody, pqs, vessel.latitude, vessel.longitude);
                }
                catch (Exception ex)
                {
                    LunaLog.LogError($"[fix:BUG-008] PQS poll threw on vessel {vesselId}; firing load event without alignment. Details: {ex}");
                    onAligned?.Invoke();
                    yield break;
                }

                if (!IsSaneAltitudeSample(currentSample))
                {
                    LunaLog.LogError($"[fix:BUG-008] PQS sample for vessel {vesselId} outside sanity envelope ({currentSample} m); aborting alignment");
                    onAligned?.Invoke();
                    yield break;
                }

                var stable = IsStable(previousSample, currentSample, DefaultStabilityDeltaMeters);
                var timedOut = Time.realtimeSinceStartup - startTime > MaxPollSeconds;

                if (stable || timedOut)
                {
                    // Re-check before any KSP property access in the snap path — vessel
                    // could have been killed between the previous IsAlive and now.
                    if (!IsAlive(vessel, vesselId))
                    {
                        LunaLog.Log($"[fix:BUG-008] vessel {vesselId} removed just before snap; abandoning");
                        yield break;
                    }

                    if (timedOut && !stable)
                        LunaLog.Log($"[fix:BUG-008] PQS alignment timed out after {MaxPollSeconds}s for vessel {vesselId}; applying best-effort snap at {currentSample:F2} m");
                    else
                        LunaLog.Log($"[fix:BUG-008] PQS stabilised for vessel {vesselId} at {currentSample:F2} m; snapping vessel");

                    TrySnapToSurface(vessel, currentSample);
                    onAligned?.Invoke();
                    yield break;
                }

                previousSample = currentSample;
                yield return null;
            }
        }

        /// <summary>
        /// Actually move the vessel rigidbody to the new altitude. Setting
        /// <c>vessel.altitude</c> alone is a bookkeeping field write that KSP overwrites
        /// from the rigidbody position on the next <c>Vessel.UpdateCaches()</c>; the
        /// real move happens via <c>body.GetWorldSurfacePosition</c> + <c>vessel.SetPosition</c>
        /// (the same idiom used by <c>Harmony/OrbitDriver_UpdateFromParameters.cs</c>).
        /// Bookkeeping fields are updated alongside so any LMP system reading
        /// <c>vessel.altitude</c> / <c>vessel.terrainAltitude</c> before the next physics
        /// tick sees consistent values.
        /// </summary>
        private static void TrySnapToSurface(Vessel vessel, double pqsSurfaceHeight)
        {
            try
            {
                var heightAboveTerrain = vessel.altitude - vessel.terrainAltitude;
                var newAltitude = pqsSurfaceHeight + heightAboveTerrain;

                var newWorldPos = vessel.mainBody.GetWorldSurfacePosition(vessel.latitude, vessel.longitude, newAltitude);
                vessel.SetPosition(newWorldPos);

                vessel.altitude = newAltitude;
                vessel.terrainAltitude = pqsSurfaceHeight;
            }
            catch (Exception ex)
            {
                LunaLog.LogError($"[fix:BUG-008] snap-to-surface threw on vessel {vessel?.id}; continuing without snap. Details: {ex}");
            }
        }
    }
}
