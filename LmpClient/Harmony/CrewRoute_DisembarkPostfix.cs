using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice E] WOLF-R4 — Postfix on
    /// <c>WOLF.CrewRoute.Disembark(IPassenger passenger)</c>.
    /// Mirrors every passenger-remove into the per-agency wire so the
    /// server-side <see cref="Server.System.Agency.AgencyWolfCrewRouter"/>
    /// can route + persist + echo + project the mutation out only to the
    /// owning agency.
    ///
    /// <para><b>Hook anchor.</b> <c>Disembark</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\CrewRoute.cs:123-139</c>.
    /// Removes the passenger from <c>__instance.Passengers</c> when the
    /// route is in <c>FlightStatus.Arrived</c> AND the passenger is on
    /// the route. If the resulting list is empty, FlightStatus transitions
    /// Arrived → Boarding (per CrewRoute.cs:134-137). Both cases ship the
    /// full-snapshot wire emit — REPLACE semantics handle the FlightStatus
    /// transition uniformly.</para>
    ///
    /// <para><b>No cross-agency check on Disembark.</b> A removal is
    /// always operator-safe: the operator is removing a passenger from
    /// their own (or a stale-state) route. No griefing vector. Server-
    /// side router runs the standard upsert; if the route was previously
    /// cross-agency-rejected and the operator is now Disembarking, the
    /// upsert lands a smaller passenger list which can only IMPROVE the
    /// cross-agency situation (fewer passengers to potentially reject on
    /// next Embark).</para>
    ///
    /// <para><b>Brittleness mitigation + gate</b> same as
    /// <see cref="CrewRoute_EmbarkPostfix"/>.</para>
    /// </summary>
    public static class CrewRoute_DisembarkPostfix
    {
        internal static void Postfix(object __instance)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__instance == null) return;

            try
            {
                var entry = WolfCrewRouteReflection.BuildEntryFromCrewRoute(__instance);
                if (entry == null) return;

                AgencyWolfCrewRouteSender.SendMutation(entry);
            }
            catch (Exception)
            {
                // Per-postfix isolation: the original Disembark already
                // ran; any failure here must not cascade.
            }
        }
    }
}
