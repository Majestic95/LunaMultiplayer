using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;
using System.Reflection;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice D] WOLF-R4 — Postfix on
    /// <c>WOLF.ScenarioPersister.CreateTerminal(IDepot depot)</c>.
    /// Mirrors every terminal creation into the per-agency wire so the
    /// server-side <see cref="Server.System.Agency.AgencyWolfTerminalRouter"/>
    /// can route + persist + echo + project the mutation out only to the
    /// owning agency.
    ///
    /// <para><b>Hook anchor.</b> <c>CreateTerminal</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\ScenarioPersister.cs:145-151</c>.
    /// Returns <c>string</c> (the new TerminalMetadata's Id — Guid in
    /// <c>ToString("N")</c> form, no hyphens). The postfix reads
    /// <c>__result</c> (the Id) plus the original method's IDepot parameter
    /// (Body/Biome come from the depot directly — TerminalMetadata's
    /// constructor stores them at <c>TerminalMetadata.cs:13-18</c>).</para>
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6). Patch is registered
    /// imperatively via <see cref="LmpClient.Base.HarmonyPatcher.PatchWolfTerminal"/>.
    /// <see cref="object"/>-typed <c>depot</c> parameter lets Harmony bind
    /// to IDepot at runtime. Graceful degradation matches the MKS-R0 + R1
    /// + R2 + Slice B-2 + Slice C precedents.</para>
    ///
    /// <para><b>Gate.</b> Under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = false</c>
    /// the postfix is a no-op. Strict dual-mode silence.</para>
    /// </summary>
    public static class ScenarioPersister_CreateTerminalPostfix
    {
        // Lightweight cached IDepot.Body / IDepot.Biome accessors. The
        // CreateTerminal postfix doesn't go through
        // WolfTerminalReflection.BuildEntryFromTerminal (no TerminalMetadata
        // instance available) — we build the entry inline here from the Id +
        // Depot.Body + Depot.Biome.
        private static PropertyInfo _depotBody;
        private static PropertyInfo _depotBiome;
        private static bool _resolved;
        private static bool _resolveFailed;

        // Once-only Debug log gate. See ScenarioPersister_CreateHopperPostfix
        // for rationale (integration-logic review #2).
        private static bool _nullResultLogged;

        /// <summary>
        /// Postfix entry point. Harmony binds <c>depot</c> by name to the
        /// original method's <c>IDepot depot</c> parameter; <c>__result</c>
        /// is the returned Id string.
        /// </summary>
        internal static void Postfix(object depot, string __result)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (string.IsNullOrEmpty(__result) || depot == null)
            {
                if (string.IsNullOrEmpty(__result) && !_nullResultLogged)
                {
                    _nullResultLogged = true;
                    LunaLog.LogWarning("[LMP]: [fix:WOLF-R4] WOLF.ScenarioPersister.CreateTerminal returned null/empty Id (once-only log) — per-agency wire emit suppressed. WOLF version mismatch?");
                }
                return;
            }

            try
            {
                if (!TryResolveDepotAccessors(depot.GetType())) return;

                var entry = new LmpCommon.Message.Data.Agency.AgencyWolfTerminalEntry
                {
                    Id = __result,
                    Body = (string)_depotBody.GetValue(depot) ?? string.Empty,
                    Biome = (string)_depotBiome.GetValue(depot) ?? string.Empty,
                };
                AgencyWolfTerminalSender.SendMutation(entry);
            }
            catch (Exception)
            {
                // Per-postfix isolation: the original CreateTerminal already
                // ran; any failure here must not cascade.
            }
        }

        private static bool TryResolveDepotAccessors(System.Type depotType)
        {
            if (_resolved) return true;
            if (_resolveFailed) return false;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                _depotBody = depotType.GetProperty("Body", flags) ?? throw new InvalidOperationException("IDepot.Body property not found");
                _depotBiome = depotType.GetProperty("Biome", flags) ?? throw new InvalidOperationException("IDepot.Biome property not found");
                _resolved = true;
                return true;
            }
            catch (Exception e)
            {
                _resolveFailed = true;
                LunaLog.LogWarning($"[LMP]: [fix:WOLF-R4] WOLF Terminal depot reflection resolve failed: {e.Message}. Per-agency WOLF terminal routing DISABLED for this session — WOLF version mismatch?");
                return false;
            }
        }
    }
}
