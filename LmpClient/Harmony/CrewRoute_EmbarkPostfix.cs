using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice E] WOLF-R4 — Postfix on
    /// <c>WOLF.CrewRoute.Embark(IPassenger passenger)</c>.
    /// Mirrors every passenger-add into the per-agency wire so the
    /// server-side <see cref="Server.System.Agency.AgencyWolfCrewRouter"/>
    /// can route + persist + echo + project the mutation out only to the
    /// owning agency.
    ///
    /// <para><b>Hook anchor.</b> <c>Embark</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\CrewRoute.cs:141-181</c>.
    /// Adds the passenger to the route's <c>Passengers</c> list when the
    /// route is in <c>FlightStatus.Boarding</c> AND berth capacity allows.
    /// Returns <c>bool</c> indicating success. The postfix reads
    /// <c>__instance</c> (the CrewRoute) regardless of the return value —
    /// when Embark returned false, the snapshot is unchanged from the
    /// prior state and the router upsert is idempotent (last-write-wins on
    /// the same UniqueId), so a no-op upsert is harmless and we keep the
    /// snapshot in sync without a branching cost.</para>
    ///
    /// <para><b>Where the cross-agency kerbal reject lives.</b> WOLF's
    /// local Embark has ALREADY added the passenger by the time this
    /// postfix runs (passenger is now in <c>__instance.Passengers</c>).
    /// The wire snapshot we emit therefore includes the freshly-added
    /// passenger. The SERVER-SIDE
    /// <see cref="Server.System.Agency.AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger"/>
    /// gate is what enforces the cross-agency reject — if Alice embarks
    /// Bob's kerbal, the server rejects the entire wire entry and the
    /// projector overwrites Alice's local CrewRoute UI back to the
    /// pre-Embark snapshot on the next <c>SendScenarioModules</c> tick
    /// (acceptable desync per pre-spec §8.f). A future Slice F prefix on
    /// <c>WOLF_CrewTransferScenario.Launch</c> can surface the rejection
    /// as a toast at click-time if operator demand surfaces; deferred
    /// until then.</para>
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6). Patch is
    /// registered imperatively via
    /// <see cref="LmpClient.Base.HarmonyPatcher.PatchWolfCrewRoute"/>.
    /// <see cref="object"/>-typed <c>__instance</c> + the <c>passenger</c>
    /// arg lets Harmony bind to the runtime types without a WOLF compile-
    /// time dependency. The <c>passenger</c> arg is accepted but unused
    /// here — we read the full passengers list from <c>__instance</c>
    /// rather than mutating a server-side delta, because the wire payload
    /// shape is full-snapshot REPLACE (matches Slice C/D semantics).</para>
    ///
    /// <para><b>Gate.</b> Same dual-mode silence as the other Slice E
    /// postfixes.</para>
    /// </summary>
    public static class CrewRoute_EmbarkPostfix
    {
        /// <summary>
        /// Postfix entry point. Harmony binds <c>__instance</c> to the
        /// CrewRoute the Embark method was called on; declared as
        /// <see cref="object"/> here so this file compiles without a WOLF
        /// reference.
        /// </summary>
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
                // Per-postfix isolation: the original Embark already ran;
                // any failure here must not cascade. Logging is gated to
                // once-only inside WolfCrewRouteReflection.
            }
        }
    }
}
