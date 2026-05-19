using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice B-2] WOLF-R4 — Postfix on <c>WOLF.Depot.Survey()</c>.
    /// Mirrors the depot's <c>IsSurveyed = true</c> state-flip into the
    /// per-agency wire.
    ///
    /// <para><b>Hook anchor.</b> <c>Survey()</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Depot.cs:266-271</c>.
    /// One-way transition (source comment: "Once a biome has been surveyed,
    /// it should never be unsurveyed"). Same emit shape as
    /// <see cref="Depot_EstablishPostfix"/>; server upserts idempotently
    /// on the same <c>(Body, Biome)</c> key.</para>
    ///
    /// <para>Same brittleness mitigation + gate behaviour as
    /// <see cref="ScenarioPersister_CreateDepotPostfix"/>.</para>
    /// </summary>
    public static class Depot_SurveyPostfix
    {
        internal static void Postfix(object __instance)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__instance == null) return;

            try
            {
                var entry = WolfDepotReflection.BuildEntryFromDepot(__instance);
                if (entry == null) return;
                AgencyWolfDepotSender.SendMutation(entry);
            }
            catch (Exception)
            {
                // Per-postfix isolation; logging gated once-only inside
                // WolfDepotReflection.
            }
        }
    }
}
