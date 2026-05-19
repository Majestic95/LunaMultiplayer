using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Message.Data.Agency;
using System;
using System.Collections;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 3 Slice D-2] MKS-R2 — Postfix on
    /// <c>KolonyTools.OrbitalLogisticsTransferRequest.Deliver()</c>. Wraps
    /// the returned <see cref="IEnumerator"/> with a completion-detection
    /// wrapper that observes the terminal Status (Delivered / Partial /
    /// Failed / Cancelled) once the inner coroutine finishes, then emits a
    /// per-agency wire echo via <see cref="AgencyOrbitalSender"/>.
    ///
    /// <para><b>Why IEnumerator-wrapper instead of <c>[HarmonyPatch("set_Status")]</c>.</b>
    /// MKS' <c>Status</c> is a public FIELD at
    /// <c>OrbitalLogisticsTransferRequest.cs:78</c>, not a property — there
    /// is no <c>set_Status</c> setter to patch. The terminal Status
    /// transitions (Delivered at MKS line 351, Partial at 354, Failed at
    /// 291/299/308/317, Cancelled at 347) all happen INSIDE the
    /// <c>Deliver</c> coroutine body. The canonical Harmony technique for
    /// observing coroutine completion is to wrap the returned
    /// <see cref="IEnumerator"/> in a postfix and yield through it; when
    /// <c>MoveNext</c> returns false, the inner coroutine has completed and
    /// Status is terminal.</para>
    ///
    /// <para><b>Skip-path interaction with <see cref="OrbitalLogisticsTransferRequest_DeliverPrefix"/>.</b>
    /// When the prefix returns false (delegated peer), Harmony's default
    /// behaviour is to skip the original method body AND still invoke
    /// postfixes. For a method returning <see cref="IEnumerator"/>,
    /// <c>__result</c> on the skip path is the default value — <c>null</c>.
    /// The wrap is conditional on <c>__result != null</c>: skip-path
    /// invocations leave <c>__result</c> at null so the wrap is bypassed.
    /// The delegated peer's "terminal" Status echo (Failed via the prefix
    /// mutation) is not relevant to the server's per-agency state —
    /// state-of-record is the OWNING peer's Deliver-completion observed
    /// here.</para>
    ///
    /// <para><b>Gate gate=on emit-only.</b> Under gate=off there's no
    /// per-agency wire to feed; the legacy 30s SHA scenario broadcast
    /// carries the terminal state to all peers naturally. The wrap still
    /// runs to consume the inner coroutine (we must yield through it for
    /// stock Deliver to execute) but the post-completion send is gated.
    /// Same dual-mode silence shape as the Slice B / C postfixes.</para>
    ///
    /// <para><b>Exception isolation.</b> The wrapper catches any exception
    /// from the inner <c>MoveNext</c> + logs once via
    /// <see cref="OrbitalLogisticsReflection.LogRuntimeFailureOnce"/>; the
    /// wrap terminates cleanly so Unity's coroutine machinery doesn't see
    /// a leaked exception. The finally block emits the per-agency echo
    /// regardless of whether the inner completed normally or threw — the
    /// terminal Status was set by stock Deliver before the throw (the
    /// failure paths set Status=Failed + <c>yield break</c>; the normal
    /// completion sets Status=Delivered/Partial/Cancelled then falls
    /// through).</para>
    /// </summary>
    public static class OrbitalLogisticsTransferRequest_DeliverPostfix
    {
        /// <summary>
        /// Harmony postfix entry point. <see cref="HarmonyLib.HarmonyMethod"/>
        /// binds <c>__instance</c> and the <c>ref IEnumerator __result</c>
        /// parameter positionally; the ref allows us to replace the
        /// returned coroutine with our wrapper.
        /// </summary>
        internal static void Postfix(object __instance, ref IEnumerator __result)
        {
            // Skip-path bypass — the prefix returned false, no IEnumerator
            // to wrap. The delegating peer already mutated Status=Failed in
            // the prefix; no wire emit needed.
            if (__instance == null || __result == null) return;

            try
            {
                __result = WrapWithCompletionEcho(__instance, __result);
            }
            catch (Exception ex)
            {
                // Wrap construction failure — leave __result alone so stock
                // Deliver still runs. The terminal-Status echo gets dropped
                // for this transfer; next 30s scenario sync (gate=off) or
                // next reconnect catchup (gate=on) brings the state current.
                OrbitalLogisticsReflection.LogRuntimeFailureOnce("DeliverPostfix-wrap", ex);
            }
        }

        /// <summary>
        /// IEnumerator wrapper that yields through the inner coroutine and
        /// emits the per-agency wire echo on completion. Uses
        /// <c>try/finally</c> around the MoveNext loop because C# forbids
        /// <c>yield return</c> inside <c>try</c>-with-<c>catch</c> but
        /// allows it inside <c>try</c>-with-<c>finally</c>.
        /// </summary>
        private static IEnumerator WrapWithCompletionEcho(object instance, IEnumerator inner)
        {
            try
            {
                while (true)
                {
                    bool hasMore;
                    try
                    {
                        hasMore = inner.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        OrbitalLogisticsReflection.LogRuntimeFailureOnce("DeliverInner-MoveNext", ex);
                        // Inner threw mid-coroutine — terminate the wrap so
                        // Unity doesn't see a leaked exception. The finally
                        // block still runs and emits whatever terminal
                        // Status stock had set up to the throw point.
                        yield break;
                    }
                    if (!hasMore) yield break;
                    yield return inner.Current;
                }
            }
            finally
            {
                // Emit only under gate=on. Under gate=off the legacy 30s
                // SHA pass carries the terminal state to all peers; no
                // per-agency wire to feed. Dual-mode silence.
                if (SettingsSystem.ServerSettings.PerAgencyCareerEnabled)
                {
                    try
                    {
                        if (OrbitalLogisticsReflection.TryBuildEntry(instance, out var entry))
                        {
                            // Sanity-gate the emit: only ship when Status is
                            // actually terminal. The wrap's finally block
                            // runs on ANY exit path including a partial-yield
                            // mid-coroutine (e.g. scene unload between
                            // MoveNext calls), where Status may still be
                            // Launched / Returning and the
                            // DoFinalLaunchTasksPostfix already echoed that
                            // state at launch time. Re-echoing it here would
                            // be idempotent at the server (router upsert by
                            // TransferGuid) but stranger-than-described —
                            // mirrors the same defensive Status-check
                            // DoFinalLaunchTasks / Abort postfixes use.
                            if (entry.Status != AgencyOrbitalTransferEntry.StatusLaunched
                                && entry.Status != AgencyOrbitalTransferEntry.StatusReturning)
                            {
                                AgencyOrbitalSender.SendTransferStateChange(entry);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OrbitalLogisticsReflection.LogRuntimeFailureOnce("DeliverPostfix-emit", ex);
                    }
                }
            }
        }
    }
}
