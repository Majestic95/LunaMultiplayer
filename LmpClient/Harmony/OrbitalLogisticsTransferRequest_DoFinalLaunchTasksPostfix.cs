using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Message.Data.Agency;
using System;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 3 Slice D-2] MKS-R2 — Postfix on
    /// <c>KolonyTools.OrbitalLogisticsTransferRequest.DoFinalLaunchTasks</c>
    /// (protected). Emits a per-agency wire echo with
    /// <c>Status=Launched</c> after MKS' own implementation sets the field
    /// (<c>OrbitalLogisticsTransferRequest.cs:713</c>).
    ///
    /// <para><b>Why <c>DoFinalLaunchTasks</c>, not <c>DoLaunchTasks</c>.</b>
    /// MKS has two entry points to a Launched transition:</para>
    /// <list type="bullet">
    ///   <item><b>Fresh launch:</b> public <c>Launch</c> at line 236 calls
    ///        protected <c>DoLaunchTasks</c> at line 262, which calls
    ///        protected <c>DoFinalLaunchTasks</c> at line 700. The
    ///        <c>DoLaunchTasks</c> path deducts resources from Origin then
    ///        delegates the Status=Launched flip to
    ///        <c>DoFinalLaunchTasks</c>.</item>
    ///   <item><b>Resumed from Returning:</b> public <c>Launch</c> at line
    ///        236 calls <c>DoFinalLaunchTasks</c> DIRECTLY at line 248
    ///        (skipping resource deduction because the transfer was
    ///        already Launched once before <c>Abort</c> made it
    ///        Returning).</item>
    /// </list>
    /// <para>Hooking <c>DoLaunchTasks</c> would miss the resumed-launch
    /// path entirely. <c>DoFinalLaunchTasks</c> is the union of both —
    /// single anchor for every Status=Launched transition.</para>
    ///
    /// <para><b>Protected method anchor.</b> <c>DoFinalLaunchTasks</c> is
    /// <c>protected void</c>. Harmony patches protected methods fine via
    /// <c>AccessTools.Method(..., BindingFlags.Instance | BindingFlags.NonPublic)</c>
    /// in <see cref="LmpClient.Base.HarmonyPatcher.PatchOrbitalLogisticsTransferRequest"/>.
    /// Brittleness mitigation: a future MKS rename triggers a single
    /// <c>[fix:MKS-R2]</c> warning at boot and this postfix becomes a
    /// no-op for the session.</para>
    ///
    /// <para><b>Gate=off no-op.</b> Under
    /// <c>PerAgencyCareerEnabled=false</c> the postfix returns immediately
    /// — no per-agency wire to feed, the legacy 30s SHA scenario broadcast
    /// carries Launched state to all peers naturally. Dual-mode silence.</para>
    /// </summary>
    public static class OrbitalLogisticsTransferRequest_DoFinalLaunchTasksPostfix
    {
        internal static void Postfix(object __instance)
        {
            if (__instance == null) return;
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;

            try
            {
                if (!OrbitalLogisticsReflection.TryBuildEntry(__instance, out var entry))
                    return;

                // Sanity check — DoFinalLaunchTasks just set Status=Launched
                // at MKS line 713. If reflection read something else, MKS'
                // shape diverged from the pinned SHA — skip the emit so we
                // don't ship a junk state.
                if (entry.Status != AgencyOrbitalTransferEntry.StatusLaunched)
                    return;

                AgencyOrbitalSender.SendTransferStateChange(entry);
            }
            catch (Exception ex)
            {
                OrbitalLogisticsReflection.LogRuntimeFailureOnce("DoFinalLaunchTasksPostfix", ex);
            }
        }
    }
}
