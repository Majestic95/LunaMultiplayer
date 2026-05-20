using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice C] WOLF-R4 — Postfix on
    /// <c>WOLF.Route.AddResource(string resourceName, int quantity)</c>.
    /// Mirrors every route resource-allocation into the per-agency wire so
    /// the server-side
    /// <see cref="Server.System.Agency.AgencyWolfRouteRouter"/> sees the
    /// updated Resources list. The postfix reads <c>__instance</c> (the
    /// IRoute) and emits its full state (idempotent upsert — last-write-
    /// wins on the composite key).
    ///
    /// <para><b>Hook anchor.</b> <c>AddResource</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Route.cs:84-116</c>.
    /// Returns a <c>NegotiationResult</c>; we use the postfix purely to
    /// snapshot post-mutation state regardless of negotiation outcome —
    /// WOLF itself decides whether the resource was actually added, our
    /// snapshot reflects the resulting <c>_resources</c> dict.</para>
    ///
    /// <para><b>Note on internal Negotiate calls.</b> <c>AddResource</c>
    /// invokes <c>OriginDepot.NegotiateConsumer</c> and
    /// <c>DestinationDepot.NegotiateProvider</c>; those are already
    /// covered by Slice B-3's
    /// <see cref="Depot_NegotiateConsumerPostfix"/> /
    /// <see cref="Depot_NegotiateProviderPostfix"/> debounced postfixes
    /// (depot-side ResourceStreams). Slice C captures the orthogonal
    /// per-Route <c>_resources</c> mutation that the depot-side postfixes
    /// don't reach.</para>
    ///
    /// <para><b>Cadence.</b> Operator-driven (WOLF UI resource-allocation
    /// click). Low frequency — does NOT need the
    /// <see cref="WolfDepotDebouncer"/> machinery that Slice B-3 added for
    /// the 50 Hz Negotiate hot path.</para>
    ///
    /// <para><b>Gate + brittleness</b> match the create-route postfix —
    /// see <see cref="ScenarioPersister_CreateRoutePostfix"/>.</para>
    /// </summary>
    public static class Route_AddResourcePostfix
    {
        /// <summary>
        /// Postfix entry point. Harmony binds <c>__instance</c> to the
        /// <c>Route</c> instance on which <c>AddResource</c> was called.
        /// </summary>
        internal static void Postfix(object __instance)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__instance == null) return;

            try
            {
                var entry = WolfRouteReflection.BuildEntryFromRoute(__instance);
                if (entry == null) return;

                AgencyWolfRouteSender.SendMutation(entry);
            }
            catch (Exception)
            {
                // Per-postfix isolation: the original AddResource already
                // ran; any failure here must not cascade.
            }
        }
    }
}
