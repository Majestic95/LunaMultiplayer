# Pre–Private-Testing KSP Soak Checklist

Run this before opening the door to other testers. Code-side, the fork is in better shape than it has ever been (all 10 top-10 bugs closed, 57 + 87 + 12 + 6 unit/integration tests green, BUG-008 4a pack-on-load shipped in commit `8fc6d109`). What no harness can verify is that the fixes actually behave correctly inside a running KSP. **This checklist is the manual side of that verification.**

For each entry: run the repro, observe the symptoms, log the result in the table at the bottom. If something fails, file a new entry in [docs/research/01-bug-inventory.md](research/01-bug-inventory.md) — don't paper over it; the whole point of this pass is to catch what the unit tests couldn't.

**Server you'll soak against:** stand up a fresh dedicated server (`Server/Server.csproj`, `.NET 10 SDK`). Two clients in different KSP installs (or one KSP install + a second `LmpClient.dll`-equipped install on the same box) — call them P1 and P2.

**Log markers to grep in [`KSP.log`](.) / server log:** any `[fix:BUG-...]` line (use `grep -F "[fix:"`). Specifically:
- `[fix:BUG-008]` — snap path fired (existing Phase A)
- `[fix:BUG-008-pack]` — new pack-on-load path fired (4a)
- `[fix:BUG-010]` — pinned-vessel broadcast on disconnect
- `[fix:BUG-013]` — reaction-wheel locale scrub
- `[fix:BUG-023]` — protoModuleCrew null scrub
- `[fix:BUG-025]` — R&D node rejection-back-to-sender
- `[fix:BUG-033]` — backup scenario serialization under per-scenario lock

---

## 1. BUG-008 — Landed-vessel polygon scramble (the new 4a slice)

**Why this is at the top:** you've reported the symptom still happens after Phase A. 4a is the targeted fix. If you still see the scramble after this pass, the next step is item 4b (server-stored `lmpTerrainAltitude`) or 4c (phantom-force suppression).

**Cold-PQS repro** (the canonical case):
1. Start a fresh server. P1 connects, places a stock plane (Aeris 3A, SPH stock) on the Kerbin runway. Quit P1.
2. P2 connects on a body LMP hasn't touched this session. Easiest cold-cache: P2 should have just launched KSP, NEVER visited the Kerbin runway scene, never zoomed into Kerbin in the tracking station.
3. P2 walks to Tracking Station → focus the runway plane → "Fly".
4. **Expected:** vessel pops in, sits frozen for ~0.5-2 s (the new pack-wait), then physics engages on stabilised terrain. No exploding parts, no scrambled polygons, no underground teleport.
5. **Failure mode pre-4a:** vessel spawns, parts explode immediately, or the craft scrambles and detonates within 1-2 s.

**Log expectation:** P2's `KSP.log` should contain `[fix:BUG-008-pack] vessel <guid> arrived loaded on Kerbin (LANDED); packing for PQS stabilise wait` followed by `[fix:BUG-008-pack] PQS stabilised for vessel <guid> at <metres> m` and `[fix:BUG-008-pack] vessel <guid> unpacked after PQS alignment` within ~1-2 s.

**Warm-PQS regression check:** repeat with P2 having visited the runway recently (or after waiting through one cold soak). The pack path should still fire and still finish cleanly — just faster (PQS sample stable on the first poll).

**EVA regression check:** P1 places a kerbal-on-EVA next to the runway. P2 connects, observes the EVA. **Expected:** no double-pack NRE in `KSP.log` (grep for `KerbalEVA.fsm`). The log should show `[fix:BUG-008-pack] vessel <guid> is EVA on Kerbin; deferring to VesselLoader's existing GoOnRails. Continuing on snap-only path.` — confirms the new EVA short-circuit.

---

## 2. BUG-010 — Floatplane-on-lake disconnect (Parts A + B)

**Repro from the Phase-2 doc [`docs/research/02-analysis/bug-010-disconnect-vessel-handoff.md`](research/02-analysis/bug-010-disconnect-vessel-handoff.md):**

**Variant A — clean disconnect:**
1. P1 builds a floatplane, lands it gently on a lake near KSC. P2 connects and approaches in another craft until P1's floatplane is in P2's physics range.
2. P1 clicks **Disconnect** from the LMP menu (clean).
3. **Expected on P2's screen:** floatplane stays intact on the water, no kraken, no joint pop, no part detonation. Tracking-station icon may briefly disappear and reappear; that's fine. Floatplane should be marked as a stationary tracking icon, not "in flight".
4. **Failure mode pre-fix:** floatplane explodes / submerges / parts pop off the moment P1 disconnects.

**Variant B — ungraceful drop:** repeat but P1 kills their KSP process (Task Manager → End Task). The fix here is Part A alone (Part B requires a graceful disconnect handshake). **Expected:** still no kraken, floatplane survives via the immortal-pin from Part A. The on-disk server proto may be a few seconds stale (Part B couldn't run), but the floatplane object is intact on P2.

**Variant C — dock-then-logoff:** P1 docks a small craft to P2's station. P1 disconnects. P2 takes control of the merged ensemble, then undocks. **Expected:** P1's old craft (now a separate vessel) settles to P2's subspace authority within one proto-update cycle, no exploding.

**Log expectation (server-side):** `[fix:BUG-010] broadcast VesselPinned for <guid>` on each lock-owned vessel before the standard lock-release storm. Part B graceful: P1's client `KSP.log` shows `[fix:BUG-010-B] flushing N owned vessels before disconnect`.

---

## 3. BUG-023 — Astronaut Complex / Tracking-Station info pane

**Repro:**
1. P1 launches a manned craft (any stock 3-kerbal pod will do) into orbit. P2 connects mid-flight.
2. P2 goes to Tracking Station, focuses P1's craft, opens the info pane (the right-hand panel that shows crew portraits).
3. **Expected:** info pane renders crew correctly, no freeze, no NRE-loop in P2's log.
4. **Failure mode pre-fix:** clicking the vessel freezes the info pane; `KSP.log` floods with `InvalidOperationException: Failed to compare two elements in the array` from `KbApp_VesselCrew.CompareSeatIdx`.

**Astronaut Complex variant:** P2 opens the Astronaut Complex while P1's kerbals are deployed. Same info-pane surface. **Expected:** kerbal entries render cleanly.

**Autosave round-trip:** P2 saves manually (the autosave path) while P1 is mid-orbit, then `Load` the autosave. **Expected:** no NRE in `Vessel.UpdateCaches` on the post-load `FixedUpdate` storm. Specifically grep `Part.RegisterCrew` and `ModuleCommand.UpdateControlSourceState` for NREs — both should be silent.

**Log expectation:** if P2's client encountered a null-crew proto (the wire race condition the fix defends against), `KSP.log` shows `[fix:BUG-023]: Scrubbed N null protoModuleCrew entries…`. Zero scrubs is fine — means the race didn't fire — but the scrub firing is also fine (means the safety net worked).

---

## 4. BUG-013 — Reaction-wheel locale crash on non–en-US KSP

**This one needs a non-English Windows locale to actually repro.** Skip if both P1 and P2 are en-US.

**Repro (only meaningful if you have a non-en-US KSP install handy):**
1. P1 (non-en-US locale) launches a craft with a reaction wheel (any probe with `ModuleReactionWheel`).
2. P2 (any locale) receives the proto.
3. **Expected:** P2's `KSP.log` shows no NRE on parse of `stateString`. `[fix:BUG-013]` log line confirms the sanitiser normalised the locale-encoded boolean.
4. **Failure mode pre-fix:** P2 crashes on parse or the reaction wheel appears stuck in the wrong state.

**Skip rationale:** if everyone testing is en-US, this is theoretical — the fix is server-side and ran 87/87 unit tests green. Real exposure starts at scale.

---

## 5. BUG-025 — R&D node duplicate-purchase

**Repro:**
1. Both P1 and P2 connect to a science-mode or career-mode server. Both open the R&D facility.
2. Both have enough science to unlock the same tech node. **Within the same physics tick** (or close to it — practice the timing), both click the unlock button on the same node.
3. **Expected:** one player gets the unlock; the other is refunded the science cost and sees a chat-style notification or log line. Neither player's local science balance double-deducts.
4. **Failure mode pre-fix:** both players' science decrements, only one gets the tech, the other's science is silently lost.

**Log expectation:** server log shows `[fix:BUG-025] rejecting duplicate ShareProgressTechnologyMsgData from <player> for <techId>; tech already unlocked by <other-player>`. Losing client's log shows the rejection message handler firing and `ShareScienceSystem.Singleton.StartIgnoringEvents()` wrapping the refund (no science-feedback loop).

---

## 6. BUG-033 — Backup scenario serialization race

**This is hard to provoke manually; the unit tests cover the race.** A loose soak surrogate:

1. Run a session for ~30 min with both players actively earning science / funds / contracts. Server should be configured to take periodic backups (`BackupSettings.IntervalMs` set to a few minutes).
2. **Expected:** server log shows backup runs with no `InvalidOperationException: Collection was modified` from `ConfigNode.ToString()`. Backup files in `Universe/Backups/` are well-formed.
3. **Failure mode pre-fix:** sporadic backup-task crash that kills the worker; backups stop without a loud signal.

**Log expectation:** server log shows `[backup] flushed scenarios in N ms` lines without any exception stacks involving `Server/System/Scenario/`.

---

## 7. Cross-cutting sanity checks (run during any of the above)

- **Fork banner:** server log on boot should show `[fork] Majestic95/LunaMultiplayer protocol 0.30.0 active fixes: BUG-051a, BUG-001, …, BUG-008-pack`. Confirm `BUG-008-pack` is at the end of the list.
- **Web dashboard:** `curl http://<server>:8900/fork` returns JSON with `ActiveFixes[]` including `BUG-008-pack`. `curl http://<server>:8900/log?level=Error` (after Stage 3.7 wired the endpoint) returns recent errors only.
- **VesselSyncLog:** P1 and P2 each accumulate a `Logs/LMP/VesselSyncLog.txt` truncated-on-launch trace. Spot-check a handful of `ARRIVED` / `LOADED` lines for the vessels exercised above — useful for retro-debugging if something subtle goes wrong.

---

## Results table

Fill this in as you run each item. If any row is RED, file a new bug entry before opening private testing.

| Item | Status | Notes / log evidence |
|------|--------|----------------------|
| 1. BUG-008 cold-PQS scramble | ⬜ |  |
| 1. BUG-008 warm-PQS regression | ⬜ |  |
| 1. BUG-008 EVA short-circuit | ⬜ |  |
| 2. BUG-010 Variant A clean disconnect | ⬜ |  |
| 2. BUG-010 Variant B ungraceful drop | ⬜ |  |
| 2. BUG-010 Variant C dock-then-logoff | ⬜ |  |
| 3. BUG-023 Tracking-Station info pane | ⬜ |  |
| 3. BUG-023 Astronaut Complex | ⬜ |  |
| 3. BUG-023 autosave round-trip | ⬜ |  |
| 4. BUG-013 (non-en-US) | ⬜ N/A unless non-en-US |  |
| 5. BUG-025 R&D race | ⬜ |  |
| 6. BUG-033 backup soak | ⬜ |  |
| 7. Cross-cutting (banner, /fork, VesselSyncLog) | ⬜ |  |

**Estimated time:** 60-90 min for items 1-3 + 7 (the high-blast-radius ones). Items 4-6 are bonus / can be deferred if time-boxed.

When all rows in the table are GREEN (or filed-as-followup with a documented BUG entry), open the door to private testers.
