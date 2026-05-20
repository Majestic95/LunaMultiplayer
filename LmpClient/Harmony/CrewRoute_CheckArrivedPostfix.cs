using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice E] WOLF-R4 — Prefix + Postfix on
    /// <c>WOLF.CrewRoute.CheckArrived(double time)</c>.
    /// Detects the Enroute→Arrived FlightStatus auto-transition and mirrors
    /// it into the per-agency wire so the server-side
    /// <see cref="Server.System.Agency.AgencyWolfCrewRouter"/> + projector
    /// keep the requesting agency's CrewRoute view in sync after a route
    /// completes its travel without operator interaction.
    ///
    /// <para><b>Hook anchor.</b> <c>CheckArrived</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\CrewRoute.cs:105-121</c>.
    /// Called from <c>ScenarioPersister.GetCrewRoutes(double)</c> at
    /// <c>ScenarioPersister.cs:158-175</c> on every WOLF UI tick for
    /// every Enroute route. Mutates <c>FlightStatus</c> from
    /// <c>Enroute</c> to <c>Arrived</c> WITHOUT operator interaction
    /// (time-driven). Returns <c>true</c> when FlightStatus is now
    /// <c>Arrived</c> (either it was already, or we just transitioned);
    /// <c>false</c> in Boarding or still-Enroute states.</para>
    ///
    /// <para><b>Why this postfix exists (integration-logic review finding).</b>
    /// Without this hook, the per-agency state stays at
    /// <c>FlightStatus=Enroute</c> forever after the route completes
    /// travel — even though WOLF's local UI shows the route as
    /// <c>Arrived</c> and the operator can Disembark passengers locally.
    /// Server-side disk + projector keep emitting <c>Enroute</c> to peer
    /// clients (under the projector splice), and the kerbal-stranding
    /// boot diagnostic at <c>WarnAboutSharedWolfOnUpgrade</c> would
    /// over-count mid-flight passengers. The Slice E pre-spec missed
    /// this transition; caught by the integration-logic lens during
    /// multi-lens review. Eventually-consistent (next operator Disembark
    /// reconciles via the Disembark postfix), but the gap is real
    /// between Arrival and Disembark.</para>
    ///
    /// <para><b>Transition detection via Harmony __state pattern.</b>
    /// The prefix captures the FlightStatus BEFORE the call; the
    /// postfix compares against the FlightStatus AFTER. Emit only on
    /// the Enroute→Arrived transition — NOT on every WOLF UI tick
    /// where the route is already Arrived (which would otherwise
    /// produce ~1Hz wire spam per Arrived route). The Harmony __state
    /// parameter passes a string between prefix + postfix per
    /// HarmonyLib's documented mechanism.</para>
    ///
    /// <para><b>Brittleness mitigation + gate</b> same as sibling
    /// postfixes.</para>
    /// </summary>
    public static class CrewRoute_CheckArrivedPostfix
    {
        /// <summary>
        /// Captures <c>FlightStatus</c> BEFORE the call so the postfix can
        /// detect the Enroute→Arrived transition. <c>__state</c> is
        /// Harmony's per-call state-passing parameter — typed as
        /// <see cref="string"/> here (enum-name) so the postfix can do a
        /// simple equality check without reflecting twice.
        /// </summary>
        internal static void Prefix(object __instance, out string __state)
        {
            __state = null;
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__instance == null) return;

            try
            {
                __state = WolfCrewRouteReflection.ReadFlightStatus(__instance);
            }
            catch
            {
                // Per-postfix isolation: resolution failure is one-shot
                // logged inside the reflection cache.
            }
        }

        /// <summary>
        /// Emits the wire entry ONLY when the prefix observed Enroute and
        /// the post-call state is Arrived. All other state combinations
        /// (already-Arrived → still-Arrived, Boarding → still-Boarding,
        /// resolution-failure null states) are skipped — matches the
        /// "operator-driven cadence" cost discipline of sibling Slice E
        /// postfixes.
        /// </summary>
        internal static void Postfix(object __instance, string __state)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__instance == null) return;
            if (__state == null) return; // Prefix failed — silent skip.

            try
            {
                var newStatus = WolfCrewRouteReflection.ReadFlightStatus(__instance);
                if (newStatus == null) return;

                // Transition detection: prefix saw Enroute, postfix sees
                // Arrived. Exactly one emit per route completion across
                // the entire session (WOLF doesn't transition back
                // through Arrived once disembarked — it goes via
                // Boarding per CrewRoute.cs:134-137).
                if (__state == "Enroute" && newStatus == "Arrived")
                {
                    var entry = WolfCrewRouteReflection.BuildEntryFromCrewRoute(__instance);
                    if (entry == null) return;

                    AgencyWolfCrewRouteSender.SendMutation(entry);
                }
            }
            catch (Exception)
            {
                // Per-postfix isolation: the original CheckArrived already
                // ran; any failure here must not cascade. Reflection
                // failures are already one-shot logged.
            }
        }
    }
}
