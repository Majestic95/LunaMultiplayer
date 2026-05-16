# Time/Sync Bug Fix Brainstorm — Handoff for Implementation Review

**Status:** Brainstorm + critic pass. NOT yet validated against code in full. Treat fix options as starting points to investigate, not approved designs.

**Scope:** Five interrelated time/subspace/lock bugs in LMP. Selected because they all live in the same subsystem boundary (TimeSync ↔ WarpSystem ↔ LockSystem ↔ VesselPositionSys) and share root causes around subspace semantics.

**Audience:** The implementation agent picking this up will need to (a) re-verify the diagnoses by reading current code, (b) decide which fix option to pursue per bug, (c) coordinate protocol-breaking changes with upstream maintainer AdmiralRadish before any PR work.

---

## Bugs at a glance

| # | Symptom | Issue | Severity | Protocol break? | Coordination |
|---|---------|-------|----------|-----------------|--------------|
| 1 | Solo player snapped to subspace time unnecessarily | #469 (open) | Annoyance | No | None |
| 2 | Remote vessel appears frozen when distant player is far ahead in time | #251, #129 (closed, not fixed) | UX | No | None |
| 3 | Lock granted across subspaces; vessel authority confused | #292 (closed with wrong fix); downstream #400, #483, #506, #421 (open) | Data integrity | YES | AdmiralRadish (lock handoff) |
| 4 | Jitter near other craft / large vessels | No specific issue (PR #628 fixed part of this) | UX | No | AdmiralRadish (vessel coupling) |
| 5 | Client stuck in `CurrentSubspace = -1` after warp ends | No issue (15s watchdog is only mitigation) | Hard failure | Yes (server dedup) | None |

---

## Bug 1 — Solo-subspace catch-up snap

### Symptom
A single player alone in a subspace gets the time-sync subsystem yanking their game clock to match the subspace's recorded time, even though no one else is in that subspace to compare against. Log breadcrumb: `[LMP] Adjusted time from X to Y due to error Z`.

### Code locations
- [LmpClient/Systems/TimeSync/TimeSyncSystem.cs:147-173](../../LmpClient/Systems/TimeSync/TimeSyncSystem.cs#L147-L173) — `CheckGameTime` snaps via `SetGameTime(targetTime)` when error > 3500ms
- [LmpClient/Systems/Warp/WarpSystem.cs:26](../../LmpClient/Systems/Warp/WarpSystem.cs#L26) — abandoned `AloneInCurrentSubspace` helper, commented out

### Diagnosed root cause
`CheckGameTime` reads `WarpSystem.Singleton.CurrentSubspaceTime` unconditionally — no check for whether the local player is the sole occupant. Maintainer gavazquez has effectively WONTFIX'd this in the issue thread ("ideally physics should run on the server, but that's a lot of work").

### Fix options

**Option A — Activate the abandoned helper (DO NOT SHIP AS-IS)**
The commented-out `AloneInCurrentSubspace` predicate has **inverted logic** as written. The expression `.Count(p => p.Value == CurrentSubspace && p.Key != myName) > 0` returns true when others ARE in the subspace, not when alone. Whoever started this gave up because the predicate was wrong. If used, the implementer must rewrite it before gating anything on it. Even after rewriting, this option has a rejoin race: a second player joining a previously-solo subspace reads the server's stale `Subspaces[id].time` and could snap backward.

**Option B (recommended) — Server-authoritative solo subspace tracking**
Server detects single-occupant subspaces (it already has `ServerContext.Clients.Any(c => c.Subspace == X)` — see [Server/System/WarpSystem.cs:37](../../Server/System/WarpSystem.cs#L37)). For those, server sends a periodic `SoloSubspaceAdvance` message setting `Subspaces[id].time = clientUT - serverNow`. Client never snaps because the server's recorded delta tracks the client's reported UT. Eliminates the rejoin race because the second-joiner reads a fresh delta.

**Option C — Drop the hard snap entirely**
Rely on `SkewClock` (0.85×–1.20×) only. Rejected: a >3.5s drift takes >17s to recover at the 1.2× ceiling, which is unacceptable when another player joins mid-recovery.

### Critic flags to re-verify
- **Inverted predicate** at WarpSystem.cs:26 — confirm by reading the line. This is the single biggest gotcha.
- Read [TimeSyncSystem.cs:210-214](../../LmpClient/Systems/TimeSync/TimeSyncSystem.cs#L210-L214) — `SetGameTime` already has NaN/Inf/negative guards, but does NOT guard against `targetTime == 0` (which can happen when `Subspaces.ContainsKey` returns false and the dictionary lookup defaults to 0). Worth a defensive check.

### Dependencies
- Should land **after** Bug 3 — solo-subspace semantics interact with how vessel authority is keyed in the lock system.

---

## Bug 2 — Remote vessel appears frozen (stall, not drift)

### Symptom
When a remote player is in a subspace far in the future of the local player, their vessel appears frozen for long periods, then snaps when a closer-bracketing position update arrives. Originally reported as "drift" — that framing is wrong.

### Code locations
- [LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs:68-77](../../LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs#L68-L77) — `MaxInterpolationDuration` returns `double.MaxValue` for future-subspace targets, `2 × update interval` for past/equal subspaces

### Diagnosed root cause
The interpolation duration cap is asymmetric. For past/equal subspaces the duration is bounded to ~2× the secondary-vessel update interval (small). For future subspaces it's `double.MaxValue`. With `InterpolationDuration = Target.GameTimeStamp - GameTimeStamp` (line 77), when Target is N seconds ahead, `NumFrames ≈ 50N` per the FixedUpdate rate, so each frame moves the vessel by ~1/50N of its position delta — imperceptible motion. The vessel looks frozen, then jumps when a fresher update arrives. **This is a stall, not unbounded drift.**

### Critic-corrected premise
An earlier framing blamed Unix-epoch-scale numbers losing precision (citing PlagueNZ commit `17f60aa`). That framing is wrong. `GameTimeStamp` is KSP game UT (typically 1e3–1e6 seconds since campaign start), not Unix epoch. Precision is not the problem in the client-side interpolation path. PlagueNZ's commit patched a different value (server's `CurrentUT`) and is not relevant here.

### Fix options

**Option A (recommended) — Symmetric interpolation cap**
Mirror the past-subspace cap on the future side. Change line 68-70 from:
```csharp
private double MaxInterpolationDuration => WarpSystem.Singleton.SubspaceIsEqualOrInThePast(Target.SubspaceId) ?
    TimeSpan.FromMilliseconds(SettingsSystem.ServerSettings.SecondaryVesselUpdatesMsInterval).TotalSeconds * 2
    : double.MaxValue;
```
to a symmetric cap (e.g., `* 2` regardless of past/future, or `* 5` if a wider window is needed). Vessel will visibly skip to its latest known pose rather than crawling. This is what users actually want.

**Option B — Configurable horizon constant**
Add `MAX_SUBSPACE_DELTA_SECONDS` to settings. Beyond it, freeze and label in UI. Rejected by critic: introduces a UX regression (you can't see a friend's vessel move during a rendezvous when subspaces are >30s apart), and KSP's physics-load range can't be gated on this. Filed as RFC, not bug fix.

### Critic flags to re-verify
- Confirm `Target.GameTimeStamp - GameTimeStamp` really does grow linearly with subspace delta in the future case. Trace what `Target` is set to when target is a future subspace.
- Look for downstream consumers of `NumFrames` and `LerpPercentage` that might break under a tighter cap.

---

## Bug 3 — UnloadedUpdate lock across subspaces

### Symptom
A player in subspace A can claim authority over a vessel that physically lives in subspace B's timeline. Manifests as ship rewinds after logout (#400), random craft deletion (#483), despawning ships (#506), random duplication/disappearance (#421).

### Code locations
- [LmpClient/Systems/Lock/LockSystem.cs](../../LmpClient/Systems/Lock/LockSystem.cs) — `AcquireUnloadedUpdateLock` has zero subspace check
- [Server/System/LockSystem.cs](../../Server/System/LockSystem.cs) — server lock registry, single flat dictionary
- Commit `fbc7a8c` — the "fix" that shipped for #292

### Diagnosed root cause
Lock registry is keyed only by `(LockType, VesselId)`. DMP has the same omission (`Dictionary<string, string> serverLocks`, no subspace dimension); LMP inherited it. progfz's #292 write-up identified the problem and proposed three fixes:
- **Option A:** Don't grant lock if requester is in a different subspace from the vessel
- **Option B:** Priority lock for last-controller; only direct control or Update lock overrides
- **Option C:** Disable UnloadedUpdate broadcasting entirely

gavazquez merged **Option C** (`fbc7a8c` just comments out `SendUnloadedSecondaryVesselPositionUpdates` and `SendUnloadedSecondaryVesselUpdates` routines). This silenced the most visible symptoms but the root cause remains: locks are still acquired across subspaces, so the underlying authority confusion still drives downstream issues (#400, #483, #506, #421).

### Fix options

**Option A (recommended — the right fix progfz proposed)**
1. Server tracks each vessel's `AuthoritativeSubspaceId` = whichever subspace last sent a `VesselProtoUpdate` for it
2. Lock key changes from `(LockType, VesselId)` to `(LockType, VesselId, AuthoritativeSubspaceId)`
3. Server rejects `ACQUIRE` when requester's subspace is in the past relative to the vessel's authoritative subspace
4. Restore the routines that `fbc7a8c` commented out (now safe because locks are properly partitioned)

**Migration approach:** Clean break, no shim. The critic pointed out that any compatibility shim has to fabricate the missing subspace dimension from old clients, and every choice for fabrication recreates the bug (use `requester.CurrentSubspace` → original bug; use `LatestSubspace` → punishes past-subspace players). Bump `LMP_PROTOCOL_VERSION`; pre-shim clients are rejected with a "protocol bumped, please upgrade" message.

**Subspace pruning interaction:** `RemoveSubspace` ([Server/System/WarpSystem.cs:34](../../Server/System/WarpSystem.cs#L34)) refuses to remove a subspace with active clients but ignores vessels. After this fix, pruning must also check `WarpContext.Subspaces.Values.Any(s => s.Id == anyVessel.AuthoritativeSubspaceId)`. That's an O(n_vessels) check on every disconnect. Acceptable for typical server scales but worth measuring.

**Option B — progfz's "priority lock"**
Last-controller gets sticky priority. Smaller scope than A but doesn't fix the cross-subspace authority confusion — still possible for two players in different subspaces to fight over a vessel.

### Coordination required
AdmiralRadish is actively working on docking/coupling, which touches `LockType.UnloadedUpdate` handoff during dock-to-undock transitions. Vessel `AuthoritativeSubspaceId` must reassign on docking (two become one) and undocking (one becomes two). Land on master without his sign-off and you'll either get reverted or hit a 3-way merge conflict on the protocol version constant. **File issue first, get design alignment, then PR.**

### Critic flags to re-verify
- Read both `LmpClient/Systems/Lock/LockSystem.cs` and `Server/System/LockSystem.cs` to confirm the actual key format (the description above is from research summary, not direct verification).
- Determine where `AuthoritativeSubspaceId` would be initialized — first ACQUIRE? First proto-update? Server restart? Needs a written invariant.

---

## Bug 4 — Warp-at-distance / big-vessel jitter

### Symptom
Subspace boundaries don't always cleanly hand off control when craft drift near each other. Large vessels (space stations) jitter visibly when other players are sharing physics range.

### Code locations
- AdmiralRadish PR #628 (merged 2026-04-17) — fixed part of this for unpacked rigidbodies
- [LmpClient/Systems/VesselPositionSys/ExtensionMethods/](../../LmpClient/Systems/VesselPositionSys/ExtensionMethods/) — vessel transform/position setters

### Diagnosed root cause(s)
- AdmiralRadish PR #628 fixed the visible rotation-frame-lag and rigidbody snap-back on unpacked parts
- Remaining: vessel transform updates touching `transform.position`/`transform.rotation` only (without `part.rb.position`/`part.rb.rotation`) still create the snap-back pattern in places PR #628 didn't reach
- Subspace boundary handoff is unenforced near other craft — no rule that two players within physics-load range must share a subspace

### Fix options

**Option A — Subsumed by Bug 2's symmetric cap**
Capping future-subspace interpolation eliminates the most visible jitter at large subspace deltas. No standalone work needed if Bug 2 ships.

**Option B (deferred as RFC) — Distance-gated subspace merge**
When two players are within 2.5km in different subspaces, propose a subspace merge to the trailing player. Critic flagged this is not a small fix: KSP's physics-load range is engine-enforced (we can't fake it), so the merge has to be opt-in by the player and the server must not load the other vessel into physics until acceptance. Also has burn/SAS-hold edge cases (don't propose mid-burn). **Treat as feature design, file separately.**

**Option C (recommended for code work) — Continue PR #628's audit**
Walk `LmpClient/Systems/VesselPositionSys/ExtensionMethods/` for any setter that touches `transform.*` without a matching `rb.*`. AdmiralRadish's PR #628 commit message describes the exact pattern:
> "For unpacked (off-rails) parts with rigidbodies, only transform.position and transform.rotation were being set. Unity's physics engine treats this as an external teleport and snaps the rigidbody back toward its last solved position on the next FixedUpdate, causing visible vibration and shaking."

Each candidate site is a small atomic PR.

### Coordination required
**Highest AdmiralRadish-collision area.** Fix C is safe (direct extension of his pattern). Option B is design RFC only — file separately, don't bundle.

### Critic flags to re-verify
- Inventory the actual call sites in `ExtensionMethods/` before estimating scope
- Check if PR #628 already covered them all — `git log upstream/master -- LmpClient/Systems/VesselPositionSys/ExtensionMethods/`

---

## Bug 5 — Stuck-at-warping limbo

### Symptom
Client gets stuck with `CurrentSubspace = -1` after time warp ends. The 15-second watchdog re-requests, but during the gap the client is in limbo (no game time advances, no position updates accepted).

### Code locations
- [LmpClient/Systems/Warp/WarpSystem.cs:24](../../LmpClient/Systems/Warp/WarpSystem.cs#L24) — `CurrentlyWarping => CurrentSubspace == -1`
- [LmpClient/Systems/Warp/WarpSystem.cs:126-134](../../LmpClient/Systems/Warp/WarpSystem.cs#L126-L134) — `CheckStuckAtWarp` 15s watchdog
- [LmpClient/Systems/Warp/WarpSystem.cs:139-149](../../LmpClient/Systems/Warp/WarpSystem.cs#L139-L149) — `CheckWarpStopped` only fires when `!WaitingSubspaceIdFromServer`
- [Server/System/WarpSystem.cs](../../Server/System/WarpSystem.cs) — `WarpContext.NextSubspaceId++` (or equivalent) in the message handler

### Diagnosed root cause
Edge-triggered handshake. `WaitingSubspaceIdFromServer` is set when client sends `NewSubspaceRequest`, cleared on ack. If ack drops, flag stays true, no re-send fires until the watchdog. Watchdog uses `TimeUtil.IsInInterval` which throttles even that.

### Critic-corrected premise (load-bearing)
**The server is NOT idempotent today.** Each `NewSubspaceRequest` mints a fresh subspace ID. Any naive client-side retry loop creates orphan subspaces at the retry cadence, each one broadcast to every other client. A 500ms retry would create dozens of orphans per minute under a stuck-warp scenario.

**This makes server-side dedup load-bearing for the rest of the fix.** It must ship first, alone, with no other behavior change.

### Fix options (ordered, all required)

**Step 1 (recommended) — Server-side request dedup**
Server keeps `Dictionary<(playerId, requestSeq), subspaceId>` cache with TTL eviction (~60s). `NewSubspaceRequest` from a player includes a monotonically incrementing sequence number. If `(playerId, seq)` already in cache, return the cached subspaceId; otherwise mint a new one and cache. No behavior change for non-buggy clients. **No protocol break if the seq field is added optionally and defaults to current behavior.** Ship in isolation as a robustness refactor.

**Step 2 — Client steady-state predicate (DMP pattern)**
Every FixedUpdate where `CurrentSubspace == -1 && TimeWarp.CurrentRateIndex == 0 && Math.Abs(TimeWarp.CurrentRate - 1) < 0.1f`, send `NewSubspaceRequest` if last send was >500ms ago. Safe to ship only after Step 1, because the seq number ensures retries dedupe at the server.

**Step 3 — Server-side backstop**
Any player in `subspaceId == -1` for >30s is force-assigned to `LatestSubspace`. Last line of defense. Acceptable trade: "stuck" becomes "instant time jump to latest" (which triggers `CheckGameTime`'s legitimate snap, no surprise). Document this failure mode.

### Critic flags to re-verify
- Confirm the server actually mints unconditionally — read `Server/System/WarpSystem.cs` and find `NextSubspaceId++` to verify it has no dedup
- Confirm `WarpContext.Subspaces` ConcurrentDictionary growth has no current bound besides `RemoveSubspace` reaping empties

---

## Recommended sequencing

Original brainstorm sequencing (1,5 → 2,4 → 3) was wrong per the critic. Corrected order:

| Step | Bug | Why this order |
|------|-----|----------------|
| 1 | **5a** server dedup | Load-bearing for all retry/robustness work. Ships isolated, no behavior change. |
| 2 | **3** lock keying + protocol bump | Heaviest architectural change. Sets the vessel-authority concept that Bug 1 depends on. Coordinate with AdmiralRadish first. |
| 3 | **1** solo subspace (Option B server-authoritative) | Builds on Bug 3's vessel-authority concept. |
| 4 | **2** symmetric interpolation cap | Isolated. Can land in parallel with 1. |
| 5 | **5b** client steady-state predicate | Safe to ship once 5a is in. |
| 6 | **4C** continue PR #628's rb-set audit | Small atomic PRs. |

Bug 4's Option B (distance-gated merge) is **deferred as design RFC**, not in the bug-fix line.

---

## Release strategy

Issue #671 notes that `master` has 188 commits not in any release branch. Bundle these into two releases:

**0.29.x patch release** — non-breaking:
- 5a (server dedup, backward-compatible if seq field is optional)
- 1 (solo subspace, server-side change)
- 2 (interpolation cap, client-side)
- 4C (rb-set audit, client-side)

**0.30.0 minor release** — protocol break:
- 3 (lock keying with `LMP_PROTOCOL_VERSION` bump)
- 5b (client steady-state predicate; benefits from 0.30.0's stricter server-side validation)

Coordinate the 0.30.0 cut with AdmiralRadish since his docking work also lives on master unreleased.

---

## Open questions for the implementation agent

Before writing any code, validate:

1. **Bug 1 Option B feasibility:** Does the server have a low-cost way to detect single-occupant subspaces on a timer, or does this need a new periodic task? Look at how `BackupSystem` schedules its work in [Server/MainServer.cs:117-118](../../Server/MainServer.cs#L117-L118).

2. **Bug 3 invariant:** When is `AuthoritativeSubspaceId` first set for a vessel? Options: (a) on first proto-update from any client; (b) on first ACQUIRE; (c) inherited from previous owner on dock/undock. Pick before writing migration logic.

3. **Bug 4 PR #628 coverage:** Has AdmiralRadish already swept all `ExtensionMethods/` sites, or are there remaining transform-only setters? Quantify before committing to Fix C scope.

4. **Bug 5 server idempotency:** Is the seq number field a clean addition to `NewSubspaceRequest` without forcing a protocol bump? Inspect the message structure in `LmpCommon/Message/Data/Warp/`.

5. **Coordination with AdmiralRadish:** What's the right channel — GitHub issue, Discord, direct mention on PR #628? Project doesn't have an obvious RFC process; need to establish one or default to issue-comment threads.

---

## Source material

- LMP issue research: GitHub issues #469, #292, #251, #129, #400, #483, #506, #421, #671 (verified open/closed states and merge history)
- AdmiralRadish PR #628 (merged 2026-04-17) — vessel interpolation rotation/rb fix
- PlagueNZ fork commits `b2ca529`, `17f60aa` (May 2026) — adjacent robustness patterns
- DarkMultiPlayer source — predecessor mod, same architectural omissions in `LockSystem` and `WarpWorker`
- EVE Online TiDi documentation — cited only for the interpolation-horizon mental model, not as a direct fix pattern
