# Breakage analysis — Phase 3 per-vessel cadence throttle by lock holder

**Branch:** `feature/server-relay-filtering` (on Phase 2 commit `4454d08a`)
**Scope:** Server-side throttle of Position relays for vessels with no active Control lock (debris, abandoned satellites, stranded probes). Reduces relay cadence from baseline 50ms to ~750ms for inactive vessels (~93% volume reduction on debris fields).
**Spec:** [docs/research/11-server-side-offload-spec.md §5](../../docs/research/11-server-side-offload-spec.md).

## What changed (functional summary)

1. **`Vessel.LastRelayedPositionMs`** — long field, ms timestamp from `ServerContext.ServerClock`. Updated only when a Position relay actually passes the throttle gate (skipped relays do NOT update timestamp, so the next inbound after the window passes unconditionally).
2. **`OptimizationSettings.UnpilotedVesselCadenceMultiplier`** — int, default 5. At default 150ms secondary interval → 750ms throttle window for unpiloted vessels. Set to 1 (or <=1) to disable.
3. **`MessageQueuer.RelayPositionMessage<T>`** — Position-only entry point. Gates the cadence check BEFORE falling through to the composed Phase 1 + Phase 2 filter. Other 8 continuous-state messages stay on `RelayMessageToFlightSceneSameBody` unchanged.
4. **`MessageQueuer.ShouldRelayPositionByCadence`** — pure helper (Guid vesselId, long lastMs, long nowMs, int secondaryMs, int multiplier). 3 branches: multiplier<=1 → true (off), `LockSystem.LockQuery.ControlLockExists` → true (active pilot), else interval comparison with `(long)secondaryMs * multiplier` (overflow-safe cast).
5. **`VesselMsgReader` Position case** — switched from `RelayMessageToFlightSceneSameBody` to `RelayPositionMessage<VesselSrvMsg>`. Inline cast to `VesselPositionMsgData` consolidated with the existing Phase 2 ActiveVesselBodyName capture.
6. **Boot diagnostic `[perf:relay-cadence]`** — enabled (with effective ms window computed) / DISABLED. `(long)secondaryMs * multiplier` cast to mirror hot-path (Phase 3 review S1 fix).
7. **`ForkBuildInfo`** entry `perf:relay-cadence`.
8. **`ServerTest/CadenceThrottleTest.cs`** — 9 cases:
   - Multiplier=1 / 0 / negative → always true
   - First-message-relays (lastMs=0)
   - Within throttle window → false
   - At throttle boundary (==) → true
   - After throttle window → true
   - High multiplier (20×) before / after window
   - Tightened secondary interval still throttles
   - Long idle period (long.MaxValue / 2) no overflow
   - **Control lock held bypasses throttle** ([[feedback-negative-assertions-lock-in-bugs]] discipline — paired positive assertion to the negative cases)

## What didn't change
- No wire-format change. No protocol bump. No client-side change.
- Other 8 continuous-state vessel-message types (Flightstate / Update / Resource / PartSync* / ActionGroup / Fairing) untouched — they keep the Phase 1+2 composed filter and their own cadences (1500ms / 5000ms / etc.) already low.
- All 109 ServerTest cases from Phase 1+2 + 87 pre-feature baseline still pass.
- Phase 1+2 entry points (`RelayMessageToFlightScene`, `RelayMessageToFlightSceneSameBody`) still exist and are still used by the other 8 message types.

## Edge cases analyzed + how covered
| Edge case | Mitigation |
|---|---|
| Vessel just minted, not in store yet | `TryGetValue` miss → throttle short-circuits to "relay" (no LastRelayedPositionMs to stamp); falls through to Phase 1+2 filter normally |
| First Position for an existing vessel | `LastRelayedPositionMs == 0` (struct default) → (nowMs - 0) >> any throttle window → unconditionally relays + stamps |
| Lock acquired mid-stream | LockQuery.ControlLockExists returns true on next throttle decision → throttle bypassed → next message relays immediately + stamps timestamp |
| Lock released mid-stream | Subsequent messages enter throttle branch; gap to previous (full-cadence) relay was <= 50ms (well under any throttle window) so first post-release inbound still relays, then subsequent throttle |
| Multi-player same-agency on same vessel (forward-compat) | LockQuery.ControlLockExists returns true if ANY player holds Control → throttle bypassed (correct: vessel under active control) |
| Operator sets multiplier to absurd value (e.g. 2 billion) | Hot-path cast `(long)secondaryMs * multiplier` prevents int overflow on the actual gate; boot diagnostic also cast (Phase 3 review S1) |
| Server restart resets all LastRelayedPositionMs to 0 | First inbound per vessel post-restart relays unconditionally + stamps; throttle resumes on second inbound. Brief spike, self-corrects within ms |
| Modified client floods Position at way-below-cadence rate | Throttle gate is per-vessel, not per-client → flood from a single misbehaving client is bounded |
| Vessel removed mid-stream | LastRelayedPositionMs stored on the Vessel record; when Vessel is removed from CurrentVessels (RemoveVessel), the timestamp goes with it. Re-added later starts fresh |

## Test plan
- **ServerTest/CadenceThrottleTest.cs** — 9 cases.
- **Full ServerTest** — 118/118 passing (was 109 after Phase 2; +9 Phase 3 cases).
- **LmpClient build** — clean (Phase 3 server-only).
- **Server build** — clean.
- **Out-of-scope:** MockClientTest e2e — deferred to soak window.

## Multi-lens review summary
- **[MUST FIX]** — none.
- **[SHOULD FIX] S1** — boot diagnostic int multiply overflow on absurd dials. **Fixed** — cast to long mirroring hot path.
- **[SHOULD FIX] S2** — no test exercises the Control-lock-present bypass branch. **Fixed** — added `ShouldRelayPositionByCadence_ControlLockHeld_BypassesThrottle` test per [[feedback-negative-assertions-lock-in-bugs]] discipline.
- **[CONSIDER] C1-C3** — doc polish (restart-spike note, live-reload-not-supported note, first-proto-race fallthrough comment). Deferred — none affect correctness; spec §5 + class XML already cover the substance.

## Known limitations + deferred items
- **Position-only.** Flightstate / Update / Resource / PartSync* etc. don't get throttled. Their baseline cadences are already low (1500ms / 5000ms) so the marginal win is small. If a future cohort surfaces Flightstate volume as a hot spot, the same throttle pattern can be applied.
- **Per-vessel state, not per-vessel-per-client.** Throttle decision affects ALL recipients of a given Position relay equally. If a future requirement needs per-recipient throttling (e.g., "throttle to slow clients differently"), that'd need per-recipient state added on top.
- **LiveReload not supported.** Operator changing UnpilotedVesselCadenceMultiplier requires server restart. Same posture as every other Optimization/Interval setting.
- **MockClientTest e2e** — deferred to soak window.

## Risk classification
- **Blast radius:** server-only; affects Position relay timing for unpiloted vessels.
- **Reversibility:** instant via `UnpilotedVesselCadenceMultiplier=1` (1 = throttle off; no restart required because the gate is re-read every relay).
- **Wire compat:** preserved bidirectionally (no wire-format change).
- **Test coverage:** decision math (ShouldRelayPositionByCadence) 100% branch-covered including the lock-present bypass.
- **Soak guidance:** watch for "the debris in tracking station looks like it's lagging behind by ~1s" (correct under default multiplier=5; if too aggressive, operator can drop to 3 or 1). Watch for "active flight feels less smooth" (would indicate the Control-lock-bypass branch isn't firing — but the test pin catches this).
