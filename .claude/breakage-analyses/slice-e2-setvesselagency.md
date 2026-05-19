# Slice E-2 (setvesselagency) — Breakage Analysis

**Branch:** `feature/per-agency-mks`
**Parent commit:** `45940aa0` (Slice E-1 — per-router migration helpers + wire tail-fields)
**Discipline:** Per [[feedback-breakage-analysis]] — mandatory before non-trivial changes.
**Author:** Session 31 (2026-05-18 continuation).

---

## Scope lock — IS

- New `Server/Command/Command/SetVesselAgencyCommandParser.cs` (pure parser, no AgencySystem touch).
- New `Server/Command/Command/SetVesselAgencyCommand.cs` implementing the 9-step contract from `AgencyKolonyRouter.MigrateForVesselTransfer` XML.
- One-line registration in `Server/Command/CommandHandler.cs:RegisterCommands` alongside `transferagency`/`deleteagency`.
- ServerTest cases (~10-12) pinning parse + gate refusal + token resolution + same-stamp short-circuit + happy A→B + dual-lock ordering + Unassigned-source + lock release + BackupSystem flush.
- MockClientTest cross-router scenarios (~3-4) pinning end-to-end wire on Alice→Bob with mixed kolony/orbital/planetary state.
- Smoke-backlog Item 5 update for operator workflow.
- CLAUDE.md update — `SetVesselAgencyCommand` added to Admin Commands inventory (18→19).

## Scope lock — IS NOT

- **No `/transferagency` change.** Owner-rename semantics preserved (operator-confirmed session 30).
- **No client-side mirror code.** `lmpOwningAgency` already flows to clients via VesselSync + 5.18c `AgencyVisibilityMsgData`. Client handles ownership transitions since 5.18b/c.
- **No new wire messages.** Slice E-1 (`45940aa0`) shipped the three `RemovedKolonyKeys`/`RemovedTransferGuids`/`RemovedPlanetaryKeys` tails as forward-compat fields under `Position<LengthBits` guard. E-2 just calls them.
- **No router code changes.** Migration helpers (`MigrateForVesselTransfer` on kolony/orbital, `InspectAffectedEntriesForVesselTransfer` on planetary) shipped in E-1 with internal-visibility + ServerTest pinning. E-2 is the CALLER.
- **No protocol bump.** Same forward-compat tail pattern as E-1.
- **No deferred items reactivated.** Slice E-1 deferred: symmetric send-side cap on Kolony+Planetary FORWARD tails (uniform refresh pass), `SendKolonyCatchupTo`+`SendPlanetaryCatchupTo` chunking, `cleanplanetaryentries` admin command. None are E-2 concerns.

## Files touched

| File | Change | Risk |
|---|---|---|
| `Server/Command/Command/SetVesselAgencyCommandParser.cs` | NEW (~90 lines) | Low. Pure string-grammar. |
| `Server/Command/Command/SetVesselAgencyCommand.cs` | NEW (~360 lines) | **HIGH.** Mutates `Vessel.OwningAgencyId`, calls 3 migration helpers, dual-lock, fires backup + lock release + 5 wire messages. |
| `Server/Command/CommandHandler.cs` | +1 line | Trivial. Same pattern as adjacent rows. |
| `ServerTest/SetVesselAgencyCommandTest.cs` | NEW (~10-12 tests) | Low. Test code. |
| `MockClientTest/CrossRouterVesselTransferTest.cs` | NEW (~3-4 tests) | Low. Test code. |
| `CLAUDE.md` | +1 inventory row | Trivial. |

## Direct breakage risk — SetVesselAgencyCommand itself

1. **Vessel.OwningAgencyId mutation lock anchor.** Initially raised as a MUST FIX concern. **Resolution (gap verified):** All existing `Vessel*DataUpdater.cs` files mutate `vessel.Fields` directly with no per-vessel writer lock; the established pattern relies on `MixedCollection`'s internal contract + the single-threaded receive context per message type. My command runs on the command-thread (one writer, by construction) while data updaters run on network threads (one writer per message-subtype). Concurrent writes to DIFFERENT keys are safe; concurrent writes to the SAME key (`lmpOwningAgency`) require two writers — one of which is the operator. The dual agency lock held during step 3 gates per-router MIGRATION DICTS, not vessel fields — correct anchor for what's being gated. 5.17a guard reads from network threads see either pre- or post- mutation; a torn read is impossible because the setter performs a single Fields.Update on a string-keyed slot. **No revision needed; documented in code with the "Concurrency note" block.**

2. **Dual-lock acquire ordering with Unassigned-source.** `RunUnderLockOrder` collapses to single-lock on destination when `source == null`. Orphaned source (non-Empty stamp but `Agencies.TryGetValue` miss) is treated as Unassigned (no migration, single-lock). **Documented choice, not a bug.**

3. **Step 1 same-stamp short-circuit.** Check `sourceAgencyId == destAgencyId` BEFORE locks. If both Empty would fire, but `TryResolveAgencyToken` rejects `Guid.Empty` — Empty can never be a destination. **Safe.**

4. **BackupSystem.RunBackup() failure.** Wrapped in try/catch. In-memory correct; disk vessels potentially stale. Matches DeleteAgencyCommand pattern. **Acceptable.**

5. **Lock release for Unassigned-source.** Uses `ClientRetriever.GetAuthenticatedClients()` (gap verified — exists at [ClientRetriever.cs:25](Server/Client/ClientRetriever.cs#L25)) to enumerate holders. Skips destination-agency holders. **Safe.**

6. **5.18b client mirror downgrade race.** `AgencyVisibilityMsgData` broadcast happens FIRST (step 8a), then source-removal echoes, then destination-add echoes. **Safe; ordering pinned by code.**

7. **Wire emit when offline.** If source/dest owner offline, `ClientRetriever.GetClientByName` returns null and echo is skipped. Catchup on reconnect ships post-migration state. **Safe; relies on existing Slice B/C/D catchup correctness.**

8. **Boot warning for orphaned destination.** `TryResolveAgencyToken` returns false for orphans → command fails fast. **Safe.**

## Indirect breakage risk — other code paths

1. **5.17a cross-agency rejection.** Post-stamp, source's player's lock acquires on the moved vessel hit cross-agency reject. **Intended.**
2. **5.17a soak Finding-2 write-path guard.** Post-stamp, source's player's relayed vessel messages are silently dropped. **Intended.**
3. **`Universe/Vessels/{guid}.txt` on-disk format.** `BackupSystem.RunBackup` → `VesselStoreSystem.BackupVessels()` → writes vessel.cfg with new `lmpOwningAgency`. **Gap verified at [BackupSystem.cs:65-76](Server/System/BackupSystem.cs#L65-L76).**
4. **VesselSync / GetVesselInConfigNodeFormat.** Post-mutation returns new stamp. **Confirmed safe by Stage 5.18b precedent.**
5. **BackupSystem lock contention.** `RunBackup` acquires `LockObj` + per-scenario semaphores (BUG-033). Called OUTSIDE the agency lock → no AB-BA risk on agency↔backup. **Safe.**
6. **Two log lines per invocation.** `BroadcastVisibilityChange` emits its own log line; command emits its own summary. **Intentional.**
7. **HandshakeSystem catchup ordering.** A returning player gets post-migration state via catchups; never sees mid-session removal/add echoes. **No breakage.**

## Edge cases

1. Vessel guid in "D" form vs "N" form — Guid.TryParse handles both. ✓
2. Agency token = vessel's owner name → resolves to that owner's agency → step 1 short-circuits if same as current. ✓
3. Vessel id collides with agency id (theoretically two random Guids). Parser routes vessel-token first, agency-token second; no cross-contamination. ✓
4. Source owner online / dest owner offline → source-removal echo sends, dest-add deferred to dest's next handshake catchup. ✓
5. Vessel has zero per-router entries → empty migration results; no per-router echoes sent; Visibility still fires. ✓
6. Idempotent re-issue → step 1 no-op. ✓
7. Self-transfer (vessel is both Origin and Destination of an orbital transfer) → kolony XML step 5 / E-1 test pinned. ✓
8. Orphaned source AgencyId → `source == null` branch, no migration, stamp + visibility + lock release. ✓
9. Destination = source's current agency → step 1. ✓
10. Vessel in CurrentVessels with `OwningAgencyId == Guid.Empty` (upgrade case) → source null, single-lock, no migration, Visibility broadcasts upgrade. ✓

## Schema impact

**None.** No new wire types, no new persisted fields, no protocol bump. Slice E-1 already shipped wire tail fields under forward-compat guard.

## Cross-stack / upstream impact

- **None upstream.** Fork-local `feature/per-agency-mks` branch work; never pushed to upstream per CLAUDE.md branch policy.
- **Server-only.** No LmpClient changes — client-side ownership mirror is 5.18b/c work; this command just exercises the existing `AgencyVisibilityMsgData` consumer.

## Test plan

**ServerTest unit cases (~10-12):**
- Parser: empty, 1-token, 3-token, valid, hyphenated guid form.
- Command gates: gate-off + non-Career refusal with distinct error.
- Token resolve failures: empty-registry, unknown agency token, unparseable vessel guid, vessel-not-in-store.
- Same-stamp short-circuit (idempotent, no side effects).
- Happy A→B: stamp mutated, kolony+orbital migrated, planetary inspected (read-only), lock released, visibility broadcast, dual SaveAgency, RunBackup invoked.
- Unassigned-source: no migration, single-lock, broader lock-release filter.
- Dual-lock acquire order (Guid.CompareTo).

**MockClientTest e2e (~3-4):**
- Alice→Bob with mixed kolony/orbital/planetary: Alice mirrors prune; Bob mirrors add; planetary retained in Alice with audit log.
- Cross-agency lock on moved vessel pre/post: Bob can't acquire pre-move; can post-move; Alice can pre-move; can't post-move.
- Visibility broadcast arrives at both clients with V→Bob entry.
- Unassigned vessel → Bob: 5.18b mirror upgrades Empty → Bob's id via `ForceRecordOwnership` bypass.

**Build cleanness:** `dotnet build -c Release` no NEW warnings beyond 29-baseline (Server.csproj) + 5-baseline (LmpCommon.csproj).

**No-op safety:** existing 414/28/84/143 counts stay green; new tests grow strictly upward.

## Pre-implementation gaps — RESOLVED (initial pass)

1. ⚠ **Vessel.OwningAgencyId mutation lock anchor.** Initial analysis claimed this matched the established VesselDataUpdater pattern (no lock). **REVISED post-review (see "Review-pass findings applied" below):** the actual VesselDataUpdater pattern DOES use a per-vessel `Semaphore` at [VesselDataUpdater.cs:88-94](Server/System/Vessel/VesselDataUpdater.cs#L88-L94) (S4 retro-review precedent). The initial gap-1 verification was based on sampling the wrong files (Position/Resource/etc. updaters which DON'T hold the lock); the OwningAgencyId-mutating path DOES. **Fixed**: added `VesselDataUpdater.GetVesselLock(Guid)` accessor; SetVesselAgencyCommand step 3 now wraps the vessel field write in `lock (VesselDataUpdater.GetVesselLock(movedVesselId))`. The wrong "Concurrency note" comment was rewritten to cite the actual S4 precedent.
2. ✅ **ClientRetriever.GetAuthenticatedClients() exists.** [Line 25](Server/Client/ClientRetriever.cs#L25). Used in command's `ReleaseStaleVesselLocks`.
3. ✅ **BackupSystem.RunBackup() flushes vessel.cfg.** [BackupSystem.cs:65-76](Server/System/BackupSystem.cs#L65-L76) → `VesselStoreSystem.BackupVessels()`. `LockObj` is reentrant; safe to call from outside.

## Review-pass findings applied

Four parallel lens reviews (general / consumer / upgrade / integration-logic) ran post-implementation. 5 MUST FIX + ~12 SHOULD FIX + ~13 CONSIDER findings; substantive applications below.

### MUST FIX (all 5 applied)

- **#1 (general): Per-vessel semaphore on `vessel.OwningAgencyId` mutation.** Exposed `VesselDataUpdater.GetVesselLock(Guid)`; SetVesselAgencyCommand wraps step 3. Closes the S4-retro-review torn-write hazard.
- **#2 (general): Reorder step 6 (RunBackup) and step 7 (release locks).** Locks now released BEFORE backup runs. Closes the "source-owner-vessel-frozen for the duration of one backup pass" UX hazard.
- **#3 (integration-logic): Flip SaveAgency pair order.** Save destination FIRST (additive), then source (subtractive). A mid-pair crash leaves the entry in BOTH agencies — recoverable. The opposite order would have left it in NEITHER agency — permanently lost.
- **#4 (consumer): `result=transferred` / `result=noop` tokens on summary log lines.** A GUI launcher's `result=(\w+)` regex now matches cleanly without substring-checking `no-op`.
- **#5 (integration-logic, scenario 7): Third-agency cross-reference inspection.** New `InspectThirdAgencyCrossReferences` helper scans every OTHER agency's `OrbitalTransfers` for entries where `OriginVesselId` or `DestinationVesselId` matches the moved vessel. Emits one Warning per stranded reference + reports the count via the new `third-agency-stranded=` summary token. Closes the "C's orbital transfer to Alice's vessel silently strands when Alice→Bob" hazard. Pre-spec §4.e didn't anticipate the graph topology.

### SHOULD FIX (most applied)

- **Tag drift (consumer):** Operator-summary log lines switched from `[fix:MKS-R2]` to `[fix:per-agency-career]` matching the neighbor `/transferagency`+`/deleteagency` family. Per-router migration audit lines (`kept origin-transfer=`, `planetary-retained-in-source key=`) stay on `[fix:MKS-R2]` since those are MKS-specific.
- **`vessel=` key on released-lock log line (consumer):** Now mirrors `/deleteagency`+`/transferagency` shape.
- **Orphan-source rationale doc (upgrade S1):** Expanded the rationale comment block to cover the orphan-lock-holder case alongside the Unassigned-bypass case.
- **Orphan Normal-level log line (upgrade S2):** Added a `LunaLog.Normal` `upgrade-from-orphan` line for operator grep.
- **Postfix-race state-loss (integration-logic scenario 4):** Documented inline at step 4 — the race window is bounded by the dual-lock critical section; structurally fixing it would require holding source's lock across an operator-driven window, trading a small race for a DoS surface. Operators are pointed at `/kick source-owner` for full closure.
- **Stale-rename lock release (integration-logic scenario 10):** `ReleaseStaleVesselLocks` rewritten to filter by holder-agency mapping uniformly (not by `source.OwningPlayerName`). Locks acquired under stale handles (pre-rename) are now caught.
- **Catchup replace-semantics doc (integration-logic scenario 3):** Added "Client apply: REPLACE, not merge" XML block to `SendKolonyCatchupTo`, `SendPlanetaryCatchupTo`, `SendOrbitalCatchupTo`. Future 5.18a/b client author obligation pinned.
- **Concurrent-operator post-lock re-check (integration-logic C1/scenario 9):** Step 2b re-checks `vessel.OwningAgencyId == destAgencyId` inside the dual lock; if so, short-circuits with Debug log. Avoids duplicate Visibility broadcasts when two operators race.

### Test additions

- **ServerTest +1**: `Execute_OrphanedSource_ProceedsWithoutMigrationAndStampsDestination` (upgrade-lens C2). ServerTest 433→434.
- **MockClientTest +1**: `SetVesselAgency_UnassignedSentinel_CrossAgencyLockHolder_ReleasedByStaleLockSweep` (upgrade-lens C1, scenario 10 verification under spec §10 Q3 bypass). MockClientTest 87→88.

### Deferred (CONSIDER items with acceptable rationale)

- **Wire-emit-order assertion in MockClientTest happy-path (consumer C):** Requires a MockNetClient inbox-peek API extension; `WaitForReply<T>` is scan-and-remove-first-match and doesn't pin position. The actual emit order IS correct in code; a regression that swapped emit order would still pass `WaitForReply<T>` calls. The harness-extension cost outweighs the value for a single test. Reopen if the emit-order invariant becomes load-bearing for a downstream consumer.
- **`/listagencies` refresh-trigger doc near summary log (consumer):** Added inline doc-comment near the summary log instead of separate documentation.
- **VesselScopedLockTypes hoist to LockSystem (general C7):** Defer to a uniform refresh pass — same HashSet appears in `DeleteAgencyCommand` and `TransferAgencyCommand` too.
- **Named-args vs param struct on `ReleaseStaleVesselLocks` (general C8):** Method now has only 2 params (movedVesselId + destAgencyId) after the unified-filter rewrite; concern obsolete.
- **"MUST stay in sync with" cross-reference between command XML and kolony helper XML (general C9):** Defer — short-term churn risk is low given the 9-step contract is stable post-E-2.
- **`(orphaned)` → `orphan:` formatting (upgrade N2):** Applied (`orphan:{sourceAgencyId:N}` prefix is now in code).
- **Test name shortening (upgrade N1):** Kept long descriptive name; the test pinpoint matters more than line length.
- **Self-transfer log demote to Debug (integration-logic C2 scenario 5):** Kept at Normal — operators running monitoring scripts is uncommon for admin commands; an operator typing `/setvesselagency` interactively wants to SEE the no-op confirmation.

## Hook gap note

The `.claude/hooks/require-bug-review.sh` enforces a commit-time receipt; there is no PreToolUse hook on `Edit|Write` for production .cs files that gates on a breakage-analysis being written FIRST. The user-memory `feedback_breakage_analysis.md` is the only enforcement, which is discipline-not-automation. **Deferred decision (operator session 31):** keep the discipline at the conversation level for now; revisit hook automation if the discipline lapses recurringly.

Lesson confirmed by this slice: the discipline correction caught one MUST FIX (the per-vessel-semaphore gap-1 mis-verification) that would otherwise have shipped as a latent torn-write bug.
