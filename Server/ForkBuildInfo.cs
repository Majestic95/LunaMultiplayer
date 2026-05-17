namespace Server
{
    /// <summary>
    /// Constants describing this fork's deviations from upstream. Logged at server boot
    /// (see <see cref="MainServer.Main"/>) so operators can identify which fix set is
    /// active when reading logs. Per-event runtime log lines for each fix carry a
    /// matching <c>[fix:BUG-XXX]</c> prefix so operators can grep for an individual
    /// fix's behavior.
    ///
    /// Add a new entry to <see cref="ActiveFixes"/> at the same time as the
    /// corresponding fix's commit message references it, and keep the order
    /// in commit-chronological order (oldest first).
    /// </summary>
    public static class ForkBuildInfo
    {
        public const string ForkName = "Majestic95/LunaMultiplayer";

        public static readonly string[] ActiveFixes =
        {
            "BUG-051a",    // Server-side NewSubspace request dedup
            "BUG-001",     // Solo-subspace catch-up suppression
            "BUG-003/004", // Symmetric future-subspace interpolation cap
            "BUG-051b",    // Client steady-state retry while stuck-at-warp
            "BUG-005/006", // Cross-subspace lock keying + protocol bump
            "BUG-013",     // Reaction-wheel stateString locale-normalisation
            "BUG-008-A",   // Client-side PQS-aware spawn-altitude re-alignment (Phase A)
            "BUG-045",     // Breaking Ground deployable science vessels now sent to server (ported from upstream Release/0_29_2)
            "vessel-load-budget", // Per-tick proto-reload budget + VesselLoadOutcome enum + SPACECENTER/EDITOR fast path (ported from upstream Release/0_29_2)
            "vessel-sync-log",    // Client-side append-only diagnostic trace at Logs/LMP/VesselSyncLog.txt + Reason wire field on VesselProtoMsgData (ported from upstream Release/0_29_2)
            "BUG-010",     // Disconnect handshake Part A: server broadcasts VesselPinned for each lock-owned vessel before fanning out lock releases; remaining clients hold the vessel immortal until the original pilot reconnects or another player takes the helm
            "BUG-010-B",   // Disconnect handshake Part B: client flushes a fresh proto for every locally-owned vessel before NetworkConnection.Disconnect, so server's on-disk snapshot reflects the actual moment-of-disconnect pose (matters for dock-then-logoff -> undock-child-pose). Clean disconnects only; ungraceful drops rely on Part A alone.
            "BUG-033",     // ScenarioStoreSystem.BackupScenarios now serializes each scenario under the matching per-scenario writer lock (ScenarioDataUpdater.GetSemaphore), so ConfigNode.ToString() no longer races AddNode/RemoveNode/ReplaceNode on the same instance. Was a critical-but-rare server-crash class: backup task's collection-modified exception killed the worker.
        };
    }
}
