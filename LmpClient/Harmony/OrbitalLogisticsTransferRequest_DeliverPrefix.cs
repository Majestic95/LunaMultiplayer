using LmpClient.Systems.Agency;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Message.Data.Agency;
using System;
using System.Collections.Concurrent;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 3 Slice D-2] MKS-R2 — Prefix on
    /// <c>KolonyTools.OrbitalLogisticsTransferRequest.Deliver()</c>. Decides
    /// whether the local peer executes the delivery or delegates to the
    /// agency-owning peer (gate=on) / Update-lock-holder peer (gate=off).
    /// Closes the per-frame double-spend hazard documented in pre-spec §1.c.
    ///
    /// <para><b>Gate-state-independent.</b> Unlike the Slice B kolony / Slice
    /// C planetary postfixes (gated on <c>PerAgencyCareerEnabled</c>), this
    /// prefix runs under BOTH gates. Under gate=off the Update-lock-holder
    /// check is the gate=off authority — strict improvement on pre-Phase-3
    /// baseline where every peer in physics range mutated the destination's
    /// resources independently. Under gate=on the agency check is the
    /// primary authority; lock check is defensive. Decision math lives in
    /// <see cref="OrbitalDeliveryGate.ShouldExecuteDelivery"/>.</para>
    ///
    /// <para><b>Critical: Status mutation BEFORE returning false.</b> Pre-spec
    /// §2.d "general-review finding #1": a naive prefix that returns false
    /// without mutating Status leaves
    /// <c>ScenarioOrbitalLogistics.cs:194</c>'s inner
    /// <c>while (Status == Launched || Status == Returning) yield return null;</c>
    /// loop yielding forever — ProcessTransfers never advances, the every-2s
    /// <c>Update</c> keeps starting fresh ProcessTransfers coroutines on top
    /// of the hung one, and KSP runs out of memory. The skip path here sets
    /// <c>Status = Failed</c> + a brief <c>StatusMessage</c> so the inner
    /// while exits on the next yield; ProcessTransfers' first if-branch at
    /// <c>ScenarioOrbitalLogistics.cs:181-186</c> moves the transfer to
    /// ExpiredTransfers cleanly.</para>
    ///
    /// <para><b>Bounded re-skip cycle under gate=off</b> (pre-spec §2.d). The
    /// legacy 30s scenario SHA pass still ships under gate=off and brings
    /// the OWNING peer's still-Launched blob back to all peers on each
    /// scenario sync. <c>OnLoad</c> clears + rebuilds PendingTransfers
    /// (<c>ScenarioOrbitalLogistics.cs:52-65</c>), the transfer reappears,
    /// the prefix re-skips, the transfer moves to ExpiredTransfers again —
    /// once per ~30s per transfer per peer until the owning peer's
    /// Deliver-completion broadcasts the terminal Status via the next
    /// scenario sync. The <see cref="RateLimitedDebugLog"/> at the bottom
    /// suppresses repeat skip-logs for the same <c>(transferGuid, decision)</c>
    /// pair within a 60s window — bounded operator visibility without
    /// log-spam.</para>
    ///
    /// <para><b>Why the delegating peer's local Failed Status does NOT
    /// leak back to peers.</b> The skip path mutates Bob's LOCAL
    /// <c>OrbitalLogisticsTransferRequest.Status</c> to Failed; Bob's
    /// <c>ScenarioOrbitalLogistics.OnLoad</c> would normally re-classify
    /// this transfer to <c>ExpiredTransfers</c> and the next scenario sync
    /// could ship Bob's local stale state to peers. Under gate=on this is
    /// prevented by Slice D-1's
    /// <c>AgencyScenarioProjector.SpliceAgencyOrbitalTransfers</c>: the
    /// projector strips ALL shared TRANSFER children from each peer's
    /// outgoing <c>ScenarioOrbitalLogistics</c> blob and replaces with the
    /// peer's own per-agency state. Bob's local stale Failed entry is
    /// invisible to other peers because Bob's projected scenario carries
    /// only Bob's agency's transfers — which never includes this one
    /// (destination's owning agency is Alice's). Under gate=off the
    /// projector doesn't run, but the 30s SHA pass brings the OWNING
    /// peer's authoritative still-Launched state back to Bob on the next
    /// sync; the bounded re-skip cycle is what we accept (one re-skip per
    /// ~30s per transfer per delegating peer) until the owning peer's
    /// terminal-Status echo replaces Bob's stale state on next sync.</para>
    ///
    /// <para><b>Trust posture under gate=on.</b> A malicious client that
    /// bypasses this prefix locally + runs <c>Deliver()</c> would mutate
    /// <c>destination.ExchangeResources</c> on a vessel it doesn't own and
    /// broadcast via <c>VesselResourceMsgData</c>. The server-side
    /// <c>VesselMsgReader.RejectIfCrossAgencyWrite</c> (Stage 5.17a
    /// write-path counterpart, soak Finding-2) drops the relay before
    /// peers see it — cross-agency state stays consistent even when this
    /// client-side gate is bypassed.</para>
    /// </summary>
    public static class OrbitalLogisticsTransferRequest_DeliverPrefix
    {
        // Rate-limit gate per destination-vessel-id. Skip-log emits at most
        // once per (destination, 60s window) so the bounded-re-skip cycle
        // under gate=off doesn't fill KSP.log.
        private static readonly ConcurrentDictionary<string, DateTime> _lastSkipLog =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly TimeSpan SkipLogWindow = TimeSpan.FromSeconds(60);

        // Opportunistic prune trigger. Without this the dict grows by one
        // entry per unique destination vessel seen on the skip path forever
        // (vessels recovered / destroyed leave stale entries). At 50 bytes
        // per entry × an open-ended cohort, multi-day soaks would accumulate
        // sub-MB but unbounded. When the dict crosses this threshold the
        // next log call sweeps anything older than SkipLogWindow.
        private const int PruneThreshold = 128;

        /// <summary>
        /// Harmony prefix entry point. Returns <c>true</c> to let stock
        /// <c>Deliver</c> run (the OWNING peer); returns <c>false</c> after
        /// mutating <c>Status=Failed + StatusMessage</c> on the delegating
        /// peers.
        /// </summary>
        /// <param name="__instance">The MKS
        ///   <c>OrbitalLogisticsTransferRequest</c> — declared as
        ///   <see cref="object"/> here so this file compiles without a
        ///   KolonyTools reference; reflection reads / writes the fields via
        ///   <see cref="OrbitalLogisticsReflection"/>.</param>
        internal static bool Prefix(object __instance)
        {
            if (__instance == null) return true;

            try
            {
                if (!OrbitalLogisticsReflection.TryResolve())
                    return true; // MKS shape unrecognised — passthrough (stock Deliver runs)

                var status = OrbitalLogisticsReflection.ReadStatus(__instance);
                var isActive = status == AgencyOrbitalTransferEntry.StatusLaunched
                            || status == AgencyOrbitalTransferEntry.StatusReturning;

                // Pass non-active statuses straight through — stock Deliver's
                // own validation handles them; pre-empting would mask
                // diagnostic Status messages stock writes (e.g. "destination
                // no longer exists"). The pure-helper short-circuit on
                // isActiveStatus=false would also return true, but reading
                // Status here lets us skip the resolve overhead on the
                // overwhelmingly-common active-passthrough fast path.
                if (!isActive) return true;

                var destinationId = OrbitalDeliveryGate.ResolveDestinationVesselGuid(
                    () => OrbitalLogisticsReflection.GetDestinationVesselId(__instance));

                var execute = OrbitalDeliveryGate.ShouldExecuteDelivery(
                    destinationVesselId: destinationId,
                    isActiveStatus: true,
                    perAgencyEnabled: SettingsSystem.ServerSettings.PerAgencyCareerEnabled,
                    localPlayerName: SettingsSystem.CurrentSettings.PlayerName,
                    localAgencyId: AgencySystem.Singleton.LocalAgencyId,
                    getOwningAgency: vid =>
                        // bool+out → nullable adapter. Empty out-value when
                        // TryGet returns false; map that to null so the
                        // helper distinguishes "unknown" (5.18a mirror not
                        // yet populated) from "Unassigned sentinel" (spec
                        // §10 Q3).
                        AgencySystem.Singleton.TryGetOwningAgency(vid, out var aid)
                            ? aid
                            : (Guid?)null,
                    getUpdateLockOwner: vid =>
                        // GetUpdateLock returns a LockDefinition or null; if
                        // null, no lock holder. PlayerName is the string the
                        // helper compares against localPlayerName.
                        LockSystem.LockQuery.GetUpdateLock(vid)?.PlayerName);

                if (execute) return true;

                // Delegated peer skip path. CRITICAL: mutate Status before
                // returning false so ProcessTransfers doesn't hang. See
                // class XML.
                OrbitalLogisticsReflection.WriteStatus(
                    __instance,
                    AgencyOrbitalTransferEntry.StatusFailed);
                OrbitalLogisticsReflection.WriteStatusMessage(
                    __instance,
                    "[fix:MKS-R2] Delegated to owning-agency player");

                RateLimitedDebugLog(destinationId);
                return false;
            }
            catch (Exception ex)
            {
                // Best-effort defensive: any unexpected reflection / lookup
                // failure falls through to stock Deliver (passthrough,
                // double-spend may reappear for the failed peers but state
                // stays consistent at the singleton-elected-executor
                // level). One-shot log so the operator has a grep target.
                OrbitalLogisticsReflection.LogRuntimeFailureOnce("DeliverPrefix", ex);
                return true;
            }
        }

        private static void RateLimitedDebugLog(Guid destinationId)
        {
            // Key on destinationId only — the (transferGuid, decision)
            // composite from pre-spec §3.f would require TransferGuid
            // resolution in the hot path, which doubles the per-skip cost
            // for log-rate-limit math. destinationId is the most useful
            // grep target anyway ("why isn't my station receiving fuel?")
            // and degenerates to one log per delegating peer per
            // destination per 60s — bounded operator visibility.
            var key = destinationId.ToString("N");
            var now = DateTime.UtcNow;
            if (_lastSkipLog.TryGetValue(key, out var prev) && now - prev < SkipLogWindow)
                return;
            _lastSkipLog[key] = now;

            // Opportunistic prune — sweep stale entries when the dict grows
            // past PruneThreshold so a long soak doesn't accumulate forever.
            // Per-entry comparison cost is negligible compared to the skip
            // path's reflection writes; only fires on threshold crossings.
            if (_lastSkipLog.Count > PruneThreshold)
            {
                var cutoff = now - SkipLogWindow;
                foreach (var kvp in _lastSkipLog)
                {
                    if (kvp.Value < cutoff)
                        _lastSkipLog.TryRemove(kvp.Key, out _);
                }
            }

            LunaLog.Log(
                $"[LMP]: [fix:MKS-R2] Orbital Deliver skipped on local peer — destination vessel " +
                $"{destinationId:N} delegated to its owning-agency / Update-lock-holder peer.");
        }
    }
}
