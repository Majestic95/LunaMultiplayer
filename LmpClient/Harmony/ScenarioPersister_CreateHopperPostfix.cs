using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice D] WOLF-R4 — Postfix on
    /// <c>WOLF.ScenarioPersister.CreateHopper(IDepot depot, IRecipe recipe)</c>.
    /// Mirrors every hopper creation into the per-agency wire so the
    /// server-side <see cref="Server.System.Agency.AgencyWolfHopperRouter"/>
    /// can route + persist + echo + project the mutation out only to the
    /// owning agency.
    ///
    /// <para><b>Hook anchor.</b> <c>CreateHopper</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\ScenarioPersister.cs:95-101</c>.
    /// Returns <c>string</c> (the new HopperMetadata's Id). Harmony's
    /// parameter-name binding gives us <c>depot</c> + <c>recipe</c> + the
    /// returned Id via <c>__result</c>; we build the wire entry directly
    /// from these without a second reflection hop into the persister's
    /// internal Hoppers list.</para>
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6). Patch is registered
    /// imperatively via <see cref="LmpClient.Base.HarmonyPatcher.PatchWolfHopper"/>
    /// because <c>WOLF</c> is not a compile-time dep. <see cref="object"/>-
    /// typed <c>depot</c> + <c>recipe</c> parameters let Harmony bind to
    /// IDepot / IRecipe at runtime without LmpClient compiling against
    /// WOLF.dll. The patch is a no-op if WOLF isn't installed or the
    /// type/method was renamed — graceful degradation matches the MKS-R0 +
    /// R1 + R2 + Slice B-2 + Slice C self-disable pattern.</para>
    ///
    /// <para><b>Gate.</b> Under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = false</c>
    /// the postfix is a no-op (no wire emit, no local state mutation
    /// suppression). The legacy 30s SHA pass on <c>WOLF_ScenarioModule</c>
    /// covers shared-mode propagation unchanged — strict dual-mode silence.
    /// The Slice B-2 <c>IgnoredScenarios</c> filter
    /// (configured for <c>WOLF_ScenarioModule</c>) provides the
    /// counterpart broadcast suppression under gate=on so the postfix +
    /// the projector are the SOLE hopper-data path under per-agency mode.</para>
    /// </summary>
    public static class ScenarioPersister_CreateHopperPostfix
    {
        // Once-only Debug log gate. If WOLF's CreateHopper ever returns null
        // (internal validation failure / future-version regression), the
        // postfix silently drops the wire emit; without this gate the
        // operator would see a hopper in WOLF UI that never appears in
        // per-agency state and no signal in KSP.log. Integration-logic
        // review #2 — gate is static so it fires once per session per
        // postfix type rather than once per WOLF tick.
        private static bool _nullResultLogged;

        /// <summary>
        /// Postfix entry point. Harmony binds <c>depot</c> + <c>recipe</c>
        /// to the original method's parameters by name; <c>__result</c> is
        /// the returned Id string.
        /// </summary>
        internal static void Postfix(object depot, object recipe, string __result)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (string.IsNullOrEmpty(__result))
            {
                if (!_nullResultLogged)
                {
                    _nullResultLogged = true;
                    LunaLog.LogWarning("[LMP]: [fix:WOLF-R4] WOLF.ScenarioPersister.CreateHopper returned null/empty Id (once-only log) — per-agency wire emit suppressed. WOLF version mismatch?");
                }
                return;
            }

            try
            {
                var entry = WolfHopperReflection.BuildEntryFromComponents(__result, depot, recipe);
                if (entry == null) return;   // Resolution failure already logged once.

                AgencyWolfHopperSender.SendMutation(entry);
            }
            catch (Exception)
            {
                // Per-postfix isolation: the original CreateHopper already
                // ran; any failure here must not cascade. Logging is gated
                // to once-only inside WolfHopperReflection.
            }
        }
    }
}
