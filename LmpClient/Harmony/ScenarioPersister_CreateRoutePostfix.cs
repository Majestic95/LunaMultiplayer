using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice C] WOLF-R4 — Postfix on
    /// <c>WOLF.ScenarioPersister.CreateRoute(string, string, string, string, int)</c>.
    /// Mirrors every route creation (or existing-route Payload increment)
    /// into the per-agency wire so the server-side
    /// <see cref="Server.System.Agency.AgencyWolfRouteRouter"/> can route +
    /// persist + echo + project the mutation out only to the owning agency.
    ///
    /// <para><b>Hook anchor.</b> <c>CreateRoute</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\ScenarioPersister.cs:103-138</c>.
    /// Returns an <c>IRoute</c> (newly-created OR the existing route after
    /// <c>IncreasePayload</c> — the persister is idempotent on the 4-string
    /// composite key). The postfix reads <c>__result</c> (the IRoute) and
    /// emits its state — same wire payload shape regardless of which branch
    /// ran. Idempotent emit matches the server-side router's idempotent
    /// upsert (last-write-wins on the same composite key).</para>
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6). Patch is registered
    /// imperatively via <see cref="LmpClient.Base.HarmonyPatcher.PatchWolfRoute"/>
    /// because <c>WOLF</c> is not a compile-time dep. <see cref="object"/>-
    /// typed parameter on the postfix lets Harmony bind to a private/unknown-
    /// at-compile-time type. The patch is a no-op if WOLF isn't installed
    /// or the type/method was renamed — graceful degradation matches the
    /// MKS-R0 + MKS-R1 + MKS-R2 + Slice B-2 self-disable pattern.</para>
    ///
    /// <para><b>Gate.</b> Under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = false</c>
    /// the postfix is a no-op (no wire emit, no local state mutation
    /// suppression). The legacy 30s SHA pass on <c>WOLF_ScenarioModule</c>
    /// covers shared-mode propagation unchanged — strict dual-mode
    /// silence. The Slice B-2 IgnoredScenarios Option B filter
    /// (configured for <c>WOLF_ScenarioModule</c>) provides the
    /// counterpart broadcast suppression under gate=on so the postfix
    /// triple + the projector are the SOLE route-data path under per-
    /// agency mode.</para>
    /// </summary>
    public static class ScenarioPersister_CreateRoutePostfix
    {
        /// <summary>
        /// Postfix entry point. Harmony binds <c>__result</c> to the
        /// <c>IRoute</c> returned by <c>CreateRoute</c>; declared as
        /// <see cref="object"/> here so this file compiles without a
        /// WOLF reference.
        /// </summary>
        internal static void Postfix(object __result)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__result == null) return;

            try
            {
                var entry = WolfRouteReflection.BuildEntryFromRoute(__result);
                if (entry == null) return;   // Resolution failure already logged once.

                AgencyWolfRouteSender.SendMutation(entry);
            }
            catch (Exception)
            {
                // Per-postfix isolation: the original CreateRoute already
                // ran; any failure here must not cascade. Logging is gated
                // to once-only inside WolfRouteReflection.
            }
        }
    }
}
