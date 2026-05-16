# BUG-051 — Stuck-at-warping limbo (`CurrentSubspace == -1` after warp ends)

**Implementation order:** **1st** (5a server dedup) and **4th** (5b client predicate) in the Option C sequence.

**Status:** Validated against `master` at commit `48df64bd` (2026-05-16). Diagnoses from [03-time-sync-fix-brainstorm.md](../03-time-sync-fix-brainstorm.md#bug-5--stuck-at-warping-limbo) verified by direct code read.

**Why split into 5a + 5b:** 5a (server-side request dedup) is load-bearing — without it, any naive client retry creates orphan subspaces at the retry cadence. 5a ships isolated first, with no behavior change. 5b (client-side steady-state predicate) then closes the warp-stuck loop safely.

**New bug ID:** This symptom is not catalogued in `01-bug-inventory.md` (closest is BUG-049 "warp toolbar unresponsive," which is a different symptom). Added as BUG-051 in this Phase-2 pass.

---

## Symptom

Client gets stuck with `CurrentSubspace = -1` after time warp ends. The 15-second watchdog re-requests, but during the gap the client is in limbo (no game time advances, no position updates accepted).

## Code locations (validated)

- [LmpClient/Systems/Warp/WarpSystem.cs:24](../../../LmpClient/Systems/Warp/WarpSystem.cs#L24) — `CurrentlyWarping => CurrentSubspace == -1`
- [LmpClient/Systems/Warp/WarpSystem.cs:126-134](../../../LmpClient/Systems/Warp/WarpSystem.cs#L126-L134) — `CheckStuckAtWarp` 15s watchdog
- [LmpClient/Systems/Warp/WarpSystem.cs:139-149](../../../LmpClient/Systems/Warp/WarpSystem.cs#L139-L149) — `CheckWarpStopped` only fires when `!WaitingSubspaceIdFromServer`
- [LmpClient/Systems/Warp/WarpSystem.cs:358-363](../../../LmpClient/Systems/Warp/WarpSystem.cs#L358-L363) — `RequestNewSubspace` sets the flag, sends, stamps `_stoppedWarpingTimeStamp`
- [Server/System/WarpSystemReceiver.cs:14-34](../../../Server/System/WarpSystemReceiver.cs#L14-L34) — `HandleNewSubspace`: takes `CreateSubspaceLock`, `TryAdd` with `NextSubspaceId`, broadcasts, `NextSubspaceId++`. **No dedup table.**
- [LmpCommon/Message/Data/Warp/WarpNewSubspaceMsgData.cs:13-15](../../../LmpCommon/Message/Data/Warp/WarpNewSubspaceMsgData.cs#L13-L15) — request fields: `PlayerCreator`, `SubspaceKey`, `ServerTimeDifference`. **No sequence number.**

## Diagnosed root cause (validated)

Edge-triggered handshake on the client + non-idempotent mint on the server:

1. Client `RequestNewSubspace` (line 358) sets `WaitingSubspaceIdFromServer = true`, sends `WarpNewSubspaceMsgData`, stamps `_stoppedWarpingTimeStamp`.
2. Server `HandleNewSubspace` mints a fresh subspace ID under `CreateSubspaceLock` and broadcasts. Every retry mints a *new* subspace — there is no `(playerId, requestId)` dedup.
3. If the broadcast ack drops, the flag stays `true`, no re-send fires until the 15s watchdog (`CheckStuckAtWarp`, line 128). `CheckWarpStopped` (line 144) is gated on `!WaitingSubspaceIdFromServer` so it cannot re-fire either.
4. Watchdog re-call uses `TimeUtil.IsInInterval` which throttles even that path.

**Load-bearing consequence:** any naive client retry loop (e.g. drop to 500ms) creates dozens of orphan subspaces per minute under a stuck-warp scenario. Each one broadcasts to every other client. **The server must dedupe before the client can be made more aggressive.**

## Fix (Option C order)

### 5a — server-side request dedup (ships FIRST, isolated)

**Scope:** server-side only, plus an optional `RequestSeq` field on `WarpNewSubspaceMsgData`. No protocol break: pre-fix clients send the field as 0 / unset, and the server treats absent/zero as "always mint" (existing behavior).

**Approach:**
- Add `public uint RequestSeq;` to `WarpNewSubspaceMsgData` (after `SubspaceKey`). Update `InternalSerialize`/`InternalDeserialize`/`InternalGetMessageSize`.
- Add `Dictionary<(string playerId, uint seq), int subspaceId>` cache in `WarpSystemReceiver` (or a sibling `WarpRequestCache` static), with TTL eviction (~60s).
- `HandleNewSubspace`: if `seq != 0 && cache.TryGetValue((player, seq), out var cachedId)`, broadcast the cached subspace ID to the requester only and return. Otherwise mint as today and store `(player, seq) -> NextSubspaceId` in the cache before incrementing.
- Client `RequestNewSubspace` allocates a fresh `RequestSeq` per fresh stuck-detection cycle, reuses the same `RequestSeq` for any retries within that cycle.

**Behavior change for non-buggy clients:** None. They send `RequestSeq` and get the same single mint as today.

**Test plan:**
- New `WarpSystemReceiverTest`: send same `(player, seq)` twice → cache hits, second `HandleNewSubspace` does not increment `NextSubspaceId`.
- TTL eviction test: insert, age past 60s, insert again → mints fresh ID.
- Concurrency: 10 parallel calls with same `(player, seq)` → exactly one mint (use existing `CreateSubspaceLock`).

### 5b — client steady-state predicate (ships AFTER 5a; 4th in Option C)

**Scope:** client-side only. Adds a tighter retry trigger that piggybacks on 5a's dedup safety net.

**Approach:**
- Every FixedUpdate where `CurrentSubspace == -1 && TimeWarp.CurrentRateIndex == 0 && Math.Abs(TimeWarp.CurrentRate - 1) < 0.1f && (LunaComputerTime.UtcNow - _lastSubspaceRequest).TotalMilliseconds > 500`, send `RequestNewSubspace`.
- Same `RequestSeq` as the original request (so 5a's dedup catches the duplicate).
- Clear `WaitingSubspaceIdFromServer` on ack as today; on each retry, leave it set.

**Test plan (Stage 4 mock-client harness):**
- Simulate dropped ack → client retries at 500ms cadence → server returns the same subspace ID each time → client unsticks within 1s.

### 5c — server-side backstop (defer; ship only if 5a/5b are insufficient)

Any player in `subspaceId == -1` for >30s is force-assigned to `LatestSubspace`. Last line of defense. Acceptable trade: "stuck" becomes "instant time jump to latest" (which triggers `CheckGameTime`'s legitimate snap — no surprise). Document this failure mode in CLAUDE.md if it ships.

## Out of scope

- Wider rework of warp/subspace state machine (deferred to a separate design if 5a-5c don't close the gap).
- `WarpContext.Subspaces` unbounded growth — currently only `RemoveSubspace` ([Server/System/WarpSystem.cs:34-51](../../../Server/System/WarpSystem.cs#L34-L51)) reaps. Worth a separate audit; out of scope here.

## Dependencies

- 5a depends on **nothing**. Smallest robustness baseline change.
- 5b depends on 5a being in. Without 5a, 5b creates orphan subspaces.
- 5c depends on 5a + 5b for safe ordering.

## Open questions

- **Should `RequestSeq` be `uint` or `Guid`?** `uint` is cheaper on the wire (4B vs 16B); `Guid` removes the wraparound risk and avoids cross-restart collision. Pick `Guid` if the protocol overhead is acceptable.
- **Cache eviction strategy: TTL or size cap?** TTL (60s) is simpler; size cap (last 100 per player) handles long-stuck cases without growing forever. Probably both.
- **`CheckStuckAtWarp` 15s threshold:** keep as fallback even after 5b ships? Probably yes — defense in depth.
