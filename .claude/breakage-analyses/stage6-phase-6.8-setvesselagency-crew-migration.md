# Stage 6 Phase 6.8 — /setvesselagency crew migration — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `b6148dbd` (Phase 6.7 — operator-required destination for WOLF cascade kerbal restoration)
**Discipline:** Per `[[feedback-breakage-analysis]]`.

**Motivation:** Phase 6.8 closes the last cross-cutting kerbal-data-flow gap under per-agency gate=on. Today `/setvesselagency A→B` reassigns the vessel stamp, migrates kolony/orbital/scan partitions, and releases stale locks — but the kerbals aboard the moved vessel stay in `Universe/Agencies/{A:N}/Kerbals/`. Under gate=on the read-side per-agency filter then never serves them to B's client (B's `KerbalsRequest` enumerates only `Agencies/{B:N}/Kerbals/`), so the moved vessel renders crewless on B's side and B cannot interact with the kerbals (EVA / board / hire / rename). The fix is per-kerbal file move between agency subdirs inside the existing dual-lock critical section, with same-name collision in destination refusing the whole command before any state mutation. Spec §Q-CrossAgency row 6 + §8 acceptance criterion 7.

Hooks into the existing 9-step orchestration in [SetVesselAgencyCommand.cs](../../Server/Command/Command/SetVesselAgencyCommand.cs) (gate refusal, dual-lock in Guid order, source-null/orphan branch, third-agency cross-ref scan, wire-emit order) — no restructuring; Phase 6.8 inserts the kerbal-file move as a new step between the router-migration helpers (step 4) and the SaveAgency pair (step 5), and the collision pre-check as a new pre-flight check before `RunUnderLockOrder` is entered.

---

## Scope lock — IS

### 1. `Server/Command/Command/SetVesselAgencyCommand.cs` — crew-extraction + collision pre-check + per-kerbal move

**1a. New private helper `ExtractCrewFromVessel(Guid vesselId, out List<string> crewNames)`** — scans `VesselStoreSystem.GetVesselInConfigNodeFormat(vesselId)` for `crew = NAME` lines and returns the deduplicated name list (string-empty / serialization-throws → empty list, treated as no-crew-aboard). Mirrors the scan shape at [KerbalSystem.cs:407](../../Server/System/KerbalSystem.cs#L407) (`CanRemoveKerbalUnderK1`) but inverted (collect names, not contains-check one). Pure-text scan, no AgencyState read, no lock needed.

**1b. Gate the whole migration on `AgencySystem.PerAgencyKerbalRosterEnabled`.** Under combined gate=off (`PerAgencyCareer=true` but `PerAgencyKerbalRoster=false`, OR `PerAgencyCareer=false` — already refused earlier), the command stays NO-OP for crew (current v7 behavior). The crew-extraction step doesn't run; the existing 9-step orchestration is unaffected.

**1c. Pre-flight collision pre-check** before `RunUnderLockOrder` is entered:
- Run `ExtractCrewFromVessel(movedVesselId, out var crewNames)`.
- For each name, check `FileHandler.FileExists(Path.Combine(GetKerbalsPathForAgency(destAgencyId), name + ".txt"))`.
- If ANY collision: log Error listing every colliding name + the resolution path (e.g. `/listagencies` to find destination's owner, ask them to rename their kerbal, OR re-run with a different destination), then `return false`. **No vessel mutation, no router migration, no SaveAgency, no wire emit, no lock acquisition** — clean refuse on the same path as the gate-refusal at [SetVesselAgencyCommand.cs:172-187](../../Server/Command/Command/SetVesselAgencyCommand.cs#L172-L187).
- This is an optimistic pre-check (no lock held). A racy second-check happens under dual-lock per (1f) below.

**1d. Source-path resolution per kerbal.** New private helper `ResolveKerbalSourcePathForMove(string kerbalName, Guid sourceAgencyId, bool sourceIsUnassigned)`:
- Source non-null + non-Empty → `Agencies/{sourceAgencyId:N}/Kerbals/{name}.txt`.
- Source Empty (Unassigned-sentinel vessel) OR orphan → probe `Agencies/{sourceAgencyId:N}/Kerbals/{name}.txt` first (defensive — orphan stamp could legitimately have a per-agency file from before the agency state file was lost), then fall back to legacy `Universe/Kerbals/{name}.txt` with Warning. Mirrors Phase 6.7's [AgencyWolfMigration.ResolveKerbalSourcePath](../../Server/System/Agency/AgencyWolfMigration.cs#L566-L571) upgrade-fallback pattern.
- If neither path exists for a kerbal-on-vessel → log Warning naming both paths + continue with the remaining kerbals (don't fail the whole command; the kerbal data is genuinely absent on disk, vessel-proto crew list is the only record).

**1e. Per-kerbal move INSIDE the existing `RunUnderLockOrder` critical section.** Slot between step 4 (router helpers, line ~380) and step 5 (`SaveAgency` pair, line ~392). For each kerbal name:
```
1. var srcPath = ResolveKerbalSourcePathForMove(name, sourceAgencyId, sourceIsUnassigned)
2. if (!FileHandler.FileExists(srcPath)) { Warning(...); continue; }
3. var destPath = Path.Combine(GetKerbalsPathForAgency(destAgencyId), name + ".txt")
4. var bytes = FileHandler.ReadFile(srcPath)
5. FileHandler.WriteAtomic(destPath, bytes, bytes.Length)
6. FileHandler.FileDelete(srcPath)
7. movedKerbalBytes[name] = bytes  // retain for wire push
```
**Order matters**: write-dest FIRST then delete-source SECOND. A mid-batch crash leaves the kerbal in BOTH dirs (recoverable — operator re-runs and the collision pre-check refuses; manual cleanup of the duplicate is straightforward). Reverse order would lose the kerbal entirely on a mid-batch crash. Same posture as the existing `SaveAgency(dest)`-first-`SaveAgency(source)`-second ordering at line 392.

**1f. Cascade-race re-check under the lock.** Before the per-kerbal loop runs, re-check `Agencies.ContainsKey(destAgencyId)` (and `Agencies.ContainsKey(sourceAgencyId)` when source is non-null). Miss → DROP the kerbal-move step with Warning + continue with the rest of the command (vessel stamp + router migration is already done; wire emit still fires; operator sees the inconsistency in the summary line and can re-run). Same pattern as Phase 6.5's [KerbalSystem.cs:147-151](../../Server/System/KerbalSystem.cs#L147-L151).

**1g. Optimistic-collision re-check under the lock.** The pre-check at (1c) is lock-free. Between pre-check and dual-lock acquisition, the destination owner's client could have written a same-name kerbal proto (`HandleKerbalProto` → `TryWriteKerbalProtoPerAgency` → write to dest subdir). Re-check `FileHandler.FileExists(destPath)` under the lock for each kerbal IMMEDIATELY before the `ReadFile + WriteAtomic`. Collision under lock → log Error listing the racing kerbal name + DROP that kerbal from the move + continue with rest. The destination kerbal stays put; the source kerbal stays put; the rest of the batch proceeds. The operator sees the per-kerbal Error and decides whether to clean up.

**1h. Wire push for moved kerbals.** AFTER `RunUnderLockOrder` returns, AFTER lock release + BackupSystem.RunBackup + Visibility broadcast + per-router echoes (slot between step 8(c) and the third-agency cross-ref scan at line 509):
- For each name in `movedKerbalBytes`, send `KerbalProtoMsgData{Kerbal = new KerbalInfo{KerbalName, KerbalData=bytes, NumBytes=bytes.Length}}` to the destination owner's client via `MessageQueuer.SendToClient<KerbalSrvMsg>(destOwnerClient, msgData)`. Offline destination owner → skip; fresh state arrives via the destination owner's next handshake `KerbalsRequest`.
- For each name in `movedKerbalBytes`, send `KerbalRemoveMsgData{KerbalName}` to the source owner's client via the same pattern. Offline source owner → skip; their next handshake `KerbalsRequest` re-fetches their (post-move) roster which no longer contains the moved kerbal.
- Construct messages via `ServerContext.ServerMessageFactory.CreateNewMessageData<...>()` (same factory pattern as the existing per-router echoes).

**1i. Extend operator-visible summary line at line ~536.** Add `kerbals-moved={n}` token + per-name `[fix:per-agency-kerbal-roster] setvesselagency {vid:N} moved-kerbal={name} src={srcPath} dest={destPath}` audit lines (grammar matches the existing per-key kolony/orbital lines at 553-568).

### 2. `Server/Command/Command/SetVesselAgencyCommand.cs` — XML doc updates

The class-level 9-step XML block + the "Phase 4 Slice F WOLF NO-OP" block both currently mention that crew migration is deferred. Update to reflect Phase 6.8 shipping:
- Class XML: extend the orchestration list with step 4b "Move per-vessel kerbal files between agency subdirs under gate=on, with pre-flight + under-lock collision check."
- Remove the Phase 6.5 / Phase 6.8 forward-looking TODO at [KerbalSystem.cs:121-128](../../Server/System/KerbalSystem.cs#L121-L128) — that block can now reference Phase 6.8's actual implementation instead of warning future-implementers.

### 3. `Server/ForkBuildInfo.cs` — append entry

Append `"stage6-6.8-setvesselagency-crew-migration"` to `ActiveFixes[]`. Keeps the boot banner entries 1:1 with shipped phases (same convention as Phase 6.2/6.3/6.4/6.5/6.6/6.7).

### 4. `ServerTest/SetVesselAgencyKerbalMigrationTest.cs` — NEW file

ServerTest cases pinning per-kerbal move + collision + cascade-race + Unassigned/orphan source + legacy fallback. Cases:

1. **`Migration_GateOn_MovesAllCrewFromSourceToDest`** — Alice has Jeb + Bill aboard her vessel V; `/setvesselagency V Bob`. Asserts Jeb + Bill files appear in `Agencies/{Bob:N}/Kerbals/`, absent from `Agencies/{Alice:N}/Kerbals/`, vessel stamp = Bob's agency.
2. **`Migration_GateOff_CrewUntouched_VesselStampOnly`** — Same setup with `PerAgencyKerbalRoster=false`. Asserts vessel stamp flips to Bob, kerbals stay in Alice's subdir (path is structurally the legacy path under gate=off + PerAgencyCareer=true is an unusual config; verify the gate-conditional skip works).
3. **`Migration_GateOn_DestHasSameNameKerbal_RefusesCleanly`** — Alice has Jeb (level 5, 100 flights); Bob has Jeb (level 1, 0 flights). `/setvesselagency V Bob` where V carries Alice's Jeb. Asserts: command returns false; vessel stamp UNCHANGED (still Alice); Alice's Jeb file UNCHANGED; Bob's Jeb file UNCHANGED; no router migration (kolony/orbital partitions untouched); no Visibility broadcast; no SaveAgency calls. Pure refuse — same shape as gate refusal.
4. **`Migration_GateOn_EmptyVessel_NoKerbalsMoved_RestOfCommandRuns`** — Alice's empty rover; `/setvesselagency V Bob`. Asserts vessel stamp flips, router migration runs, no kerbal files written/deleted (no `crew = ` lines in the vessel text).
5. **`Migration_GateOn_UnassignedSourceVessel_LegacyFallbackReadProbes`** — Vessel with `OwningAgencyId = Empty`; Jeb file in `Universe/Kerbals/Jeb.txt` (pre-upgrade legacy state); `/setvesselagency V Bob`. Asserts Jeb file appears in `Agencies/{Bob:N}/Kerbals/`, removed from `Universe/Kerbals/`. Warning logged naming both paths per spec §Q-Migration.
6. **`Migration_GateOn_OrphanSourceAgency_LegacyFallbackReadProbes`** — Vessel with non-Empty stamp but agency-id missing from `Agencies` dict (orphan); same legacy-fallback path. Different code path from (5) — orphan goes through `sourceIsOrphaned` branch, Unassigned goes through `sourceIsUnassigned`.
7. **`Migration_GateOn_KerbalNotOnDisk_LoggedWarning_OtherKerbalsStillMove`** — Alice's vessel carries Jeb (file present) + Bill (file mysteriously absent). Command logs Warning for Bill but still moves Jeb. Resilient to disk-state drift.
8. **`Migration_GateOn_CascadeRace_DestAgencyDeletedBetweenPreCheckAndLock_DropsMoveContinuesCommand`** — Simulate `Agencies.TryRemove(destAgencyId)` between collision pre-check and dual-lock acquire (in-test fixture manipulation). Asserts kerbal-move step DROPs cleanly (no `FileNotFoundException` propagation), vessel-stamp + router migration steps already ran, wire emit fires. Operator sees both the Warning and the summary line.
9. **`Migration_GateOn_OptimisticCollisionRaceUnderLock_DropsRacingKerbal_OtherMovesProceed`** — Pre-check sees no collision; under lock a same-name kerbal appears in dest (simulated by direct file write in the test fixture). Assert that ONE kerbal DROPs with Error, OTHER kerbals in the batch move successfully.
10. **`Migration_GateOn_SourceOwnerOffline_DestOwnerOffline_NoWirePush_FilesStillMove`** — Both owners disconnected; assert files migrate, no exception from MessageQueuer.SendToClient with null client.

### 5. `MockClientTest/SetVesselAgencyKerbalMigrationE2eTest.cs` — NEW file

Two-client e2e cases:

1. **`AliceVesselWithJebAboard_TransferredToBob_BobReceivesJebViaWirePush_AliceSeesJebRemoved`** — Connect Alice + Bob under combined gate=on. Alice broadcasts a `KerbalProtoMsgData` for "Aurora Test-Kerman" (unique-name pattern from Phase 6.5 e2e). Alice has a vessel carrying Aurora (vessel proto with `crew = Aurora Test-Kerman`). Operator runs `/setvesselagency V Bob`. Assert: Alice's mock-client inbox receives `KerbalRemoveMsgData` for Aurora; Bob's mock-client inbox receives `KerbalProtoMsgData` with matching bytes; the on-disk file moved to Bob's subdir.
2. **`AliceVesselWithJebAboard_CollisionInBobsAgency_RefusesCleanly_NoWireTraffic`** — Both Alice + Bob have a "Jebediah Kerman" file in their respective subdirs (seeded by Phase 6.3 stock-4 seeding). Alice's vessel carries Alice's Jeb (verified via vessel-proto crew list). Operator runs `/setvesselagency V Bob`. Assert: command returns false; neither client receives any KerbalProto / KerbalRemove / AgencyVisibility; both kerbal files unchanged on disk.

---

## Scope lock — IS NOT

- **No new wire MsgData types.** `KerbalProtoMsgData` + `KerbalRemoveMsgData` cover the dest-push + source-prune. No `AgencyKerbalMigrationMsgData` or similar — wire push reuses existing shapes.
- **No protocol bump.** Stays at 0.31.0.
- **No `AgencyState` field change.** Kerbal data lives in per-agency `Kerbals/` subdir on disk only.
- **No client-side change.** Same client-side `KerbalProtoMsgData` / `KerbalRemoveMsgData` ingest already exists; no new Harmony patches; no new client Systems.
- **No `/transferagency` change.** Owner-rename semantics preserved (kerbals live under AgencyId, not owner-name; rename is structurally NO-OP for kerbals — already documented in [SetVesselAgencyCommand.cs:118-148](../../Server/Command/Command/SetVesselAgencyCommand.cs#L118-L148)).
- **No `/deleteagency` change.** Phase 6.7 already closed the cascade-routing surface with `--restore-to <agency>` / `--restore-to-none`.
- **No auto-rollback on partial filesystem failure.** Each kerbal write is individually atomic (`FileHandler.WriteAtomic` rotates `.tmp` → rename); a mid-batch disk error leaves N moved + M not-moved with a loud Error log. Operator hand-recovers. Same posture as Phase 6.7 cascade-after-leak. Auto-rollback adds rollback-of-rollback complexity worse than the operator-recovery path.
- **No K1 grief-guard touch.** Stage 7 territory. K1 scan is structurally moot under gate=on already; setvesselagency doesn't trigger it (different code path).
- **No Final Frontier integration.** Phase 6.9, optional.
- **No vessel-proto `crew = NAME` mutation.** The on-vessel crew list IS the source of truth for who's aboard; we're moving the per-kerbal-file data to match. The vessel ConfigNode itself is unchanged by this command — the kerbal name on the vessel stays the same, only the file storage path changes.

---

## Files touched

| File | Change | Risk |
|---|---|---|
| `Server/Command/Command/SetVesselAgencyCommand.cs` | +~150 lines (pre-check + helpers + under-lock move + wire push + summary extension) | **HIGH.** Cross-subdir file moves, dual-lock-held disk I/O, optimistic collision race. |
| `Server/ForkBuildInfo.cs` | +1 entry | Trivial. |
| `Server/System/KerbalSystem.cs` | XML doc-comment cleanup (remove Phase 6.8 forward-looking TODO) | Trivial. |
| `ServerTest/SetVesselAgencyKerbalMigrationTest.cs` | NEW (~10 tests) | Low. Test code. |
| `MockClientTest/SetVesselAgencyKerbalMigrationE2eTest.cs` | NEW (~2 tests) | Low. Test code. |
| `docs/research/10-stage6-per-agency-kerbals-spec.md` | Row 6.8 ✅ marker + Phase 6.8 shipped subsection | Trivial. |
| `CLAUDE.md` | Stage Roadmap row 6.8 ✅ + Stack Notes entry for the per-kerbal move pattern | Trivial. |

---

## Direct breakage risk — Phase 6.8 itself

1. **Crew extraction false positives.** The needle `crew = ` could match nested context (a ConfigNode child whose key is literally `crew`). KSP's vessel format uses `crew = NAME` at the protoModuleCrew level (not nested). The existing K1 scan at [KerbalSystem.cs:407](../../Server/System/KerbalSystem.cs#L407) uses the same needle without depth-awareness and has been stable since Stage 5.17e-8. **Acceptable** — but if a false-positive appears in soak, switch to the depth-aware text walker from `AgencyWolfMigration.TryRewriteKerbalText` ([AgencyWolfMigration.cs:653](../../Server/System/Agency/AgencyWolfMigration.cs#L653)) which already tracks brace depth for top-level field detection.

2. **Collision pre-check is optimistic (lock-free).** Pre-check at (1c) reads `FileHandler.FileExists` without holding the destination lock; a same-name kerbal could appear in dest between pre-check and the under-lock move. Mitigation: the under-lock re-check at (1g) catches this. Operator-visible behavior: occasional `result=transferred kerbals-moved=2 kerbals-skipped=1` instead of `result=transferred kerbals-moved=3`. Acceptable — better than holding a long lock across the pre-check.

3. **Per-kerbal file move is NOT atomic across the batch.** Mid-batch disk failure leaves N moved + M not-moved. Per-file atomic (`WriteAtomic` rotates `.tmp`). Operator-recovery path via the Error log. **Documented in the summary line + spec §7.** No auto-rollback (decision rationale above).

4. **Wire push order vs apply order on dest client.** Dest client receives `KerbalProtoMsgData` AFTER the Visibility broadcast (Visibility goes first, per existing step 8 order at line 444-456). Dest client's `KerbalProtoMsgData` handler is the existing one — writes the file to the local KSP install's persistent.sfs companion path (the client doesn't differentiate by per-agency under gate=on; the wire payload comes from the server's per-agency subdir but the client's local representation is universe-wide-style). **Verify in soak** that dest client's `HighLogic.CurrentGame.CrewRoster` picks up the new kerbal correctly. Risk: low — the existing `KerbalProtoMsgData` ingest path is the same one used during initial KerbalsRequest reply, which has been stable since Stage 4.

5. **Source-owner KerbalRemove push race vs source-owner's pending KerbalProto.** Source owner might have an in-flight `KerbalProtoMsgData` for the moved kerbal that the server hasn't processed yet when we issue the wire push. Timeline: (a) source owner sends KerbalProto; (b) operator runs /setvesselagency; (c) source-owner's KerbalProto arrives at server AFTER the dual-lock releases; (d) `TryWriteKerbalProtoPerAgency` resolves sender→Alice, acquires Alice's lock, `Agencies.ContainsKey(Alice)` = true, writes to Alice's subdir. Result: the kerbal file reappears in Alice's subdir after we moved it out. **Mitigation:** the cross-agency-write guard in Phase 6.5 doesn't fire here because the sender IS Alice and the target IS Alice's subdir — by-construction tautology per [KerbalSystem.cs:130-159](../../Server/System/KerbalSystem.cs#L130-L159). This is a real race window. **Operator-mitigation:** the existing operator advice for `/setvesselagency` — `/kick` the source owner BEFORE the command — closes this race fully ([SetVesselAgencyCommand.cs:108-116](../../Server/Command/Command/SetVesselAgencyCommand.cs#L108-L116)). Document the kerbal-specific extension in the class XML's "Connected-source-owner handling" block. **Acceptable** — same race-window posture as the existing in-flight KolonyEntry / OrbitalTransfer issue at lines 351-368.

6. **Read-then-write-then-delete is NOT a true `File.Move`.** The spec's "atomic rename" wording suggested using `File.Move` for true cross-directory atomicity (NTFS supports this within the same volume). I chose `ReadFile + WriteAtomic + FileDelete` instead because: (a) `WriteAtomic` already exists and handles crash-tolerance via `.tmp` rotation; (b) symmetric with Phase 6.7's [AgencyWolfMigration.WriteAtomicViaFileHandler](../../Server/System/Agency/AgencyWolfMigration.cs#L611-L617) pattern; (c) preserves the bytes in memory for the wire push (a `File.Move` would force a re-read for the wire push, doubling disk I/O); (d) works across volumes if the operator runs the server with `Universe/` on a different volume from `Agencies/` (edge case but cheap to handle). **Trade-off:** marginally less atomic — a crash between WriteAtomic and FileDelete leaves the file in BOTH dirs. Cheap recovery (operator re-runs; collision check refuses; delete dup manually). Acceptable.

7. **Wire push factory access from command thread.** `ServerContext.ServerMessageFactory.CreateNewMessageData<KerbalProtoMsgData>()` is called from the command thread (operator typed the command). The factory is documented as thread-safe in the existing per-router echo path which runs from the same thread. **Safe.**

8. **`KerbalInfo.NumBytes` vs `bytes.Length`.** `WriteAtomic(path, byte[], int)` overload from Phase 6.5 takes `numBytes`. When we `ReadFile(srcPath)` we get the full file as a `byte[]`. Use `bytes.Length` as `numBytes` — no rented-buffer concern on the read path. **Safe.**

9. **Lock-release ordering wrt move step.** Move happens INSIDE `RunUnderLockOrder`. Step 7 (release stale vessel locks) runs OUTSIDE the dual-lock, AFTER the move. So the move completes, dual-lock releases, then stale-lock release. **No reordering hazard.**

---

## Indirect breakage risk — other code paths

1. **Phase 6.5 `TryWriteKerbalProtoPerAgency` race with our move.** Already addressed in (5) above. The under-lock re-check at (1g) catches the case where dest owner writes a same-name kerbal between pre-check and our move. Source-owner's in-flight KerbalProto after our move is operator-mitigated via `/kick`.

2. **Phase 6.7 `AgencyWolfMigration.CascadeOnDelete` overlap.** A scenario: source agency has both an in-flight WOLF CrewRoute AND a vessel carrying a kerbal; operator runs `/setvesselagency V destAgency` while concurrently another operator runs `/deleteagency sourceAgency --restore-to destAgency`. Both target the same destination agency's subdir. The dual-lock on `(source, dest)` serializes both operations. First to acquire wins; second blocks. Outcome depends on which lands first: if `/setvesselagency` wins, the kerbal files are at dest already, `/deleteagency` cascade reads from source's subdir but the kerbal aboard the moved vessel may already be gone (move ran on the in-vessel kerbal; the CrewRoute kerbal is a DIFFERENT kerbal — CrewRoute Missing-state kerbals aren't aboard any vessel by construction). No collision. **Safe by construction** — the two operations move disjoint kerbal sets (vessel-crew vs CrewRoute-passengers).

3. **KSP-side ProtoCrewMember validity post-move.** Dest client receives the kerbal via wire push; KSP's `KerbalRoster.AddCrewMember` (called inside `KerbalProtoMsgData` ingest) handles a fresh kerbal joining the local roster. The existing path is the same one used by initial KerbalsRequest reply at handshake. **Stable since Stage 4.**

4. **VesselSync content vs moved-kerbal expectation.** After move, when dest client requests vessel sync for the moved vessel, the server returns the vessel ConfigNode with `crew = NAME` text unchanged. Dest client's local `CrewRoster` now contains NAME (just pushed via KerbalProtoMsgData), so the scrub patch at [VesselLoader.ScrubInvalidProtoCrew](../../LmpClient/VesselUtilities/VesselLoader.cs) does NOT drop the entry. Vessel renders with full crew. **Intended.**

5. **Source client's vessel view post-move.** Source client receives the AgencyVisibility broadcast (vessel now belongs to dest) + the KerbalRemove push. Vessel renders foreign-agency-style via Phase 6.6's `IsForeignVessel` + `FormatForeignVesselCrewLabel`. Crew count from `ForeignCrewCount` registry — populated by `ScrubInvalidProtoCrew` on next vessel reload when the source client's KSP can't resolve the no-longer-local kerbal name. **Cosmetic drift** during the gap between KerbalRemove push and next vessel reload — source client may briefly see the vessel as own-agency-style with a now-removed kerbal in the protoCrew. Self-corrects on next vessel proto delivery. Acceptable.

6. **BackupSystem.RunBackup window.** Currently called at step 6 AFTER lock release. Phase 6.8's per-kerbal move adds work inside the dual-lock. Backup flush still runs after; the moved kerbal files are now WriteAtomic-rotated on disk so a crash between move-end and backup-end loses no kerbal data. The vessel.cfg stale-stamp window unchanged from current Slice E-2 posture. **No regression.**

7. **Third-agency cross-ref scan.** `InspectThirdAgencyCrossReferences` reads `AgencyState.OrbitalTransfers` for non-source-non-dest agencies. Does not touch kerbal files. **Unaffected.**

8. **Existing `SetVesselAgencyCommand` tests in ServerTest + MockClientTest.** Existing tests don't seed crew aboard the moved vessel (the test fixtures construct vessels with empty protoCrew). Phase 6.8's gate-on default for those tests is "empty vessel → kerbal-move no-op" — existing tests stay green without modification. **Verify** by running the existing test family with `PerAgencyKerbalRoster=true` toggle as part of the new test file's `[TestInitialize]`.

---

## Edge cases

1. Vessel with `crew = ` line but the named kerbal is genuinely not on disk anywhere → Warning logged, command continues with rest of crew. ✓ (test case 7)
2. Vessel with duplicate `crew = NAME` lines (KSP shouldn't emit, but defensively) → `ExtractCrewFromVessel` dedups. ✓
3. Vessel under `OwningAgencyId = Empty` (Unassigned sentinel) with crew aboard → legacy fallback probe. ✓ (test case 5)
4. Vessel under orphan agency-id (non-Empty but `Agencies` miss) with crew aboard → legacy fallback probe. ✓ (test case 6)
5. Destination has stock-4 + our move adds 4 more → no collision (different names typically); files coexist in dest subdir. ✓
6. Same-name vessel collision (both Alice and Bob hired a "John Kerman") → refuse. ✓ (test case 3)
7. Self-transfer (vessel already in destination) → step 1 same-stamp short-circuit fires; crew migration never runs. ✓ (existing behavior at line 271-281)
8. Empty vessel → crew-move loop runs zero times. ✓ (test case 4)
9. Source owner offline + dest owner offline → no wire push; both clients catch up on reconnect. ✓ (test case 10)
10. Source owner online but dest owner offline → KerbalRemove push goes; KerbalProto push to dest skipped. Both clients reach correct state via their respective paths. ✓
11. Operator runs `/setvesselagency` twice in a row on the same vessel A→B then B→A → second run refuses on collision (A's subdir now has empty space for that name, but our pre-check finds the file in B's subdir — wait, this is reverse direction. Re-derive: A→B moves files to B; B→A reverses. Pre-check looks for collision in A; A's subdir is now EMPTY of those names (we moved them out). No collision. Second run completes. **Reversible.** ✓
12. Cascade-race: dest agency deleted between pre-check and lock → DROP kerbal-move with Warning, rest of command runs. ✓ (test case 8)
13. Optimistic collision race: same-name file appears in dest under lock → DROP that kerbal, continue rest of batch. ✓ (test case 9)

---

## Test plan

| Surface | Tests | Coverage |
|---|---|---|
| `ExtractCrewFromVessel` | 3-4 inline ServerTest cases (multi-crew / empty / dup / serialization-throws) | Pure scan |
| `ResolveKerbalSourcePathForMove` | 3 inline ServerTest cases (per-agency / legacy fallback / both-absent) | Resolver branches |
| Collision pre-check refuse | 1 case in main test file | Refuse posture |
| Happy A→B migration | 1 case | Round-trip |
| Empty vessel | 1 case | No-op posture |
| Unassigned + orphan source | 2 cases | Legacy fallback |
| Cascade race | 1 case | DROP-and-continue |
| Optimistic collision race | 1 case | Per-kerbal DROP |
| Offline owners | 1 case | Wire-push skip |
| End-to-end | 2 MockClientTest cases | Wire round-trip + refuse-no-wire |

**Expected delta:** ServerTest 734 → ~744 (+10). MockClientTest +2.

---

## Review lenses to spawn (per `[[feedback-review-lens-framing]]` + `[[feedback-integration-logic-review]]`)

Three parallel agents:

1. **Consumer-lens** — read the new helper XML + summary log line + Error texts; would a GUI launcher's regex catch the new `kerbals-moved={n}` token cleanly? Would an operator reading the Error block on collision-refuses understand what to do? Does the doc comment on `ExtractCrewFromVessel` warn about the depth-unaware needle in case false positives appear?
2. **Upgrade-lens** — pre-Phase-6.5 universes with `AllowEnablePerAgencyKerbalsOnExistingUniverse=true` have kerbal files in `Universe/Kerbals/` legacy. `/setvesselagency` under gate=on on a vessel from such a universe needs the legacy-fallback probe to work cleanly. Does test case 5 + the source-resolver helper cover this end-to-end?
3. **Integration-logic** — trace 5-7 multi-actor scenarios: (a) operator + source-owner-online concurrent KerbalProto; (b) operator + dest-owner-online concurrent KerbalProto; (c) operator + concurrent `/deleteagency dest`; (d) operator + concurrent `/deleteagency source --restore-to other-agency`; (e) operator + concurrent second `/setvesselagency V different-dest`; (f) crash between WriteAtomic and FileDelete; (g) crash between move and BackupSystem.RunBackup.

---

## Rollback

Single-commit; revert restores the Phase 6.7 NO-OP-for-crew posture. Per-agency disk subdirs from Phase 6.3 remain in place; kerbal files moved by post-Phase-6.8 commands stay where the command put them (the revert doesn't un-move). Operators who ran `/setvesselagency` under gate=on after Phase 6.8 shipped + then reverted: their moved kerbal files are in destination's subdir (correct from the moved-vessel-belongs-to-dest perspective; the vessel stamp is also already in dest from the existing Phase-6.7-shipped command). Pre-revert mutations are not data-corrupting; post-revert behavior matches Phase 6.7 (vessel stamp moves, kerbal files don't). No persisted-data corruption from the revert itself.

---

## Decisions

- **Write-dest-first then delete-source.** Mid-batch crash leaves the kerbal in both dirs (recoverable). Reverse order would lose it on crash. Same posture as `SaveAgency(dest) → SaveAgency(source)` order at line 392.
- **Two-phase collision check (lock-free pre-check + under-lock re-check).** Avoids long lock-held disk-IO during the optimistic pre-check; the under-lock re-check catches the race window. Operator-visible behavior on race: per-kerbal DROP, not whole-batch refuse. Acceptable.
- **No auto-rollback on partial filesystem failure.** Each kerbal write is individually atomic; mid-batch disk error logs Error and continues. Operator hand-recovers. Matches Phase 6.7 cascade-after-leak posture.
- **Wire push for moved kerbals reuses existing `KerbalProtoMsgData` + `KerbalRemoveMsgData` shapes.** No new wire types. Server-side construction via the existing factory. Dest client and source client receive the same shape they'd see during a normal handshake or in-session KerbalProto event.
- **Legacy-fallback probe ONLY for Unassigned/orphan source.** Healthy-source flow always reads from `Agencies/{source:N}/Kerbals/`; legacy probe is the upgrade-hazard mitigation for pre-Phase-6.5 universes with the gate flipped via `AllowEnablePerAgencyKerbalsOnExistingUniverse=true`. Mirrors Phase 6.7's pattern.
- **Race with source-owner's in-flight KerbalProto is operator-mitigated, not structurally closed.** `/kick` the source owner before the command. Same posture as the existing in-flight KolonyEntry race documented at lines 351-368. Structurally closing this race would require holding source's agency lock across an arbitrarily-long operator window, trading a small race for a much larger denial-of-service surface.
- **Class XML "Phase 4 Slice F WOLF NO-OP" block stays.** Phase 6.8 doesn't touch the WOLF dicts (vessel-keyed migration on non-vessel-keyed entities is still NO-OP). The block remains accurate; just add a sibling note that vessel-crew kerbal files DO migrate.
