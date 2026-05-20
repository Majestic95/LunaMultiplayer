using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice D] WOLF-R4 — Postfix on
    /// <c>WOLF.ScenarioPersister.RemoveHopper(string id)</c>.
    /// Emits the removed Id through the
    /// <see cref="LmpCommon.Message.Data.Agency.AgencyWolfHopperStateMsgData.RemovedKeys"/>
    /// tail so the server-side
    /// <see cref="Server.System.Agency.AgencyWolfHopperRouter"/> drops the
    /// entry from <c>AgencyState.WolfHoppers</c>. Without this companion
    /// postfix the per-agency snapshot would stale-keep removed hoppers
    /// indefinitely.
    ///
    /// <para><b>Hook anchor.</b> <c>RemoveHopper</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\ScenarioPersister.cs:432-440</c>.
    /// Normal-operation API — the WOLF UI recipe-change flow calls
    /// <c>RemoveHopper</c> + <c>CreateHopper</c> as a pair when the operator
    /// picks a different recipe for an existing hopper module. The id-string
    /// is the only parameter; we forward it verbatim.</para>
    ///
    /// <para><b>Idempotent removal.</b> The server-side router's
    /// <c>RemovedKeys</c> loop drops keys not present in
    /// <see cref="Server.System.Agency.AgencyState.WolfHoppers"/> as a no-op
    /// — so a duplicate remove from a buggy mod doesn't error; it just
    /// doesn't echo.</para>
    ///
    /// <para><b>Gate.</b> Under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = false</c>
    /// the postfix is a no-op. Strict dual-mode silence.</para>
    /// </summary>
    public static class ScenarioPersister_RemoveHopperPostfix
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
                AgencyWolfHopperSender.SendRemoval(id);
            }
            catch (Exception)
            {
                // Per-postfix isolation: the original RemoveHopper already
                // ran; any failure here must not cascade.
            }
        }
    }
}
