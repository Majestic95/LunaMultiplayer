# Retroactive bug-review findings — 2026-05-16

This document tracks the cumulative findings from the retroactive bug review of all
commits between `upstream/master` and `HEAD` as of 2026-05-16. Four parallel review
agents were run (network, server-systems, client-harmony, persistence) using the
rubrics in `.claude/review-agents/`.

The review was triggered by the new `.claude/hooks/require-bug-review.sh` gate
(commit `538ef21f`). Prior commits had not been gated; this audit closes the gap
on everything that had already shipped.

**[MUST FIX]** and **[SHOULD FIX]** findings are addressed inline in follow-up
commits per the user's policy "Fix everything inline". **[CONSIDER]** findings
land here for later triage.

---

## [MUST FIX] — addressed inline

| # | Domain | Finding | Resolution commit |
|---|--------|---------|---------|
| M1 | Network | `WarpNewSubspaceMsgData.cs:45` defensive read uses byte-aligned arithmetic on a bit-indexed reader (`LengthBytes - PositionInBytes`). Should be `LengthBits - Position >= 32`. | TBD |
| M2 | Server | `Server/Web/Structures/ForkInformation.cs` aliases `ForkBuildInfo.ActiveFixes` array by reference — mutation through the property leaks into the registry. | TBD |
| M3 | Persistence | `BackupSystem.RestoreFromArchive` overwrites canonical Universe without a pre-restore safety snapshot. Operator picking the wrong timestamp loses prior state irrecoverably. | TBD |
| M4 | Persistence | `BackupSystem.RestoreFromArchive` is not crash-safe mid-copy. Failure between subtrees leaves a half-restored Universe with no recovery path. | TBD |
| M5 | Server | `VesselMsgReader.HandleMessage` Position/Update/Flightstate/Resource/PartSync\*/ActionGroup/Fairing branches relay without an `IsStrictlyPast` check. With the restored client-side `SendUnloadedSecondaryVessel*` senders, a past-subspace client can corrupt a future-subspace vessel's state by changing subspace after acquiring an UnloadedUpdate lock. BUG-005/006 is partially regressed for unloaded vessels. | TBD |

## [SHOULD FIX] — addressed inline

| # | Domain | Finding | Resolution commit |
|---|--------|---------|---------|
| S1 | Network | `WarpNewSubspaceMsgData.cs` defensive-read comment is stale ("pre-fix clients send 0") — pre-fix clients cannot connect at all after the protocol bump to 0.30.0. Either delete the read or update the comment. | TBD |
| S2 | Network | `WarpRequestCache.EntryTtl` is a mutable `public static` field. Encapsulate (private setter or test-only entry point). | TBD |
| S3 | Network | `VesselMsgReader.HandleVesselCouple` stamps `AuthoritativeSubspaceId` whenever `client.Subspace > 0` — no `IsStrictlyPast` rewind guard. A past-subspace initiator can rewind authority via a couple message. | TBD |
| S4 | Server | `VesselDataUpdater.RawConfigNodeInsertOrUpdate` reads `CurrentVessels` for the auth-preserve branch OUTSIDE the per-vessel semaphore. Concurrent updates can race. | TBD |
| S5 | Server | `VesselDataUpdater.Semaphore` `ConcurrentDictionary<Guid, object>` grows without bound. Add cleanup hook off `VesselStoreSystem.RemoveVessel`. | TBD |
| S6 | Server | The fire-and-forget `Task.Run` body in `RawConfigNodeInsertOrUpdate` has no top-level try/catch. Exceptions surface only via `TaskScheduler.UnobservedTaskException` at GC. | TBD |
| S7 | Persistence | `BackupSystem` bypasses `FileHandler` for archive IO (raw `Directory.*` / `File.*`). Either route through `FileHandler` helpers or document the exception at top of file. | TBD |
| S8 | Persistence | `Directory.Delete(dest, recursive: true)` in restore + retention paths has no Universe-root assertion. Misconfigured `UniverseDirectory` could fire a recursive delete against an arbitrary CWD-relative path. | TBD |
| S9 | Persistence | `RunArchiveBackup` uses second-precision timestamps. Two backups in the same second can have the second's failure trigger a `Directory.Delete(archiveDir, recursive)` that destroys the first run's archive. | TBD |
| S10 | Persistence | `RunArchiveBackup` holds `LockObj` for the entire `CopyUniverseSnapshot` — periodic `RunBackup` flush is blocked for the duration. Snapshot the flush under the lock then drop it before the copy. | TBD |

## [CONSIDER] — deferred

| # | Domain | Finding |
|---|--------|---------|
| C1 | Network | Add explicit version re-check at top of `HandshakeSystem.HandleHandshakeRequest` (belt-and-braces; `MessageReceiver`-level check is the contract). |
| C2 | Network | `WarpRequestCache` has no upper bound on `Entries.Count`. A misbehaving client streaming unique `RequestSeq` values fills ~5 MB before TTL evicts. Add size cap. |
| C3 | Network | DEBUG branch of the `NetReliableSenderChannel.DestoreMessage` fix still throws — same NRE-as-NetException reappears if tests ever run DEBUG. |
| C4 | Network | Couple-handler relays before validating (`MessageQueuer.RelayMessage` runs before the auth-stamp guard). Inverted vs. proto path. Consider moving validation first. |
| C5 | Network | `WarpRequestCache` not purged on player disconnect. Username-reuse within 60s can hit a stale dedupe entry. Add `ClearForPlayer(playerName)` in disconnect path. |
| C6 | Server | `RefreshSoloStatuses` snapshots `Subspaces.Values` and `Clients.Values` separately; mid-flight subspace change can briefly miscount. Self-correcting within 5s. |
| C7 | Server | `WarpContext.LatestSubspace` throws on empty dictionary (pre-existing; documented elsewhere). |
| C8 | Server | `RestoreFromArchive` timestamp validation could use `Path.GetFullPath` + containment check rather than substring `..` test for stronger guarantees. |
| C9 | Server | `ForkInformation` / `LogSnapshot` leak internal state to an unauthenticated TCP port. Consider binding to `127.0.0.1` by default or adding basic auth (already in CLAUDE.md v2 deferred list). |
| C10 | Server | `JsonGetHandler` throws on factory exception — verify `ExceptionHandler` catches it or wrap in try/catch to return structured `{"error":"..."}`. |
| C11 | Server | `VesselSanitizer` log line emitted outside the per-vessel semaphore — can interleave on concurrent runs. Cosmetic. |
| C12 | Server | `Vessel.AuthoritativeSubspaceId` getter does `int.TryParse` every read. Cache as `Lazy<int>` / `int?` if profiling flags it. |
| C13 | Server | `LogRingBuffer.Capacity` is `const`; `LogSettings.RingBufferSize` deferred. Track for when the dashboard wants to tune. |
| C14 | Server | `LunaLog.LogFilename` is mutably reassigned across threads (pre-existing). |
| C15 | Client | Magic constant `MaxFutureInterpolationMultiplier=10` in `VesselPositionUpdate.cs` is comment-documented but worth pulling from settings later. |
| C16 | Client | Add "requires protocol ≥ 0.30.0" markers in `BUG-005/006` comments at the restored Send\*Secondary call sites. |
| C17 | Client | Tag `_currentRequestSeq` / `RequestNewSubspace` / `CheckSteadyStateRetry` as `// main-thread only` to deter future off-thread moves. |
| C18 | Persistence | `BackupCommand.list` doesn't show archive size / age. Operators want size + creation time at minimum. |
| C19 | Persistence | `BackupCommand` restore lacks `--dry-run` / `--force` UX. |
| C20 | Persistence | `IntervalSettingsDefinition` settings have no round-trip test in `ServerTest/`. |
| C21 | Persistence | `ArchiveBackupRetentionCount = 0` is currently "keep all forever"; XML comment is silent on the 0 semantics. Either clarify or change. |
| C22 | Persistence | `Subspace.txt` is in archive manifest but `Solo` flag is not persisted (intentional — runtime-derived). Add comment. |
| C23 | Persistence | `SnapshotDirs` is a hard-coded list; future per-agency / mod-plugin state silently won't be archived. Add a `LunaLog.Debug` enumeration of skipped top-level entries on first archive. |
| C24 | Persistence | `BackupSubspaces` / `FileHandler.WriteToFile` is non-atomic (no temp-file + rename). A crash mid-write leaves truncated `Subspace.txt`. Pre-existing in `FileHandler`; archive copies can propagate the corruption. |
| C25 | Persistence | Archive symlink/junction safety — out-of-tree paths could be reached via crafted archive contents. Operator-only mitigation today. |

---

## Process notes

- The `client-harmony-review` of the cumulative client diff found one `[MUST FIX — verification]` (the unloaded-secondary reject path) which proved true on inspection and was upgraded to M5 in the table above.
- BUG-008 Phase A (commits `3827ff1e` + `078cef31`) was reviewed and addressed earlier in this session — out of scope for the retroactive sweep.
- Test-harness changes (`899c0ddc`, `3f2565e5`, `10dfbfa4`) had a separate server-systems review during the session — also out of scope here.
- The `.claude/hooks/require-bug-review.sh` gate (`538ef21f`) is exempt by virtue of touching only `.claude/` and `.gitignore`; no production code.
