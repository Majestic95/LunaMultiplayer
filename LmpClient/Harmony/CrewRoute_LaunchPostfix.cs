using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice E] WOLF-R4 — Postfix on
    /// <c>WOLF.CrewRoute.Launch(double now)</c>.
    /// Mirrors the Boarding → Enroute FlightStatus transition (with the
    /// freshly-computed ArrivalTime) into the per-agency wire so the
    /// server-side <see cref="Server.System.Agency.AgencyWolfCrewRouter"/>
    /// + projector keep the requesting agency's CrewRoute view in sync.
    ///
    /// <para><b>Hook anchor.</b> <c>Launch</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\CrewRoute.cs:183-196</c>.
    /// Transitions <c>FlightStatus</c> from <c>Boarding</c> to
    /// <c>Enroute</c> + sets <c>ArrivalTime = now + Duration</c>. The
    /// passenger list is locked-in at this point (Embark only accepts
    /// passengers when FlightStatus is Boarding per CrewRoute.cs:143-145).
    /// Snapshot at this point captures the full Enroute state.</para>
    ///
    /// <para><b>Why we also need this postfix in addition to Embark.</b>
    /// FlightStatus + ArrivalTime aren't mutated by Embark — they're set
    /// HERE. Without the Launch postfix, the per-agency state would still
    /// show <c>FlightStatus=Boarding</c> after the operator clicks
    /// Launch in WOLF UI. The projector would emit Boarding to the
    /// outgoing scenario blob, and on next scene load the kerbals would
    /// re-appear at the origin instead of correctly tracking toward the
    /// destination Arrival.</para>
    ///
    /// <para><b>Pure ApprovedAccept; no cross-agency check.</b> Launch is
    /// a state transition on a route that has ALREADY been Embarked into;
    /// any cross-agency reject would have fired at the Embark postfix
    /// (server-side per-passenger check). Launch can only LOCK IN whatever
    /// passenger list survived prior Embarks. The router's idempotent
    /// upsert handles this safely — same UniqueId key, updated
    /// FlightStatus + ArrivalTime fields.</para>
    ///
    /// <para><b>Brittleness mitigation + gate</b> same as
    /// <see cref="CrewRoute_EmbarkPostfix"/>.</para>
    /// </summary>
    public static class CrewRoute_LaunchPostfix
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
                // Per-postfix isolation: the original Launch already ran;
                // any failure here must not cascade.
            }
        }
    }
}
