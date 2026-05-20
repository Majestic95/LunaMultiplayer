using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice B-3] WOLF-R4 — Postfix on
    /// <c>WOLF.Depot.NegotiateProvider(Dictionary&lt;string, int&gt;)</c>.
    /// Mirrors resource-stream Incoming/Outgoing mutations into the
    /// per-agency wire so peer per-agency clients (via projection) see the
    /// depot's production state.
    ///
    /// <para><b>Hook anchor.</b> <c>NegotiateProvider</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Depot.cs:131-181</c>.
    /// Mutates <c>_resourceStreams[name].Incoming</c> via increment at
    /// <c>:173</c>. Fires per-tick on every active producer-side recipe
    /// during MKS' resource conversion — hot path, debounced via
    /// <see cref="WolfDepotDebouncer"/> per pre-spec §3.e.</para>
    ///
    /// <para><b>Debounce semantics.</b> Enqueue stores the depot's CURRENT
    /// state under <c>(Body, Biome)</c> key, replacing any pending entry.
    /// Latest-wins — by the time the debouncer flushes (every 1s on the
    /// next Negotiate that crosses the interval), the dispatched snapshot
    /// reflects the depot's final state for that flush window. The flush
    /// itself runs inline on the same FixedUpdate that triggered it; no
    /// background timer.</para>
    ///
    /// <para>Same brittleness mitigation + gate behaviour as the Slice B-2
    /// postfixes (<see cref="ScenarioPersister_CreateDepotPostfix"/> et al.).</para>
    /// </summary>
    public static class Depot_NegotiateProviderPostfix
    {
        internal static void Postfix(object __instance)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__instance == null) return;

            try
            {
                var entry = WolfDepotReflection.BuildEntryFromDepot(__instance);
                if (entry == null) return;
                WolfDepotDebouncer.EnqueueAndMaybeFlush(entry);
            }
            catch (Exception)
            {
                // Per-postfix isolation; logging gated once-only inside
                // WolfDepotReflection.
            }
        }
    }
}
