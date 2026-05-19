using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Message.Data.Agency;
using System;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 3 Slice D-2] MKS-R2 — Postfix on
    /// <c>KolonyTools.OrbitalLogisticsTransferRequest.Abort()</c>. Emits a
    /// per-agency wire echo with <c>Status=Returning</c> when stock
    /// <c>Abort</c> at <c>OrbitalLogisticsTransferRequest.cs:363-369</c>
    /// performs the Launched → Returning transition.
    ///
    /// <para><b>Conditional transition.</b> Stock <c>Abort</c> only mutates
    /// Status when current Status == Launched (MKS line 365). Calling
    /// <c>Abort</c> on a PreLaunch / Cancelled / Delivered / etc. transfer
    /// is a no-op. The postfix reads Status after stock ran; if it's not
    /// Returning, the call was a no-op and there's nothing to echo.</para>
    ///
    /// <para><b>Public method anchor.</b> <c>Abort</c> is
    /// <c>public void Abort()</c> — lower brittleness than the protected
    /// <c>DoFinalLaunchTasks</c>. A future MKS rename would still trigger
    /// graceful no-op + boot warning via the <see cref="LmpClient.Base.HarmonyPatcher"/>
    /// imperative registration.</para>
    ///
    /// <para><b>Gate=off no-op.</b> Under
    /// <c>PerAgencyCareerEnabled=false</c> the postfix returns immediately
    /// — the legacy 30s SHA scenario broadcast carries Returning state to
    /// all peers naturally. Dual-mode silence.</para>
    ///
    /// <para><b>Why not also patch <c>AbortTransfer</c>?</b>
    /// <c>ScenarioOrbitalLogistics.AbortTransfer</c> at MKS line 209-219
    /// is the UI entry point — it calls <c>transfer.Abort()</c> for
    /// pending transfers (line 217). Patching <c>Abort</c> on the request
    /// type itself catches every entry point uniformly. Same shape as
    /// Slice B's <c>KolonizationManager.TrackLogEntry</c> manager-side
    /// anchor (which catches both direct calls AND
    /// <c>ModuleColonyRewards</c>-driven calls).</para>
    /// </summary>
    public static class OrbitalLogisticsTransferRequest_AbortPostfix
    {
        internal static void Postfix(object __instance)
        {
            if (__instance == null) return;
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;

            try
            {
                if (!OrbitalLogisticsReflection.TryBuildEntry(__instance, out var entry))
                    return;

                // Stock Abort only flips Status when it was Launched. If we
                // observe anything else, the call was a no-op (Status was
                // already Returning / Delivered / etc.) and there's no
                // transition to echo. Skip the emit.
                if (entry.Status != AgencyOrbitalTransferEntry.StatusReturning)
                    return;

                AgencyOrbitalSender.SendTransferStateChange(entry);
            }
            catch (Exception ex)
            {
                OrbitalLogisticsReflection.LogRuntimeFailureOnce("AbortPostfix", ex);
            }
        }
    }
}
