using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice B-2] WOLF-R4 — Postfix on
    /// <c>WOLF.ScenarioPersister.CreateDepot(string body, string biome)</c>.
    /// Mirrors every depot creation into the per-agency wire so the
    /// server-side <see cref="Server.System.Agency.AgencyWolfDepotRouter"/>
    /// can route + persist + echo + project the mutation out only to the
    /// owning agency.
    ///
    /// <para><b>Hook anchor.</b> <c>CreateDepot</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\ScenarioPersister.cs:76-87</c>.
    /// Returns an <c>IDepot</c> (existing or newly-created — the persister
    /// is idempotent on <c>(body, biome)</c>). The postfix reads
    /// <c>__result</c> (the IDepot) and emits its state — same wire payload
    /// shape regardless of whether a new depot was minted or an existing
    /// one returned. Idempotent emit matches the server-side router's
    /// idempotent upsert (last-write-wins on the same Body|Biome key).</para>
    ///
    /// <para><b>Brittleness mitigation (pre-spec §6).</b> Patch is registered
    /// imperatively via <see cref="LmpClient.Base.HarmonyPatcher.PatchWolfDepot"/>
    /// because <c>WOLF</c> is not a compile-time dep. <see cref="object"/>-
    /// typed parameter on the postfix lets Harmony bind to a private/unknown-
    /// at-compile-time type. The patch is a no-op if WOLF isn't installed
    /// or the type/method was renamed — graceful degradation matches the
    /// MKS-R0 + MKS-R1 + MKS-R2 self-disable pattern.</para>
    ///
    /// <para><b>Gate.</b> Under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = false</c>
    /// the postfix is a no-op (no wire emit, no local state mutation
    /// suppression). The legacy 30s SHA pass on <c>WOLF_ScenarioModule</c>
    /// covers shared-mode propagation unchanged — strict dual-mode
    /// silence. The <c>IgnoredScenarios</c> Option B filter shipped in
    /// Slice B-2 alongside this postfix provides the counterpart broadcast
    /// suppression under gate=on so the postfix + the projector are the
    /// SOLE depot-data path under per-agency mode.</para>
    /// </summary>
    public static class ScenarioPersister_CreateDepotPostfix
    {
        /// <summary>
        /// Postfix entry point. Harmony binds <c>__result</c> to the
        /// <c>IDepot</c> returned by <c>CreateDepot</c>; declared as
        /// <see cref="object"/> here so this file compiles without a
        /// WOLF reference.
        /// </summary>
        internal static void Postfix(object __result)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__result == null) return;

            try
            {
                var entry = WolfDepotReflection.BuildEntryFromDepot(__result);
                if (entry == null) return;   // Resolution failure already logged once.

                AgencyWolfDepotSender.SendMutation(entry);
            }
            catch (Exception)
            {
                // Per-postfix isolation: the original CreateDepot already
                // ran; any failure here must not cascade. Logging is
                // gated to once-only inside WolfDepotReflection.
            }
        }
    }
}
