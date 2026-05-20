using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice C] WOLF-R4 — Postfix on
    /// <c>WOLF.Route.RemoveResource(string resourceName, int quantity)</c>.
    /// Symmetric counterpart to <see cref="Route_AddResourcePostfix"/> —
    /// captures the per-Route <c>_resources</c> dict shrink (or full key
    /// removal at zero remaining quantity) into the wire so the server-
    /// side per-agency snapshot stays consistent with WOLF's local state.
    ///
    /// <para><b>Hook anchor.</b> <c>RemoveResource</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Route.cs:135-162</c>.
    /// WOLF removes the dict key entirely when the post-mutation count
    /// drops below 1 — our snapshot reflects that (the Resources list in
    /// the wire entry will be missing that key after the WOLF call
    /// returns). The router upsert is last-write-wins on the composite
    /// key, so the resource-removal effect propagates correctly.</para>
    ///
    /// <para><b>Gate, brittleness, cadence, and Negotiate-overlap notes</b>
    /// all match <see cref="Route_AddResourcePostfix"/>.</para>
    /// </summary>
    public static class Route_RemoveResourcePostfix
    {
        /// <summary>
        /// Postfix entry point. Harmony binds <c>__instance</c> to the
        /// <c>Route</c> instance on which <c>RemoveResource</c> was called.
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
                // Per-postfix isolation: the original RemoveResource
                // already ran; any failure here must not cascade.
            }
        }
    }
}
