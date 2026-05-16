# BUG-001 — Solo-subspace catch-up snap

**Implementation order:** **2nd** in the Option C sequence (after BUG-051a server dedup).

**Status:** Validated against `master` at commit `48df64bd` (2026-05-16). Diagnoses from [03-time-sync-fix-brainstorm.md](../03-time-sync-fix-brainstorm.md#bug-1--solo-subspace-catch-up-snap) verified by direct code read.

**Inventory entry:** [`docs/research/01-bug-inventory.md`](../01-bug-inventory.md) BUG-001 (top of priority list).

**Upstream overlap (reference only, no coordination):** PR [#662 "Time Paradoxes Fix"](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/662) by BraveCaperCat2 is open and broader-scope. Read it before designing the fix; do not depend on it landing.

---

## Symptom

Solo player in their own subspace gets snapped via `SetGameTime` whenever the local game time drifts >3500ms from the server-projected subspace time, even though no one else is present to require synchronization. Reporter log breadcrumb: `[LMP] Adjusted time from X to Y due to error Z`. Often teleports the vessel into terrain.

## Code locations (validated)

- [LmpClient/Systems/TimeSync/TimeSyncSystem.cs:147-173](../../../LmpClient/Systems/TimeSync/TimeSyncSystem.cs#L147-L173) — `CheckGameTime`: reads `WarpSystem.Singleton.CurrentSubspaceTime` unconditionally; snaps via `SetGameTime(targetTime)` when `|currentError| > MaxPhysicsClockMsError` (3500ms). No solo-subspace check.
- [LmpClient/Systems/TimeSync/TimeSyncSystem.cs:208-214](../../../LmpClient/Systems/TimeSync/TimeSyncSystem.cs#L208-L214) — `SetGameTime` guards NaN/Inf/negative but NOT zero.
- [LmpClient/Systems/Warp/WarpSystem.cs:248-261](../../../LmpClient/Systems/Warp/WarpSystem.cs#L248-L261) — `GetSubspaceTime` returns `0d` as silent fallback when the subspace isn't in the local `Subspaces` dictionary. Logs a warning but `SetGameTime` will then snap to UT=0.
- [LmpClient/Systems/Warp/WarpSystem.cs:26](../../../LmpClient/Systems/Warp/WarpSystem.cs#L26) — abandoned `AloneInCurrentSubspace` helper, commented out. **Inverted as written** — `... > 0` returns "alone" when others ARE in the subspace. Do not uncomment without rewriting.
- [Server/System/WarpSystem.cs:135](../../../Server/System/WarpSystem.cs#L135) — `GetEmptySubspaces` already exists on the server. Inverse — `GetSoloSubspaces` (subspace has exactly one client) — would be a 3-line addition.

## Diagnosed root cause (validated)

`CheckGameTime` has no concept of "I am alone in this subspace." It reads the local `Subspaces[CurrentSubspace]` delta (which the server periodically updates from the latest client's perspective) and pushes the local game clock toward it. For a solo subspace, the delta is stale relative to wall-clock: as the player plays, their game UT advances while the recorded `Subspaces[id].time` ages.

Maintainer `gavazquez` effectively WONTFIX'd this in the issue thread ("ideally physics should run on the server, but that's a lot of work"). The reporter's own fix sketch — "short-circuit catch-up when the subspace has a population of one" — is correct and minimal.

## Recommended fix (Option B from brainstorm — server-authoritative)

**Approach:**
1. Add a new `Subspace` field to `Server/Context/WarpContext.cs`: `Solo` (bool, recomputed each tick).
2. Add a `SubspaceSoloStatus` message type (`WarpSrvMsg`) carrying `{ subspaceId, solo }`.
3. Server task (sibling of backup tasks, see [Server/MainServer.cs](../../../Server/MainServer.cs)): every N seconds, for each subspace, check `ServerContext.Clients.Count(c => c.Subspace == subspaceId) == 1`. If true and the subspace's `Solo` flag changed, broadcast `SubspaceSoloStatus` to that one client.
4. On the solo client, on receipt: skip `CheckGameTime`'s `SetGameTime(targetTime)` branch (line 165-169). `SkewClock` (line 160-163) is still safe because it's bounded.
5. **Better:** server pushes a `SoloSubspaceAdvance` message every N seconds setting `Subspaces[id].time = clientReportedUT - serverNow`. Client never falls behind because the delta tracks them.

**Why Option B (server-authoritative push) over Option A (client predicate):**

The abandoned `AloneInCurrentSubspace` (line 26) demonstrates Option A's problem: a second player joining a previously-solo subspace reads the server's stale `Subspaces[id].time` and could snap backward. Server-authoritative tracking closes the rejoin race because the server's recorded delta is always current.

## Out of scope / rejected alternatives

- **Activate the abandoned `AloneInCurrentSubspace` helper** — has inverted logic AND a rejoin race. Reject.
- **Drop the hard snap entirely** — rejected by brainstorm critic: relies on `SkewClock` (0.85x-1.20x) which takes >17s to recover from a 3.5s drift. Unacceptable when another player joins mid-recovery.
- **Defensive `targetTime == 0` guard in `SetGameTime`** — defensible micro-fix but does NOT solve the core bug. Worth adding as a hardening pass; track separately.

## Test plan

**Server tests (new `WarpSystemTest`):**
- Subspace with 0 clients → solo = false (degenerate; nothing to broadcast).
- Subspace with 1 client → solo = true.
- Subspace with 2 clients → solo = false.
- Client joining a previously-solo subspace → solo flips to false; subsequent `SoloSubspaceAdvance` is suppressed.
- Client leaving leaving the 2nd-occupant case → solo flips back to true.

**Client tests (deferred until Stage 4 mock-client harness):**
- Solo subspace + simulated 5s drift → `CheckGameTime`'s snap path does not fire.
- Solo subspace → 2nd-player joins → first observed drift after join triggers normal snap behavior.

## Dependencies

- **Hard dependency:** none. Bug 1 Option B is self-contained on subspace-occupancy data.
- **Soft dependency:** the brainstorm doc claims Bug 1 should land after Bug 3 because "solo-subspace semantics interact with how vessel authority is keyed in the lock system." Read-through of the actual Option B design shows this is wrong — Bug 1 operates on subspace-level data (`Subspaces[id].time`), Bug 3 operates on vessel-level locks (`AuthoritativeSubspaceId`). Different state, different files. The dependency is conceptual, not technical.
- **Note for Option C ordering:** ship 5a (BUG-051a) first regardless. The dedup baseline doesn't matter for Bug 1 specifically but it's the smallest robustness change and earns the right pattern.

## Risks

- **Server-side periodic task overhead.** New solo-detection pass iterates over `ServerContext.Clients`. With typical server scales (<50 clients), trivial. With 500+ clients it merits batching. Profile in soak.
- **Protocol additive.** New message type — clients pre-this-fix will ignore the message (which is fine, they hit the old path). Forward-compat is clean.
- **Mid-burn join race:** if a 2nd player joins mid-snap (`SetGameTime` already in flight), the client should NOT abort the snap. The fix only suppresses subsequent snaps, not in-flight ones. Verify in design.

## Open questions

- **How often does the server check solo status?** 5s seems generous; 1s probably fine. Settings field: `IntervalSettings.SoloSubspaceCheckSeconds`.
- **Should solo state be persisted across server restart?** No — it's recomputed from client occupancy on restart.
- **Should `CheckGameTime` log when it suppresses a snap?** Yes — once per minute at most, with the subspace ID and the suppressed delta, so operators can see the system working.
