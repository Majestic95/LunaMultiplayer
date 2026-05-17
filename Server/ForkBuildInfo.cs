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
            "BUG-025",     // R&D tech node duplicate-purchase race. Server synchronously checks the canonical scenario before relaying a ShareProgressTechnologyMsgData; if the tech is already unlocked (the sender's open R&D panel was stale relative to another player's broadcast, or they clicked within sub-RTT), server sends ShareProgressTechnologyRejectedMsgData back to the sender so the client refunds the science it locally deducted. Additive wire enum value (TechnologyRejected = 11); no protocol bump.
            "BUG-023",     // Astronaut Complex desync / Tracking-Station info-pane freeze / scene-FLIGHT black scene / FixedUpdate NRE storm — all driven by null entries in ProtoPartSnapshot.protoModuleCrew that stock KSP appends when wire-side crew names don't resolve through CrewRoster. Three-part fix ported from upstream Release/0_29_2 (Drew Banyai, d3223931 + 138c2b3e): (1) VesselLoader.ScrubInvalidProtoCrew strips nulls in lockstep with protoCrewNames at vessel-load time, (2) VesselProtoSystem.CheckVesselsToLoad drains queued KerbalProto messages before each vessel-load batch to close the receiving-side timing race, (3) Part_RegisterCrew + KnowledgeBase_GetVesselCrewByAvailablePart Harmony patches as defense-in-depth for the autosave Save+Load round-trip that re-introduces nulls.
            "BUG-008-pack", // Phase A item 4a: pack-on-load + delayed unpack. PqsAlignmentRoutine now packs a freshly-loaded surface vessel (LANDED/SPLASHED/PRELAUNCH) that arrived in physics range (packed==false) on a PQS body, runs the PQS stabilise wait, snaps if NeedsRealignment, yields one FixedUpdate, and unpacks. Closes the residual polygon-scramble window where the immediate PQS sample happened to agree with stored altitude but the high-LOD mesh streamed in seconds later and exploded the collider. Active vessel never packed (would judder the camera); already-packed vessels stay on the existing snap-only path. Client-only change, no wire payload, no protocol bump.
            "per-agency-career", // Stage 5 scaffolding: PerAgencyCareer setting (default false) + protocol bump 0.30.0 -> 0.31.0. Setting=false preserves shared-agency behaviour bit-for-bit; the bump exists because per-agency wire payloads (later Stage 5 steps) will be a hard break against 0.30.x peers. No cross-compat row in LmpVersioning.cs — same break pattern as BUG-005/006 at 0.30.0.
        };
    }
}
