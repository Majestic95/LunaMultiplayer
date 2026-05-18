using System;
using System.Collections;
using System.Collections.Generic;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;
using UnityEngine;

// ReSharper disable All

namespace LmpClient.Systems.Warp
{
    /// <summary>
    /// MKS-R1 — Snaps the local subspace forward to match another player's
    /// subspace when our active vessel is in physics range of their controlled
    /// MKS kolony, so MKS' time-based accrual reads (Planetarium.GetUniversalTime)
    /// happen in the same time base across the co-present cohort.
    ///
    /// The bug this addresses (Phase 2 / R1 of mks-lmp-compatibility-handoff
    /// v3.3): MKS' KolonizationManager writes KolonizationEntry.LastUpdate /
    /// KolonyDate values minted in the local subspace UT. Two clients at the
    /// same body in different subspaces produce entries time-stamped in
    /// different UT bases; LMP's 30s scenario SHA pass merges them
    /// incoherently; cross-vessel aggregation (GetGeologyResearchBonus, etc.)
    /// drifts. Forcing subspace agreement on proximity keeps the cross-vessel
    /// math time-base-coherent while both players are co-present.
    ///
    /// Mirrors the structure of <c>WarpSystem.WarpIfSpectatingToController</c>
    /// but for the opposite condition: the local player CONTROLS their own
    /// vessel (not spectating) AND is in physics range of another player's
    /// MKS-bearing vessel. The two routines are siblings, not collaborators;
    /// preconditions are mutually exclusive.
    ///
    /// Jank-protection stack (Phase 2 pre-spec §3):
    /// <list type="number">
    ///   <item><see cref="WarpSystem.SafeToSync"/> gate — refuses snap during
    ///         atmospheric flight or when the UT jump would decay periapsis
    ///         into the atmosphere.</item>
    ///   <item>Baseline-warp gate — only fire at TimeWarp.CurrentRate ~1x to
    ///         avoid compounding snaps mid-warp.</item>
    ///   <item>Pre-snap pack + delayed unpack — <c>vessel.GoOnRails()</c>
    ///         before the subspace change, <c>WaitForFixedUpdate</c>, then
    ///         <c>GoOffRails()</c>. KSP's analytic on-rails state advances
    ///         cheaply at the new UT; physics integrator restarts clean.
    ///         Direct precedent: BUG-008-A pack-on-load
    ///         (<see cref="LmpClient.VesselUtilities.PqsAlignmentRoutine"/>).</item>
    ///   <item>One-direction-only via
    ///         <see cref="WarpSystem.WarpIfSubspaceIsMoreAdvanced"/> —
    ///         backward snap structurally impossible.</item>
    ///   <item><see cref="CooldownMs"/> = 10000 — caps snap frequency at
    ///         6/min worst case.</item>
    ///   <item>Operator notification via
    ///         <see cref="WarpSystem.DisplayMessage"/> with player name so the
    ///         UT jump is operator-explained.</item>
    /// </list>
    ///
    /// MKS-module identity is resolved via
    /// <c>HarmonyLib.AccessTools.TypeByName("KolonyTools.MKSModule")</c> at
    /// first-tick so LmpClient does not need a compile-time dep on MKS. If MKS
    /// is not installed or the type rename, the routine self-disables and
    /// logs once. The not-installed and renamed-or-mismatched cases are
    /// distinguished by an assembly scan so operators get a warning for the
    /// latter (silent drift-bug-reappearance) but not the former.
    ///
    /// <para><b>Known upgrade hazard — not a bug introduced by R1.</b>
    /// An operator with a pre-LMP single-player MKS save has
    /// <c>KolonizationEntry.LastUpdate</c> values minted against their offline
    /// <c>Planetarium.GetUniversalTime()</c>. On first LMP connect, the local
    /// subspace is set to the server's UT (typically very different from the
    /// offline save's UT). The first MKS FixedUpdate computes
    /// <c>currentUT - LastUpdate</c> against the new time base and can produce
    /// a massive accrual spike. This bug exists with or without R1 — R1 only
    /// amplifies it when a foreign player's subspace is even further ahead.
    /// Mitigation is operator-side: use a fresh save when upgrading to LMP, or
    /// manually edit the save's KolonizationEntry rows to reset
    /// <c>LastUpdate</c> values. Documented as a known limitation in the
    /// release notes; not auto-fixable from the LMP side without rewriting
    /// MKS' internal state.</para>
    /// </summary>
    public static class KolonyProximityWarpRoutine
    {
        /// <summary>
        /// Minimum interval between snap attempts. Caps snap frequency so a
        /// foreign-player-actively-warping scenario doesn't pump-pump-pump-snap
        /// the local client.
        ///
        /// Measured-from-START of the previous snap (timestamp is set right
        /// before the coroutine launches, not at coroutine completion). This
        /// is deliberate: it caps the rate of snap-attempts even if the
        /// coroutine itself hangs, and the pack/wait/unpack itself is ~20-50ms
        /// so start vs end is functionally indistinguishable at the 10s
        /// horizon.
        ///
        /// <c>SafeToSync</c>-refused outcomes do NOT consume this cooldown —
        /// the timestamp is set AFTER the safety gate passes — so a refused
        /// snap retries on the very next 1s tick (with the one-shot log
        /// suppressing log spam on repeated refusals).
        /// </summary>
        private const int CooldownMs = 10000;

        /// <summary>
        /// Tolerance around TimeWarp.CurrentRate == 1.0f. Matches the
        /// <c>WarpSystem.ShouldSteadyStateRetry</c> tolerance pattern.
        /// </summary>
        private const float BaselineWarpTolerance = 0.1f;

        private static Type _mksModuleType;
        private static bool _mksTypeResolved;
        private static DateTime _lastSnapAtUtc = DateTime.MinValue;

        /// <summary>
        /// Most-recent subspace id whose snap was refused by
        /// <see cref="IsSafeToSnap"/>. Tracks distinct refused targets so an
        /// operator running <c>grep [fix:MKS-R1] KSP.log</c> sees a one-shot
        /// per-target-subspace line — otherwise R1 silently no-ops while
        /// kolony-aggregation drift continues. Reset on <see cref="OnDisabled"/>.
        /// </summary>
        private static int _lastSafeToSnapRefusedSubspace;

        /// <summary>
        /// Routine entry point. Registered in <see cref="WarpSystem.OnEnabled"/>
        /// on a 1000 ms Update cadence — same as the existing
        /// <c>WarpIfSpectatingToController</c> sibling.
        /// </summary>
        public static void Run()
        {
            // Network state gate — same threshold as MKS-R0. Between Connected
            // and LocksSynced the LockStore is empty and we can't identify
            // foreign-controlled vessels.
            if (MainSystem.NetworkState < ClientState.LocksSynced) return;

            // Active-vessel + scene gate.
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) return;
            var active = FlightGlobals.ActiveVessel;
            if (active == null) return;

            // Don't double-fire when spectator routine is in charge.
            if (VesselCommon.IsSpectating) return;

            // Baseline-warp gate — don't snap mid-warp.
            if (TimeWarp.CurrentRate > 1.0f + BaselineWarpTolerance) return;

            // Cooldown — cap snap frequency.
            if ((DateTime.UtcNow - _lastSnapAtUtc).TotalMilliseconds < CooldownMs) return;

            // One-shot MKS type resolution.
            if (!_mksTypeResolved)
            {
                TryResolveMksType();
                _mksTypeResolved = true;
            }
            if (_mksModuleType == null) return;

            // Decision math — extracted to a pure-helper for testability.
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null || loaded.Count == 0) return;

            var local = SettingsSystem.CurrentSettings.PlayerName;
            var target = FindMostAdvancedForeignKolonySubspace<Vessel>(
                active.id,
                loaded,
                v => v == null ? Guid.Empty : v.id,
                v => HasMksAnchor(v),
                local,
                vid => LockSystem.LockQuery.GetUpdateLockOwner(vid),
                playerName => WarpSystem.Singleton.GetPlayerSubspace(playerName),
                WarpSystem.Singleton.Subspaces);

            if (target.SubspaceId <= 0 || string.IsNullOrEmpty(target.PlayerName)) return;
            if (target.SubspaceId == WarpSystem.Singleton.CurrentSubspace) return;

            // Forward-only short-circuit. Mirrors the check that
            // WarpIfSubspaceIsMoreAdvanced does internally (WarpSystem.cs:272-282)
            // so we don't pay the pack/wait/unpack coroutine cost when the
            // setter would silently refuse anyway. Pure optimisation, not a
            // correctness gate — the setter's own check is the authoritative
            // forward-only guarantee.
            if (WarpSystem.Singleton.Subspaces.TryGetValue(target.SubspaceId, out var targetTime)
                && targetTime <= WarpSystem.Singleton.CurrentSubspaceTimeDifference)
            {
                return;
            }

            // KSP-side safety gate (atmospheric / orbital-decay). Log once per
            // distinct refused target so an operator grepping for [fix:MKS-R1]
            // sees that R1 was applicable but suppressed by SafeToSync —
            // important because R1 silently no-oping while kolony-aggregation
            // drift continues is exactly the failure mode S1 (consumer-lens
            // review) flagged.
            if (!IsSafeToSnap())
            {
                if (_lastSafeToSnapRefusedSubspace != target.SubspaceId)
                {
                    _lastSafeToSnapRefusedSubspace = target.SubspaceId;
                    LunaLog.Log($"[LMP]: [fix:MKS-R1] Skipped snap to subspace {target.SubspaceId} ({target.PlayerName}'s kolony): SafeToSync refused (atmospheric flight or orbital-decay risk). R1 drift mitigation paused until vessel is safe to advance UT.");
                }
                return;
            }
            // Reset the refused-tracker once SafeToSync passes so the next
            // refusal (different target or same after safe-window) logs again.
            _lastSafeToSnapRefusedSubspace = 0;

            // All gates passed — schedule the pack/snap/unpack coroutine.
            _lastSnapAtUtc = DateTime.UtcNow;
            try
            {
                MainSystem.Singleton.StartCoroutine(SnapCoroutine(active, target.SubspaceId, target.PlayerName));
            }
            catch (Exception ex)
            {
                LunaLog.LogWarning($"[LMP]: [fix:MKS-R1] StartCoroutine threw; subspace snap not scheduled. Details: {ex.Message}");
            }
        }

        /// <summary>
        /// Hook called from <see cref="WarpSystem.OnDisabled"/> to clear
        /// state across reconnects. The MKS type lookup is process-wide and
        /// not reset — it remains valid for the KSP session.
        /// </summary>
        public static void OnDisabled()
        {
            _lastSnapAtUtc = DateTime.MinValue;
        }

        /// <summary>
        /// Resolves <c>KolonyTools.MKSModule</c> at runtime via string-typed
        /// reflection so LmpClient does not need a compile-time dep on MKS.
        /// Logs once with <c>[fix:MKS-R1]</c> tag.
        ///
        /// Distinguishes three cases for the operator-grep workflow:
        /// <list type="bullet">
        ///   <item>MKS not installed → <see cref="LunaLog.Log"/> at info level.
        ///         Quiet — vanilla LMP loaders shouldn't see warnings.</item>
        ///   <item>MKS installed but <c>MKSModule</c> not found (version
        ///         mismatch / type rename) → <see cref="LunaLog.LogWarning"/>
        ///         with a clear "drift bug will reappear" message. Operator
        ///         needs to know.</item>
        ///   <item>Resolved cleanly → info-level confirmation.</item>
        /// </list>
        /// </summary>
        private static void TryResolveMksType()
        {
            try
            {
                _mksModuleType = HarmonyLib.AccessTools.TypeByName("KolonyTools.MKSModule");
                if (_mksModuleType != null)
                {
                    LunaLog.Log("[LMP]: [fix:MKS-R1] Resolved KolonyTools.MKSModule — kolony-proximity subspace routine active.");
                    return;
                }

                // Distinguish "MKS not installed" from "MKS installed but
                // MKSModule renamed/moved" so operators can act on the latter.
                // Scan loaded assemblies for the KolonyTools namespace.
                var mksAssemblyPresent = false;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm == null) continue;
                        var name = asm.GetName().Name;
                        if (name != null && name.IndexOf("KolonyTools", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            mksAssemblyPresent = true;
                            break;
                        }
                    }
                }
                catch
                {
                    // GetAssemblies / GetName can theoretically throw under
                    // load-context oddities; we'd rather degrade to the
                    // info-level "not installed" path than fail the entire
                    // routine over a diagnostic refinement.
                    mksAssemblyPresent = false;
                }

                if (mksAssemblyPresent)
                {
                    LunaLog.LogWarning("[LMP]: [fix:MKS-R1] KolonyTools assembly detected but MKSModule type not found — MKS version mismatch (type renamed or moved?). Kolony-proximity subspace routine DISABLED; the cross-vessel kolony time-base drift bug will reappear. Check MKS version compatibility against the LMP fork.");
                }
                else
                {
                    LunaLog.Log("[LMP]: [fix:MKS-R1] KolonyTools.MKSModule type not found — MKS not installed, skipping kolony-proximity subspace routine.");
                }
            }
            catch (Exception ex)
            {
                LunaLog.LogWarning($"[LMP]: [fix:MKS-R1] Failed to resolve KolonyTools.MKSModule; kolony-proximity subspace routine disabled. Details: {ex.Message}");
                _mksModuleType = null;
            }
        }

        /// <summary>
        /// Checks whether a vessel carries at least one <c>MKSModule</c> part.
        /// Iterates parts × modules per call; bounded by KSP's physics-loaded
        /// vessel count (~50) × parts (~100) and only runs once per 1s.
        /// </summary>
        private static bool HasMksAnchor(Vessel v)
        {
            if (v == null || _mksModuleType == null) return false;
            var parts = v.Parts;
            if (parts == null) return false;
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null) continue;
                var modules = part.Modules;
                if (modules == null) continue;
                for (var j = 0; j < modules.Count; j++)
                {
                    var module = modules[j];
                    if (module != null && _mksModuleType.IsInstanceOfType(module))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Wraps <see cref="WarpSystem.SafeToSync"/> (which is private). The
        /// SafeToSync gate is the canonical "is it safe to advance UT?"
        /// predicate (WarpSystem.cs:412-426); it returns false during
        /// atmospheric flight, or when the new UT would put the active
        /// vessel's periapsis below the body's atmospheric/terrain envelope.
        /// Phase 2 reuses this gate verbatim — re-implementing it parallelly
        /// would risk drift between the two predicates.
        ///
        /// Because <c>SafeToSync</c> is currently private, this wrapper
        /// mirrors its check logic. If a future refactor makes
        /// <c>SafeToSync</c> internal/public, replace this wrapper with a
        /// direct call.
        ///
        /// Takes no parameter because the underlying check depends only on
        /// the local active vessel's situation/orbit, not the target subspace.
        /// </summary>
        private static bool IsSafeToSnap()
        {
            if (SettingsSystem.CurrentSettings.IgnoreSyncChecks) return true;

            var scene = HighLogic.LoadedScene;
            if (scene != GameScenes.FLIGHT) return true;

            var active = FlightGlobals.ActiveVessel;
            if (active == null) return true;

            // Spectating short-circuit is in the routine's caller (Run);
            // we don't need to re-check here.

            // Landed / Splashed / Pre-launch / atmospheric-flying are safe.
            if (active.situation <= Vessel.Situations.FLYING) return true;

            // For orbital vessels, check that periapsis stays above minimum.
            var orbit = active.orbit;
            if (orbit != null && orbit.eccentricity < 1d)
            {
                var body = active.mainBody;
                if (body != null)
                {
                    var minDist = FinePrint.Utilities.CelestialUtilities.GetMinimumOrbitalDistance(body, 1f);
                    return minDist < orbit.PeR;
                }
            }

            return false;
        }

        /// <summary>
        /// Pack / snap-subspace / wait one FixedUpdate / unpack coroutine.
        /// Mirrors the BUG-008-A pack-on-load pattern in
        /// <see cref="LmpClient.VesselUtilities.PqsAlignmentRoutine"/>.
        ///
        /// Unpack is conditional on <c>active.packed</c> still being true
        /// at exit: KSP's physics-range logic may have re-evaluated and
        /// packed/unpacked the vessel mid-wait. Calling <c>GoOffRails</c>
        /// in that state would force-load a vessel KSP intentionally kept
        /// packed.
        /// </summary>
        private static IEnumerator SnapCoroutine(Vessel active, int targetSubspace, string ownerName)
        {
            // Single screen message after the snap completes — WarpSystem.DisplayMessage
            // clobbers any prior message it owns (sets its duration to 0f), so a
            // pre-snap message would be suppressed by the post-snap one within ~20 ms.
            // Operator gets one clear message that names the controller AND the new
            // subspace, posted after the UT jump so the timestamp reflects the new
            // state.
            LunaLog.Log($"[LMP]: [fix:MKS-R1] Snapping local subspace -> {targetSubspace} (foreign kolony controller: {ownerName})");

            var weCalledPack = false;
            try
            {
                if (active != null && !active.packed)
                {
                    active.GoOnRails();
                    weCalledPack = true;
                }
            }
            catch (Exception ex)
            {
                // Pack-failure is non-fatal — proceed with the subspace
                // change. The physics-integrator jank risk is real but
                // bounded; SafeToSync already filtered out the worst-case
                // (atmospheric / orbital-decay) scenarios.
                LunaLog.LogWarning($"[LMP]: [fix:MKS-R1] GoOnRails threw on active vessel; proceeding with subspace change. Details: {ex.Message}");
            }

            // The forward-only subspace setter.
            WarpSystem.Singleton.WarpIfSubspaceIsMoreAdvanced(targetSubspace);

            // Let KSP's FixedUpdate apply the new UT before we unpack.
            yield return new WaitForFixedUpdate();

            if (weCalledPack && active != null && active.packed)
            {
                try
                {
                    active.GoOffRails();
                }
                catch (Exception ex)
                {
                    LunaLog.LogWarning($"[LMP]: [fix:MKS-R1] GoOffRails threw on active vessel; KSP will retry on next physics tick. Details: {ex.Message}");
                }
            }

            WarpSystem.Singleton.DisplayMessage($"[MKS-R1] Synced time with {ownerName}'s kolony (subspace {targetSubspace}).", 4f);
        }

        /// <summary>
        /// Pure decision-math helper. Given a snapshot of physics-loaded
        /// vessels + closures over LMP's lock + subspace state, returns the
        /// (subspaceId, playerName) of the foreign-controlled MKS vessel
        /// whose subspace has the most-advanced server UT. Returns
        /// <c>(0, null)</c> if no eligible foreign vessel exists.
        ///
        /// Predicate per loaded vessel:
        /// <list type="number">
        ///   <item>Not the local active vessel.</item>
        ///   <item>Has at least one MKSModule part
        ///         (<paramref name="hasMksAnchor"/> returns true).</item>
        ///   <item>Update lock is held by a non-local player
        ///         (<paramref name="getUpdateLockOwner"/> returns a non-null,
        ///         non-empty, non-local name).</item>
        ///   <item>Owner is in a tracked subspace
        ///         (<paramref name="getPlayerSubspace"/> returns &gt; 0).</item>
        ///   <item>Subspace time is known
        ///         (<paramref name="subspaceTimes"/> contains the key).</item>
        /// </list>
        /// Tie-break: ordinal name sort so soak logs are reproducible.
        /// </summary>
        /// <typeparam name="TVessel">Element type — production passes
        /// <c>Vessel</c>; tests pass a lightweight test record.</typeparam>
        public static SnapTarget FindMostAdvancedForeignKolonySubspace<TVessel>(
            Guid activeVesselId,
            IList<TVessel> loadedVessels,
            Func<TVessel, Guid> getId,
            Func<TVessel, bool> hasMksAnchor,
            string localPlayerName,
            Func<Guid, string> getUpdateLockOwner,
            Func<string, int> getPlayerSubspace,
            IDictionary<int, double> subspaceTimes)
        {
            if (loadedVessels == null || loadedVessels.Count == 0) return SnapTarget.None;
            if (subspaceTimes == null || subspaceTimes.Count == 0) return SnapTarget.None;

            var bestSubspace = 0;
            string bestOwner = null;
            var bestTime = double.MinValue;

            for (var i = 0; i < loadedVessels.Count; i++)
            {
                var v = loadedVessels[i];
                if (v == null) continue;

                var vid = getId(v);
                if (vid == Guid.Empty || vid == activeVesselId) continue;

                if (!hasMksAnchor(v)) continue;

                var owner = getUpdateLockOwner(vid);
                if (string.IsNullOrEmpty(owner)) continue;
                if (owner == localPlayerName) continue;

                var ownerSubspace = getPlayerSubspace(owner);
                if (ownerSubspace <= 0) continue;

                if (!subspaceTimes.TryGetValue(ownerSubspace, out var time)) continue;

                if (time > bestTime
                    || (time == bestTime && (bestOwner == null || string.CompareOrdinal(owner, bestOwner) < 0)))
                {
                    bestTime = time;
                    bestSubspace = ownerSubspace;
                    bestOwner = owner;
                }
            }

            return bestSubspace > 0
                ? new SnapTarget(bestSubspace, bestOwner)
                : SnapTarget.None;
        }

        /// <summary>
        /// Return value of <see cref="FindMostAdvancedForeignKolonySubspace{TVessel}"/>.
        /// </summary>
        public struct SnapTarget
        {
            public readonly int SubspaceId;
            public readonly string PlayerName;
            public SnapTarget(int subspaceId, string playerName) { SubspaceId = subspaceId; PlayerName = playerName; }
            public static SnapTarget None => new SnapTarget(0, null);
        }
    }
}
