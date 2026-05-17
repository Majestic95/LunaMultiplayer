# Strategy B Implementation Plan — VesselSyncLog Vendor + BUG-045 Fix

**Purpose:** This document is the execution plan for porting the Breaking Ground deployable-science fix (BUG-045) and supporting `VesselSyncLog` diagnostic infrastructure from `origin/Release/0_29_2` (Drew Banyai's upstream stability branch) into our fork's `master`.

**Read this before doing anything else.** It is the authoritative ordering and acceptance criteria for the work. Read `CLAUDE.md` after this, then proceed to B.0.

**Status:** Approved direction. Not yet started.

---

## Context (90 seconds)

Two parallel upstream workstreams exist:
- `upstream/master` — AdmiralRadish-led, our `origin/master` mirrors it. Our local fork-master sits 26 commits ahead.
- `upstream/Release/0_29_2` — Drew Banyai-led stability/hardening branch. 66 commits not in upstream master. **Never merged back.** Mirrored exactly on `origin/Release/0_29_2`.

Our fork has been tracking `upstream/master` exclusively. The 0_29_2 branch was effectively invisible to us until the BUG-045 survey turned it up. Drew's branch contains the only known fix for BUG-045 (the top-reacted open mod-compat bug, 22 reactions) and several other Tier A candidates for our open inventory.

**Strategy B** = port Drew's BUG-045 fix and the supporting diagnostic infrastructure it depends on. Leave Drew's broader hardening pile for a separate triage session (Phase B.5).

For deeper survey of what's on Drew's branch and why we're cherry-picking selectively, see commit history with `git log origin/master..origin/Release/0_29_2`.

---

## Approved decisions (defaults — flag with user before changing)

| Decision | Choice | Reason |
|---|---|---|
| `reason:` parameter strategy | **Option β** — upgrade all 24 existing `SendVesselMessage` call sites | If we vendor the diagnostic, we should get its full value; partial coverage defeats the purpose |
| `VesselSyncDiagnostics` default | **ON** | Drew's design intent; truncate-on-startup bounds disk usage |
| Phase B.5 (loose siblings) | **Separate work session** | B.1–B.3 is already a meaningful unit; folding siblings doubles review surface and dilutes BUG-045 story |
| Branch strategy | **`fix/bug-045-and-vesselsynclog` branch, squash-merge to master** | Single reviewable history entry for community-visible fix |
| CLAUDE.md update timing | **End of B.3** | One comprehensive Stack Notes entry covering VesselSyncLog provenance + BUG-045 fix + 0_29_2 strategy gap |
| Protocol version | **Stay at 0.30.0** — do NOT cherry-pick `494a28a2` | That commit bumps to 0.29.2 RC and would silently downgrade our cross-subspace lock invariant (BUG-005/006) |

---

## Phase B.0 — Pre-flight checks (no code edits)

Time: ~30 min. **All must pass before B.1.**

- [ ] Confirm `VesselType.DeployedSciencePart` and `VesselType.DeployedScienceController` enum values exist in the KSP reference assemblies under `External/KSPLibraries/`. Without these the BUG-045 fix won't compile.
  - Grep `External/KSPLibraries/Assembly-CSharp.dll` symbols or check via reflection in a throwaway test.
- [ ] Read current state of [LmpClient/Systems/VesselEvaEditorSys/VesselEvaEditorEvents.cs](LmpClient/Systems/VesselEvaEditorSys/VesselEvaEditorEvents.cs) on our HEAD.
- [ ] Read [LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs](LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs) on our HEAD. Note line count (file-size cap: 600 soft / 900 hard).
- [ ] Read [LmpClient/VesselUtilities/VesselLoader.cs](LmpClient/VesselUtilities/VesselLoader.cs) on our HEAD. Note line count and confirm BUG-008 changes from commit `3827ff1e` are intact.
- [ ] Diff `origin/Release/0_29_2:LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs` against our HEAD copy. Identify conflict zones with our master fixes:
  - `0f10b2d3` BUG-001 solo-subspace catch-up
  - `cd551859` BUG-003/004 future-subspace interpolation cap
  - `25303e7d` BUG-051b client steady-state retry
- [ ] Diff `origin/Release/0_29_2:LmpClient/VesselUtilities/VesselLoader.cs` against our HEAD copy. Identify overlap with our `3827ff1e` BUG-008 PQS-aware spawn-altitude work.
- [ ] Verify mock harness regression-test feasibility for BUG-045. Today the harness does only handshake + WarpRequest dedup; it has no vessel-proto-send path. Decide: extend harness in B.4, or skip the automated test and rely on manual KSP verification.
- [ ] Create the working branch: `git checkout -b fix/bug-045-and-vesselsynclog`

**Report back to user at end of B.0** with: conflict zone summary (clean / minor / non-trivial), enum availability confirmation, harness feasibility verdict.

---

## Phase B.1 — BUG-045 fix, minimal adaptation

Time: ~90 min. **Risk: Low.** Ships the user-visible fix first, independent of all infrastructure risk.

### Goal
Land the deployable-science gate-widening on our master with the existing `SendVesselMessage(Vessel vessel, bool forceReload = false)` signature. No `reason:` parameter yet. Diagnostic value is partial but the bug is fixed.

### Source
Drew's commit `2526e15a` on `origin/Release/0_29_2`. **Do not literal-cherry-pick** — the original calls `SendVesselMessage(vessel, reason: "...")` which doesn't compile against our master signature.

### Changes

**File: [LmpClient/Systems/VesselEvaEditorSys/VesselEvaEditorEvents.cs](LmpClient/Systems/VesselEvaEditorSys/VesselEvaEditorEvents.cs)**

Rewrite `VesselCreated(Vessel vessel)` to widen the gate. Conceptual shape (do not copy verbatim; adapt to current code style):

```
- Null/spectating guard at top
- Check 1: isEvaConstructionDrop = System.DetachingPart
- Check 2: isDeployableScience = vessel.vesselType == DeployedSciencePart || DeployedScienceController
- Early-return if neither
- Acquire UpdateLock + UnloadedUpdateLock (existing behaviour)
- LunaLog with [fix:BUG-045] tag indicating which branch fired
- SendVesselMessage(vessel) — existing signature, no reason parameter
```

### Required ancillary updates

- [ ] [Server/ForkBuildInfo.cs](Server/ForkBuildInfo.cs) — append `"BUG-045"` to `ActiveFixes[]` in commit-chronological order
- [ ] [docs/research/01-bug-inventory.md](docs/research/01-bug-inventory.md) — change BUG-045 status from "Open" to "Fixed (Phase A, ported from upstream Release/0_29_2 commit 2526e15a)"
- [ ] Create [docs/research/02-analysis/bug-045-deployable-science.md](docs/research/02-analysis/bug-045-deployable-science.md) — Phase-2 doc following existing format: Symptom / Root cause / Fix / Risk / Verification / Provenance

### Acceptance criteria

- [ ] `"/c/Users/austi/.dotnet/dotnet.exe" build LmpClient/LmpClient.csproj -c Release` — no NEW warnings beyond the 30 pre-existing
- [ ] `"/c/Users/austi/.dotnet/dotnet.exe" build Server/Server.csproj -c Release` — clean
- [ ] `"/c/Users/austi/.dotnet/dotnet.exe" test ServerTest/ServerTest.csproj -c Release` — pass
- [ ] `"/c/Users/austi/.dotnet/dotnet.exe" test LmpCommonTest/LmpCommonTest.csproj -c Release` — pass
- [ ] `"/c/Users/austi/.dotnet/dotnet.exe" test MockClientTest/MockClientTest.csproj -c Release` — pass
- [ ] Single commit. Suggested message:
  ```
  fix(vessel): BUG-045 — send Breaking Ground deployable science vessels to server

  Ported from upstream Release/0_29_2 commit 2526e15a (Drew Banyai).
  Adapted to drop the SendVesselMessage(reason:) parameter which lives
  on the supporting VesselSyncLog infrastructure (deferred to a follow-up).

  VesselEvaEditorEvents.VesselCreated previously gated proto-send on
  System.DetachingPart, which is only set by EVA Construction Mode
  part-drops. Breaking Ground deployables go through inventory placement
  and fire GameEvents.onNewVesselCreated without raising any
  EVAConstructionEvent, so their protos were never transmitted to the
  server. Widening the gate to also match vesselType ==
  DeployedSciencePart / DeployedScienceController fixes the disappearance.

  Closes BUG-045.
  ```

### **PAUSE for user review at end of B.1.**

Surface the diff and the test results. User may want to ship this independently of B.2/B.3 (e.g., merge to master and re-evaluate before infrastructure work).

---

## Phase B.2 — Port per-tick proto budget + VesselLoadOutcome enum

Time: 2–3 hrs. **Risk: HIGH.** Conflict-heavy merge against our existing master fixes.

### Goal
Land Drew's `346ef48a` adapted for our codebase. This is the highest-risk merge in Strategy B because it touches two files we've recently modified.

### Source
`origin/Release/0_29_2:346ef48a` "LmpClient: cap per-frame proto reloads, add SPACECENTER/EDITOR fast path, distinguish load outcomes"

### Changes

**File: [LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs](LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs)**
- +164 lines on Drew's side
- Conflict zones from our master: BUG-001 (`0f10b2d3`), BUG-003/004 (`cd551859`), BUG-051b (`25303e7d`)
- Per-tick `MaxExpensiveReloadsPerTick = 2` budget on destructive `ProtoVessel.Load` calls
- Queue-peek-but-not-dequeue retry pattern in `CheckVesselsToLoad`
- Distinguishes `VesselLoadOutcome` { FreshlyLoaded, Reloaded, UnchangedEarlyOut, Failed } from the previous single-bool return

**File: [LmpClient/VesselUtilities/VesselLoader.cs](LmpClient/VesselUtilities/VesselLoader.cs)**
- +117 lines on Drew's side
- Conflict zone: our local `3827ff1e` BUG-008 PQS-aware spawn-altitude realignment
- New `UpdateProtoInPlace(...)` cheap proto-swap for SPACECENTER + EDITOR scenes
- `LoadVessel` return type changes from `bool` to `VesselLoadOutcome` enum

### Conflict-resolution approach

Manual three-way merge, **not** raw `git cherry-pick`:
1. Read both versions of each file end-to-end before editing
2. For each conflict block, decide whether Drew's logic or our fix logic wins, OR whether they compose (most common case will be "compose")
3. Make sure BUG-008's PQS-realignment path fires for `FreshlyLoaded` and `Reloaded` outcomes but NOT for `UnchangedEarlyOut`
4. Make sure BUG-001/003/004 timing guards still wrap the per-tick budget loop, not the other way around

### Required ancillary updates

- [ ] [Server/ForkBuildInfo.cs](Server/ForkBuildInfo.cs) — append `"vessel-load-budget"` (not a BUG-id; infra)
- [ ] No bug-inventory change (no specific BUG closed)

### Acceptance criteria

- [ ] All four build/test commands from B.1 pass
- [ ] Manual review of merged `VesselProtoSystem.cs` and `VesselLoader.cs` confirms each of BUG-001/003/004/051b/008 invariants is still enforced
- [ ] If either file ends up over 600 lines, add a top-of-file comment justifying the exception (file-size cap policy from CLAUDE.md)
- [ ] Single commit. Suggested message:
  ```
  feat(client): per-tick proto-reload budget + VesselLoadOutcome enum

  Ported from upstream Release/0_29_2 commit 346ef48a (Drew Banyai).
  Manually merged against our existing BUG-001/003/004/051b/008 fixes
  in VesselProtoSystem.cs and VesselLoader.cs.

  Caps destructive ProtoVessel.Load calls to MaxExpensiveReloadsPerTick
  per Update tick to prevent frame hiccups when N peer broadcasts arrive
  simultaneously. Adds SPACECENTER/EDITOR scene fast path via cheap
  in-place proto-swap (no part instantiation cost). Distinguishes load
  outcomes (FreshlyLoaded / Reloaded / UnchangedEarlyOut / Failed) so
  callers can log meaningfully and only fire LMP events on actual change.

  Enabling infrastructure for the VesselSyncLog diagnostic in the
  follow-up commit.
  ```

---

## Phase B.3 — VesselSyncLog diagnostic + reason: parameter upgrade

Time: 2–3 hrs. **Risk: Medium.** Large new file, 24-site call ripple.

### Goal
Vendor Drew's vessel-sync trace log and retrofit the `reason:` parameter through all `SendVesselMessage` call sites.

### Source
- `origin/Release/0_29_2:4733081d` — initial VesselSyncLog
- `origin/Release/0_29_2:60a2ed5d` — truncate-on-startup follow-up (fold into same commit)

### New file

**[LmpClient/Diagnostics/VesselSyncDiagnostics.cs](LmpClient/Diagnostics/VesselSyncDiagnostics.cs)** (412 lines on Drew's side, fold in `60a2ed5d` truncate-on-startup change before committing)

Verbatim port. Verify before committing:
- No `record`, `init`, `Span<T>`, `Index/Range`, top-level statements, or `System.Text.Json` (would fail on net472)
- No raw `System.IO.File.*` for vessel state — diagnostic file I/O is for the log file itself and lives under `Logs/LMP/`, not the Universe; this is OK
- No `Console.WriteLine` — use `LunaLog.*`
- Catch blocks log via `LunaLog.Warning` / `Error` and self-disable, not silent-swallow
- Self-disable on init failure is preserved (commit message explicitly: "instrumentation must never break what it observes")
- Cross-platform path handling: confirm `Logs/LMP/` directory creation works on Linux dedicated servers, not just Windows

### Modified files

- [LmpClient/LmpClient.csproj](LmpClient/LmpClient.csproj) — include the new file
- [LmpClient/MainSystem.cs](LmpClient/MainSystem.cs) — init, scene cache via `NotifyScene`, drain on `OnExit`. Folds in `2eb4ac14` heartbeat consolidation? **NO** — heartbeat consolidation is a B.5 sibling; keep this commit narrow.
- [LmpClient/Systems/SettingsSys/SettingsStructures.cs](LmpClient/Systems/SettingsSys/SettingsStructures.cs) — add `VesselSyncDiagnosticsEnabled` bool, default `true`
- [LmpClient/Systems/VesselProtoSys/VesselProto.cs](LmpClient/Systems/VesselProtoSys/VesselProto.cs) — ARRIVED + DISCARDED log points
- [LmpClient/Systems/VesselProtoSys/VesselProtoMessageHandler.cs](LmpClient/Systems/VesselProtoSys/VesselProtoMessageHandler.cs) — wire log points at message receive
- [LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs](LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs) — LOADED / RELOADED / SWAPPED / UNCHANGED + REMOVED log points hooked to VesselLoadOutcome from B.2
- [LmpClient/Systems/VesselProtoSys/VesselProtoMessageSender.cs](LmpClient/Systems/VesselProtoSys/VesselProtoMessageSender.cs) — add `string reason = null` parameter to `SendVesselMessage`

### Option β — upgrade all 24 call sites with reason strings

The 24 sites are in 7 files:

| File | Sites |
|---|---|
| `LmpClient/Systems/AsteroidComet/AsteroidCometEvents.cs` | 3 |
| `LmpClient/Systems/FlagPlant/FlagPlantEvents.cs` | 1 |
| `LmpClient/Systems/VesselCrewSys/VesselCrewEvents.cs` | 3 |
| `LmpClient/Systems/VesselEvaEditorSys/VesselEvaEditorEvents.cs` | 3 (one of which is our B.1 BUG-045 fix) |
| `LmpClient/Systems/VesselProtoSys/VesselProtoEvents.cs` | ~8 |
| Plus a few others in less-trodden systems | ~6 |

For each site, supply a short string describing why this send is firing. Patterns to use:
- `"new vessel: asteroid spotted"` (asteroid spawn)
- `"new vessel: comet spotted"` (comet spawn)
- `"flag plant"`
- `"crew transfer"`
- `"EVA construction: new vessel from detached part"`
- `"Breaking Ground: deployed science part placed"` (the BUG-045 site from B.1 — retrofit it now)
- `"active vessel changed"`
- `"part attached"`, `"part detached"`, `"part destroyed"`
- etc.

Reasons should be short and human-readable. The diagnostic value is "I can grep VesselSyncLog and tell why this particular vessel got sent."

### Required ancillary updates

- [ ] [Server/ForkBuildInfo.cs](Server/ForkBuildInfo.cs) — append `"vessel-sync-log"`
- [ ] [CLAUDE.md](CLAUDE.md) — Stack Notes entry at end of "Stack Notes & Patterns Learned" section, dated today. **One entry covering all three:**
  1. VesselSyncLog lives at `Logs/LMP/VesselSyncLog.txt`, truncates on startup, captures ARRIVED/DISCARDED/LOADED/REMOVED. Provenance: ported from upstream `Release/0_29_2:4733081d + 60a2ed5d` (Drew Banyai).
  2. `SendVesselMessage` now takes `string reason = null`; new call sites should pass a short human-readable reason.
  3. Strategy note: `origin/Release/0_29_2` is a third active workstream (Drew Banyai) parallel to upstream master. Fork-master strategy was widened in Strategy B to selectively port from it. See `docs/strategy-b-implementation-plan.md` for the criteria.

### Acceptance criteria

- [ ] All four build/test commands from B.1 pass
- [ ] `VesselSyncDiagnostics.cs` line count under 600 (it's 412 on Drew's side, should stay similar)
- [ ] Manually verify on disk: starting a Server then connecting via the mock harness produces a `Logs/LMP/VesselSyncLog.txt` file with sensible content (even if just handshake-only)
- [ ] Single commit. Suggested message:
  ```
  feat(client): VesselSyncLog diagnostic trace + reason: parameter

  Ported from upstream Release/0_29_2 commits 4733081d + 60a2ed5d
  (Drew Banyai). Adds an append-only per-session trace at
  Logs/LMP/VesselSyncLog.txt capturing vessel arrival, rejection,
  load outcome, and removal — designed to make sync bugs diagnosable
  without re-reading the full KSP.log.

  Adds `string reason = null` parameter to SendVesselMessage and
  upgrades all 24 existing call sites with short human-readable
  reasons. Includes the B.1 BUG-045 site (Breaking Ground
  deployable placed) so the fix is now observable in the trace.

  Self-disables on init failure; instrumentation must never break
  what it observes. Truncates log on startup to bound disk usage.
  Gated by SettingsStructures.VesselSyncDiagnosticsEnabled (default
  true).
  ```

### **PAUSE for user review at end of B.3.**

Surface the merged diff (probably substantial), test results, and a sample of the generated `VesselSyncLog.txt` from a local harness run.

---

## Phase B.4 — Mock harness extension + BUG-045 regression test (optional)

Time: 3–4 hrs. **Risk: Medium.** Scope-honest version of what was hand-waved as "1 test file."

### Why optional
The mock harness today (Stage 4.9 / 4.10 first wave) only does handshake + WarpRequest dedup. Asserting BUG-045 requires teaching the mock client to send a vessel-proto message with `vesselType=DeployedSciencePart`. That's a meaningful harness feature.

### Skip-criteria
If a manual KSP verification has been done (place a deployable, restart server, reconnect, deployable persists) and recorded in the BUG-045 Phase-2 doc, B.4 can be deferred to a future Stage 4.10 batch.

### If executed

- Extend `MockClientTest/Support/MockNetClient.cs` (or similar) with `SendVesselProto(...)` capable of arbitrary `vesselType`
- New test `MockClientTest/Bug045DeployableScienceTest.cs`:
  1. Connect, handshake completes
  2. Send a synthesised vessel-proto message with `vesselType=DeployedSciencePart`
  3. Assert server's `VesselStoreSystem.CurrentVessels` contains the new vessel guid
  4. Disconnect + reconnect (or just query) and assert it's still there
- Update [CLAUDE.md](CLAUDE.md) test suite section with the new test
- Commit message: `test(harness): Stage 4.10 — BUG-045 deployable-science proto-send regression`

---

## Phase B.5 — Loose sibling triage (separate session, NOT part of this plan)

Out of scope for this document. Catalogued for the next planning session:

| Commit | Description | Likely value |
|---|---|---|
| `3d7f027a` | Quarantine local topology mutations from incoming proto race | High (BUG-010 adjacent) |
| `5354de09` | Fix `VesselPartSyncFieldMsgData` deserialization for Quaternion and Object | High (looks like real wire bug) |
| `18923984` | Suppress redundant Part-count-drift rebroadcasts | Medium |
| `2eb4ac14` | Consolidate heartbeat into single per-second tick | Low (QoL) |
| `e4cb3c2d` | `DiscoveryInfoSanitizer` in `VesselSerializer` | Medium (compare with master's `2e47ce90`) |
| `0e11c918` | ScenarioAchievements crew dedupe | High (validate against BUG-023 first) |
| `ef760ecf` + `61df88ce` | Vessel destruction race fixes | High (validate against BUG-010 first) |
| `876b158c` | Withdrawn/DeadlineExpired contract sync + IgnoreEvents guard | Validate against BUG-025 first |

---

## Fork conventions and guardrails (do not skip)

These are easy to forget when porting from upstream. CLAUDE.md is authoritative; this is a high-priority subset:

1. **Build path:** Always `"/c/Users/austi/.dotnet/dotnet.exe"`. The system `dotnet` is 7.x and cannot satisfy `global.json`'s 10.0.100 pin.
2. **Per-phase commits required.** Do NOT amend. Do NOT batch B.1 + B.2 + B.3 into a single commit. Each phase commits independently and master stays green between phases.
3. **No `git add -A`.** Use explicit file paths. `.claude/` and `CLAUDE.md` must never reach upstream PRs (fine for `origin` master work).
4. **No AI attribution** anywhere. Strip from commit trailers, PR bodies, code comments, committed docs. Silent partner rule. Upstream has reverted AI-attributed contributions before (Fierce-Cat / issue #588).
5. **`[fix:BUG-045]` log-tag convention.** Any new `LunaLog.*` line added for BUG-045 must carry the prefix so operators can `grep -F "[fix:"` to find fork-attributed events.
6. **`ForkBuildInfo.ActiveFixes`** registry must be updated for every fix in commit-chronological order. Do not reorder existing entries.
7. **Push to `origin` only.** Never push to `upstream`.
8. **net472 target compatibility for LmpClient.** When porting from Drew, scan for: `record`, `init` setters, `Span<T>`, `Index/Range`, top-level statements, `System.Text.Json`, pattern-matching beyond C# 7.x. Any of these must be backported to net472-compatible equivalents.
9. **`FileHandler` is the disk gateway for Universe state.** The diagnostic log is NOT Universe state — it lives in `Logs/LMP/` and can use direct `System.IO.File` calls. Do not regress this distinction.
10. **`LunaLog.Normal` is the project's info level**, not `LunaLog.Info`. Match existing usage in `BackupCommand` and friends.
11. **File-size caps:** 600 soft / 900 hard for `.cs`. If a port pushes a file past 600, either split-out or add a top-of-file justifying comment.
12. **Mandatory breakage analysis before non-trivial changes.** Scope / edge cases / test plan. See "Edge cases the agent must consider" below.

---

## Edge cases the agent must consider

Per CLAUDE.md's mandatory breakage analysis. Reason through these before committing each phase.

### BUG-045 gate-widening (B.1)
- **Both flags true:** Deployable placed during EVA Construction Mode. Should fire once, not twice. The new logic should use `||`, not two separate `if` blocks.
- **Future BG VesselType values:** KSP might add more deployable subtypes in DLC updates. Current enum has `DeployedSciencePart` + `DeployedScienceController`. If a third appears, the gate misses it. Acceptable risk for now; flag in the Phase-2 doc.
- **0-part deployable mid-spawn:** Race between `onNewVesselCreated` firing and KSP finishing part instantiation. Drew's code does not guard against this; existing `SendVesselMessage` path tolerates it (asteroid-spawn flow is similar). Leave as-is.
- **Placing kerbal is in a stale subspace:** If the EVA kerbal is in a subspace behind the server (BUG-001/005 territory), the deployable's `lmpAuthSubspace` stamp will be wrong. Out of scope for BUG-045; the existing subspace-stamping logic handles it.
- **Existing orphan locks server-side:** Players who ran pre-fix sessions have orphan `UnloadedUpdate` locks on the server from deployables that were never persisted. No cleanup needed — they expire on subspace teardown. Document this in the Phase-2 doc.

### VesselSyncLog diagnostic (B.3)
- **Disk-write at seconds-scale with 50+ vessels:** Burst of arrivals on session start could exceed reasonable I/O. Drew's design batches via a single lock; verify the lock-hold time stays bounded.
- **Linux server path handling:** `Logs/LMP/` directory creation. Drew is on Windows; verify `Path.Combine` and `Directory.CreateDirectory` work cross-platform.
- **Logs/LMP/ permissions failure:** If the directory can't be created (containerised server, read-only mount), self-disable per Drew's design. Verify the warning hits KSP.log and the diagnostic doesn't throw on subsequent calls.
- **Log file open during a crash:** If KSP crashes mid-write, the trailing lines may be truncated. Acceptable. No fsync needed.

### Per-tick proto budget (B.2)
- **Budget=2 with a 50-vessel session-start burst:** Takes 25 ticks (about 0.5s at 60fps) to drain. Acceptable for steady-state; verify against our BUG-051b retry logic that we don't trigger duplicate retries on the still-queued vessels.
- **Queue-peek-but-not-dequeue:** If a peeked item throws on the next tick's Load attempt, does it block the queue indefinitely? Drew's code likely has a failure-counter or dequeue-on-Failed; verify.

---

## Build target gotchas (net472 / Mono)

LmpClient builds against `net472` and runs on KSP's bundled Mono. Modern C# / .NET features that will FAIL to compile or run:

- `record` types
- `init`-only setters
- `Span<T>`, `Memory<T>`, `ReadOnlySpan<T>`
- `Index` / `Range` operators (`^`, `..`)
- Top-level statements
- `System.Text.Json` (use the vendored Newtonsoft adapter at `LmpClient/Utilities/Json.cs`)
- C# 9+ pattern matching beyond what C# 7.x supports

When porting from Drew's branch, search the diff for any of these and adapt. Drew also targets net472, so this should be rare — but verify, don't assume.

---

## Rollback plan

If any phase ships and later reveals a regression:

1. `git revert <phase commit hash>` on a `revert/bug-045-rollback` branch
2. Update [Server/ForkBuildInfo.cs](Server/ForkBuildInfo.cs) — remove the corresponding entry from `ActiveFixes[]`
3. Update [docs/research/01-bug-inventory.md](docs/research/01-bug-inventory.md) — revert BUG-045 status to "Open" with a note about the failed port
4. Update [CLAUDE.md](CLAUDE.md) Stack Notes — add a strike-through entry per existing convention, dated, explaining what went wrong
5. Open an issue summarising the regression for future re-attempts

Because each phase is its own commit and master is green between phases, partial rollback (e.g., revert B.3 but keep B.1 + B.2) is straightforward.

---

## Open questions to resolve during execution

These are flagged for the agent to encounter and surface back to the user when hit:

1. **Does `VesselType.DeployedSciencePart` compile against our KSP DLLs?** Resolved in B.0.
2. **Do the 24 `SendVesselMessage` call sites generate meaningful `reason:` strings, or are half of them too generic to label?** Resolved during B.3; if many are generic, consider going back to Option α (overload).
3. **Does `346ef48a`'s `VesselLoader.cs` change touch the same lines as our `3827ff1e` BUG-008 fix?** Resolved in B.0.
4. **Should the heartbeat consolidation (`2eb4ac14`) be folded into B.3 since both touch `MainSystem.cs`?** Default answer: NO, keep B.3 narrow. Revisit only if B.3's `MainSystem.cs` changes are trivial enough that the agent wants to absorb the heartbeat refactor at the same time.
5. **Mock harness extension feasibility for B.4** — resolved in B.0.

---

## Sources

- BUG-045 entry: [docs/research/01-bug-inventory.md](docs/research/01-bug-inventory.md) lines 368-376
- Original upstream issue: https://github.com/LunaMultiplayer/LunaMultiplayer/issues/308
- Drew's fix commit: `2526e15a` on `origin/Release/0_29_2`
- Supporting infrastructure: `346ef48a`, `4733081d`, `60a2ed5d` on `origin/Release/0_29_2`
- Fork-build registry: [Server/ForkBuildInfo.cs](Server/ForkBuildInfo.cs)
- Bug inventory: [docs/research/01-bug-inventory.md](docs/research/01-bug-inventory.md)
- Stage roadmap: [CLAUDE.md](CLAUDE.md) → "Stage Roadmap" section
- Mock harness design: [docs/research/04-mock-client-harness-design.md](docs/research/04-mock-client-harness-design.md)

---

## Done definition

Strategy B is complete when:
- [ ] B.1 committed; BUG-045 status flipped to Fixed; bug-045 Phase-2 doc exists
- [ ] B.2 committed; per-tick budget + VesselLoadOutcome live on master
- [ ] B.3 committed; VesselSyncLog generates traces; all 24 call sites carry reason strings; CLAUDE.md updated
- [ ] (Optional) B.4 committed; regression test in `MockClientTest`
- [ ] Branch `fix/bug-045-and-vesselsynclog` squash-merged to master with single reviewable history entry
- [ ] User has run manual KSP verification of BUG-045 fix (place deployable, restart server, reconnect, deployable persists)

B.5 (loose sibling triage) is explicitly out of scope and handled in a future planning session.
