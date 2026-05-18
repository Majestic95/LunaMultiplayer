using System;
using System.Collections.Generic;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// MKS-R0 — Filters the depot / power-distributor lists that
    /// USITools.ModuleLogisticsConsumer iterates so it can only mutate the
    /// resource amounts on vessels whose Update lock is held by the local
    /// player.
    ///
    /// The problem: USITools' <c>ModuleLogisticsConsumer.CheckLogistics</c>
    /// fires every FixedUpdate on every consumer part. It calls
    /// <c>GetResourceStockpiles</c> / <c>GetPowerDistributors</c> to find
    /// nearby warehouses, then writes <c>pr.amount -= demand</c> (and
    /// symmetrically in <c>PushResources</c>: <c>res.amount += add</c>) on
    /// every PartResource of every vessel in the returned list. Without a
    /// filter, client A pumps resources on client B's vessel. B's periodic
    /// <c>VesselResourceMessageSender</c> pulse reverts A's local view of B,
    /// and the loop oscillates indefinitely.
    ///
    /// The fix: postfix the two list-returning anchors. After USI computes
    /// the unfiltered candidate list, remove every entry whose Update lock is
    /// NOT held by the local player. The consumer's own vessel survives the
    /// filter (the local player by definition holds Update on what they
    /// control), so legitimate self-scavenging is unaffected.
    ///
    /// Three load-bearing rules embedded in the filter predicate:
    /// <list type="number">
    ///   <item><c>v.id == Guid.Empty</c> is excluded — a vessel without an id
    ///         can't be looked up in <c>LockStore.UpdateLocks</c>; the
    ///         explicit guard makes the intent legible.</item>
    ///   <item>"No lock owner" is NOT permission. <c>UpdateLockBelongsToPlayer</c>
    ///         returns false when the lock doesn't exist. LMP issues Update
    ///         locks aggressively, so an unlocked window is microseconds;
    ///         treating that as authority to pump would race the next lock
    ///         claim.</item>
    ///   <item><c>MainSystem.NetworkState &lt; ClientState.LocksSynced</c>
    ///         short-circuits to a no-op so single-player MKS behaviour is
    ///         bit-for-bit preserved when LMP is loaded but not connected to a
    ///         server, AND the mid-session connect window (states
    ///         <c>Connected</c>..<c>SyncingLocks</c>) doesn't starve the
    ///         player on their own warehouses while <c>LockStore</c> is still
    ///         empty post-<c>OnDisabled</c>. <c>ContractPreLoader_Filter</c>
    ///         uses <c>&lt; Connected</c> because it runs only on scenario
    ///         load; this postfix fires every FixedUpdate so the tighter
    ///         <c>LocksSynced</c> gate matters.</item>
    /// </list>
    ///
    /// This patch is NOT applied via <c>[HarmonyPatch]</c> attributes because
    /// <c>USITools.ModuleLogisticsConsumer</c> is not a compile-time dependency
    /// of LmpClient. <see cref="LmpClient.Base.HarmonyPatcher.PatchModuleLogisticsConsumer"/>
    /// looks up the type at runtime and applies the postfixes imperatively. If
    /// USITools is not installed, the patch is a no-op (no log noise).
    ///
    /// Per-agency dovetail: under <c>PerAgencyCareer=true</c> + Career mode,
    /// Stage 5.17a already structurally prevents cross-agency Update lock
    /// acquires for vessels with a non-Empty <c>OwningAgencyId</c>. The
    /// Update-lock-held-by-local check therefore acts as the per-agency
    /// boundary for stamped vessels. For pre-0.31 vessels carrying the
    /// Unassigned sentinel (<c>OwningAgencyId = Guid.Empty</c>) Stage 5.17a
    /// allows any agency to take Update, so under Unassigned the filter's
    /// boundary collapses to "whoever holds the lock right now" — which is
    /// the right answer (mutual exclusion via lock, not via agency).
    ///
    /// Handoff timing: <c>LockSystem.AcquireLock</c> is async (server round-
    /// trip; local <c>LockStore</c> updates on reply, ~1-8 FixedUpdate ticks
    /// at typical RTT). The "Fly to remote vessel" handoff therefore has a
    /// brief window where the simulator switched but the filter still excludes
    /// the destination warehouse — pump-lag, never pump-corruption.
    /// <c>ReleaseLock</c> is the opposite: it removes from local <c>LockStore</c>
    /// before sending, so reverse handoff (releasing a borrowed warehouse)
    /// re-excludes immediately. Asymmetric, intentional.
    ///
    /// This is a CLIENT-ONLY fix. <c>ForkBuildInfo.ActiveFixes</c> carries
    /// <c>"MKS-R0"</c> for operator visibility, but no server-side enforcement
    /// is required or possible — USI mutates client-local <c>PartResource.amount</c>
    /// fields whose authoritative values come from the owning client's periodic
    /// <c>VesselResourceMsgData</c> broadcast.
    /// </summary>
    public static class ModuleLogisticsConsumer_DepotListPostfix
    {
        /// <summary>
        /// Harmony postfix applied imperatively to
        /// <c>USITools.ModuleLogisticsConsumer.GetResourceStockpiles()</c>.
        /// </summary>
        internal static void PostfixStockpiles(List<Vessel> __result)
        {
            ApplyFilter(__result);
        }

        /// <summary>
        /// Harmony postfix applied imperatively to
        /// <c>USITools.ModuleLogisticsConsumer.GetPowerDistributors()</c>.
        /// </summary>
        internal static void PostfixPower(List<Vessel> __result)
        {
            ApplyFilter(__result);
        }

        private static void ApplyFilter(List<Vessel> __result)
        {
            // Gate threshold is LocksSynced (~25), not Connected (~2): between
            // those two states LockStore.UpdateLocks is empty post-OnDisabled-
            // clear, so UpdateLockBelongsToPlayer returns false for every id
            // and the filter would starve the local player on their own
            // warehouses. LocksSynced is the actual precondition the filter
            // depends on; the boot-time HarmonyPatch log fires regardless of
            // gate, so operators still see patch evidence at startup.
            if (MainSystem.NetworkState < ClientState.LocksSynced) return;
            if (__result == null || __result.Count == 0) return;

            var local = SettingsSystem.CurrentSettings.PlayerName;
            // Two closure allocations per call (one capturing `local`, one
            // capturing the predicate args inside RemoveAll) — deliberate
            // trade-off for the pure-helper testability pattern (BUG-051b /
            // BUG-003-004 precedent). At FixedUpdate × N consumers cadence
            // this is ~64 KB/sec for a 10-consumer base — invisible against
            // KSP's MB/sec baseline. Eligible for a future perf pass if a
            // measured profile flags it.
            FilterToLocallyOwned<Vessel>(
                __result,
                v => v?.id ?? Guid.Empty,
                vid => LockSystem.LockQuery.UpdateLockBelongsToPlayer(vid, local));
        }

        /// <summary>
        /// Pure decision-math helper. Removes every entry from
        /// <paramref name="result"/> that is null OR whose id is
        /// <c>Guid.Empty</c> OR whose Update lock is not held by the local
        /// player. Designed for testability without standing up
        /// <c>LockSystem</c> / <c>SettingsSystem</c> singletons or constructing
        /// real <c>Vessel</c> instances — the <c>LmpClientTest</c> suite passes
        /// a custom record type with id projection.
        ///
        /// The player-name closure is the caller's responsibility — production
        /// reads <c>SettingsSystem.CurrentSettings.PlayerName</c> once at the
        /// callsite and closes it into <paramref name="isUpdateLockHeldByLocal"/>;
        /// tests pass an in-memory map.
        /// </summary>
        /// <typeparam name="T">List element type — production callsite is
        /// <c>Vessel</c>; tests use a lightweight test record.</typeparam>
        /// <param name="result">List to mutate in place. Null and empty lists
        /// are no-ops.</param>
        /// <param name="getId">Projects an element to its vessel id. Never
        /// called with a null element — the helper short-circuits null
        /// entries to "remove" before this projection runs.</param>
        /// <param name="isUpdateLockHeldByLocal">Returns true iff the local
        /// player holds the Update lock for the given vessel id. Returns
        /// false when no lock exists (this is the "no owner ≠ permission"
        /// rule).</param>
        public static void FilterToLocallyOwned<T>(
            List<T> result,
            Func<T, Guid> getId,
            Func<Guid, bool> isUpdateLockHeldByLocal)
        {
            if (result == null || result.Count == 0) return;
            result.RemoveAll(item =>
            {
                if (item == null) return true;
                var id = getId(item);
                if (id == Guid.Empty) return true;
                return !isUpdateLockHeldByLocal(id);
            });
        }
    }
}
