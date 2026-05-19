using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice B-2] WOLF-R4 — Postfix on <c>WOLF.Depot.Establish()</c>.
    /// Mirrors the depot's <c>IsEstablished = true</c> state-flip into the
    /// per-agency wire so peer per-agency clients (via projection) see the
    /// depot as established.
    ///
    /// <para><b>Hook anchor.</b> <c>Establish()</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Depot.cs:38-43</c>.
    /// One-way transition (<c>IsEstablished</c> has a private setter; the
    /// source comment notes "Once a depot is established, it should never
    /// be unestablished"). Postfix emits the full updated depot snapshot —
    /// the server-side router upserts idempotently on the same
    /// <c>(Body, Biome)</c> key.</para>
    ///
    /// <para>Same brittleness mitigation + gate behaviour as
    /// <see cref="ScenarioPersister_CreateDepotPostfix"/>.</para>
    /// </summary>
    public static class Depot_EstablishPostfix
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
