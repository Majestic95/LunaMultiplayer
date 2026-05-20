using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice D] WOLF-R4 — Postfix on
    /// <c>WOLF.ScenarioPersister.RemoveTerminal(string id)</c>.
    /// Emits the removed Id through the
    /// <see cref="LmpCommon.Message.Data.Agency.AgencyWolfTerminalStateMsgData.RemovedKeys"/>
    /// tail so the server-side
    /// <see cref="Server.System.Agency.AgencyWolfTerminalRouter"/> drops the
    /// entry from <c>AgencyState.WolfTerminals</c>. Companion to
    /// <see cref="ScenarioPersister_CreateTerminalPostfix"/>.
    ///
    /// <para><b>Hook anchor.</b> <c>RemoveTerminal</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\ScenarioPersister.cs:442-449</c>.
    /// Normal-operation API — the WOLF UI removes a terminal when the
    /// operator decommissions it.</para>
    ///
    /// <para><b>Idempotent removal.</b> Server-side router drops keys not
    /// present in <see cref="Server.System.Agency.AgencyState.WolfTerminals"/>
    /// as a no-op.</para>
    ///
    /// <para><b>Gate.</b> Strict dual-mode silence under
    /// <c>PerAgencyCareerEnabled = false</c>.</para>
    /// </summary>
    public static class ScenarioPersister_RemoveTerminalPostfix
    {
        /// <summary>
        /// Postfix entry point. Harmony binds <c>id</c> by name to the
        /// original method's <c>string id</c> parameter.
        /// </summary>
        internal static void Postfix(string id)
        {
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (string.IsNullOrEmpty(id)) return;

            try
            {
                AgencyWolfTerminalSender.SendRemoval(id);
            }
            catch (Exception)
            {
                // Per-postfix isolation.
            }
        }
    }
}
