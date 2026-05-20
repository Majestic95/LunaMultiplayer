using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice B-3] WOLF-R4 — Postfix on
    /// <c>WOLF.Depot.NegotiateConsumer(Dictionary&lt;string, int&gt;)</c>.
    /// Mirrors resource-stream Outgoing mutations into the per-agency wire.
    ///
    /// <para><b>Hook anchor.</b> <c>NegotiateConsumer</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Depot.cs:183-217</c>.
    /// Mutates <c>_resourceStreams[name].Outgoing</c> via increment at
    /// <c>:211</c>. Same hot-path cadence as
    /// <see cref="Depot_NegotiateProviderPostfix"/> — debounced through
    /// <see cref="WolfDepotDebouncer"/>.</para>
    ///
    /// <para>Same brittleness mitigation + gate behaviour as the Slice B-2
    /// postfixes.</para>
    /// </summary>
    public static class Depot_NegotiateConsumerPostfix
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
