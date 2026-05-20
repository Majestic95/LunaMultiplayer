using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice E] WOLF-R4 — Postfix on
    /// <c>WOLF.ScenarioPersister.CreateCrewRoute(string, string, string, string, int, int, double)</c>.
    /// Mirrors every crew-route creation into the per-agency wire so the
    /// server-side <see cref="Server.System.Agency.AgencyWolfCrewRouter"/>
    /// can route + persist + echo + project the mutation out only to the
    /// owning agency.
    ///
    /// <para><b>Hook anchor.</b> <c>CreateCrewRoute</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\ScenarioPersister.cs:45-74</c>.
    /// Returns an <c>ICrewRoute</c> (newly-created route with empty
    /// passengers + <c>FlightStatus.Boarding</c>). The postfix reads
    /// <c>__result</c> and emits its initial snapshot — same wire shape
    /// as the Embark / Disembark / Launch postfixes; passengers are added
    /// LATER via <c>CrewRoute.Embark(IPassenger)</c>, so this create-time
    /// emit ships an empty passengers list.</para>
    ///
    /// <para><b>Why emit the empty snapshot.</b> Without this postfix, a
    /// CrewRoute that's created but never has Embark/Launch fired on it
    /// (operator created the route then left WOLF UI without assigning
    /// passengers) would only land in per-agency state on the next
    /// Embark — meaning a freshly-created empty route is INVISIBLE to
    /// reconnect catchup. Emitting the empty snapshot at create time
    /// pins the route's existence in <c>AgencyState.WolfCrewRoutes</c>
    /// immediately. The router upsert handles the "no passengers" case
    /// safely (no cross-agency check fires on an empty passengers list).</para>
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6). Patch is
    /// registered imperatively via
    /// <see cref="LmpClient.Base.HarmonyPatcher.PatchWolfCrewRoute"/>
    /// because <c>WOLF</c> is not a compile-time dep. <see cref="object"/>-
    /// typed parameter on the postfix lets Harmony bind to the
    /// <c>ICrewRoute</c> interface at runtime without LmpClient compiling
    /// against WOLF.dll. The patch is a no-op if WOLF isn't installed or
    /// the type/method was renamed — graceful degradation matches the
    /// MKS-R0 + R1 + R2 + Slice B-2 / C / D self-disable pattern.</para>
    ///
    /// <para><b>Gate.</b> Under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = false</c>
    /// the postfix is a no-op (no wire emit, no local state mutation
    /// suppression). The legacy 30s SHA pass on <c>WOLF_ScenarioModule</c>
    /// covers shared-mode propagation unchanged — strict dual-mode
    /// silence. The Slice B-2 <c>IgnoredScenarios</c> filter (configured
    /// for <c>WOLF_ScenarioModule</c>) provides the counterpart broadcast
    /// suppression under gate=on so the postfix quartet + the projector
    /// are the SOLE crew-route-data path under per-agency mode.</para>
    /// </summary>
    public static class ScenarioPersister_CreateCrewRoutePostfix
    {
        /// <summary>
        /// Postfix entry point. Harmony binds <c>__result</c> to the
        /// <c>ICrewRoute</c> returned by <c>CreateCrewRoute</c>; declared
        /// as <see cref="object"/> here so this file compiles without a
        /// WOLF reference.
        /// </summary>
        internal static void Postfix(object __result)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__result == null) return;

            try
            {
                var entry = WolfCrewRouteReflection.BuildEntryFromCrewRoute(__result);
                if (entry == null) return;   // Resolution failure already logged once.

                AgencyWolfCrewRouteSender.SendMutation(entry);
            }
            catch (Exception)
            {
                // Per-postfix isolation: the original CreateCrewRoute
                // already ran; any failure here must not cascade. Logging
                // is gated to once-only inside WolfCrewRouteReflection.
            }
        }
    }
}
