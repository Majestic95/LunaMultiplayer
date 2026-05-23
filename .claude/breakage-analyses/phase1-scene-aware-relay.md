# Breakage analysis — Phase 1 scene-aware vessel relay filtering

**Branch:** `feature/server-relay-filtering` (off `origin/master` b965e05f)
**Scope:** Server-side filtering of continuous vessel-state relays (Position / Flightstate / Update / Resource / PartSync{Field,UiField,Call} / ActionGroup / Fairing) to clients NOT in Flight or TrackingStation scenes.
**Spec:** [docs/research/11-server-side-offload-spec.md](../../docs/research/11-server-side-offload-spec.md) §3.
**Workstream:** First of 3 phases (Phase 2 = same-body filter; Phase 3 = per-vessel cadence by lock holder).

---

## What changed (functional summary)

1. **New wire field** — `ClientSceneType` enum + tail-byte on `PlayerStatusSetMsgData` carrying the client's current KSP scene.
2. **Server-side filter** — `MessageQueuer.RelayMessageToFlightScene<T>` parallels the existing `RelayMessageToSubspace<T>` pattern; gates the 9 continuous-state vessel relays in `VesselMsgReader` on the recipient's reported scene.
3. **Pure helper** — `MessageQueuer.ShouldRelayToScene(ClientSceneType)` returns true iff `Flight` / `TrackingStation` / `Unknown` (compat passthrough). Public for direct ServerTest invocation.
4. **Operator gate** — new `OptimizationSettings` group (`OptimizationSettings.xml`) with `SceneAwareRelayEnabled` (default true). Escape hatch — flipping to false reverts to baseline `RelayMessage`.
5. **Boot diagnostic** — `[perf:relay-scene] enabled`/`DISABLED` in `MainServer.Main` so operators can grep gate state without reading XML.
6. **Client-side** — `StatusSystem` maps `HighLogic.LoadedScene` → `ClientSceneType` every CheckPlayerStatus tick (1s) AND on OnEnabled; `StatusIsDifferent` extended to include Scene so transitions trigger SendOwnStatus.

## What didn't change

- Existing `RelayMessage<T>` path untouched — used unchanged for Proto / Sync / Couple / Remove / Decouple / Undock (structural / catch-up relays).
- Existing `PlayerStatusInfo` wire shape unchanged — Scene moved to `PlayerStatusSetMsgData` directly (see §M1 below).
- Existing 87 ServerTest cases + 6 LmpCommonTest cases still pass.
- Subspace authority + cross-agency rejection paths unchanged (Phase 1 is pure-additive filtering on top).
- No protocol bump (additive tail field, backward-read-compat via `Position < LengthBits` guard).

## Edge cases analyzed + how covered

| Edge case | Mitigation |
|---|---|
| Pre-Phase-1 client connects to Phase-1 server | Server reads Scene=Unknown (tail-byte absent), filter treats Unknown as "relay always" (compat preserved) |
| Phase-1 client connects to pre-Phase-1 server | Client writes extra byte; old server's deserializer reads only the fields it knows and ignores trailing bytes (Lidgren default) |
| Phase-1 client never sends PlayerStatusSet | Server's ClientStructure.PlayerStatus.Scene defaults to Unknown → relay-always |
| Scene transition mid-session (Flight → SC) | Client's `CheckPlayerStatus` polling at 1000ms detects change, fires SendOwnStatus; up to 1s of stale filtering during transition (documented in OnEnabled comment) |
| Operator wants to revert without binary downgrade | `OptimizationSettings.SceneAwareRelayEnabled=false`; `RelayMessageToFlightScene` falls back to `RelayMessage` |
| `OptimizationSettings.SettingsStore` accessed before settings load | `SettingsBase<T>.SettingsStore` initialized at type-init to `new T()` with property defaults — safe to read pre-Load |
| Cross-version PlayerStatusReply array corruption (the [MUST FIX] from upgrade-lens review) | Scene moved out of `PlayerStatusInfo` (embedded as unframed array in Reply) into `PlayerStatusSetMsgData` (terminal-position tail-byte is safe). Explicit DO-NOT-add-fields comment block in PlayerStatusInfo.cs prevents regression |
| StatusSystem.OnEnabled fires first SendOwnStatus with Scene=Unknown | Initialize `MyPlayerStatus.Scene` BEFORE `SendOwnStatus()` call (1-line fix in OnEnabled) — closes the "1s of relay-always after join" finding |
| Operator can't tell if filter is active | Boot diagnostic emits per-feature `[perf:relay-scene] enabled/DISABLED` line so operators can grep — paired with the `[fork] ... perf:relay-scene` token in the existing fork banner |

## Test plan

- **ServerTest/SceneAwareRelayTest.cs** — 10 cases pinning every `ShouldRelayToScene` branch including: Unknown→true (compat), Flight→true, TrackingStation→true, SpaceCenter→false, Editor→false, MainMenu→false, RnD→false, Mission→false, Other→false, plus a regression-fence assertion that exactly 2 non-Unknown scenes return true (forces deliberate updates if a future contributor adds a new true scene).
- **Full ServerTest** — 97/97 passing.
- **LmpCommonTest** — 6/6 passing (no new cases needed — wire shape change covered indirectly via Server-side ServerTest).
- **Server build** — clean (0 errors; 29 pre-existing warnings).
- **LmpClient build** — clean (0 errors; 7 pre-existing warnings).
- **Out-of-scope:** MockClientTest end-to-end + KSP-bound LmpClientTest — deferred. The pure-helper ServerTest covers the load-bearing filter decision; MockClientTest e2e is queued for the workstream's soak window.

## Multi-lens review summary (parallel general-purpose agents per `[[feedback-review-lens-framing]]`)

### server-systems lens
- Verdict: ship-ready. 0 [MUST FIX], 3 [SHOULD FIX], 2 [CONSIDER].
- S1 (doc-comment listing Decouple/Undock as intended sites) → **fixed** (MessageQueuer XML corrected).
- S2 (spec §3.e said 11 sites; implementation correctly uses 9) → **fixed** (spec text updated).
- S3 (predicate cost is O(N_clients) per relay) → noted; net win because skipped SendToClient costs more than the predicate. Leave; reassess if Phase 3 layers asymmetrically.
- C1 (SpaceCenter exclusion reasoning inline) → **fixed** (inline comment added).
- C2 (`internal` vs `public` on ShouldReceiveVesselUpdate) → **fixed** (refactored to pure-helper pattern; ShouldRelayToScene is public for ServerTest, wrapper stays internal).

### consumer lens
- Verdict: 2 [MUST FIX] + 4 [SHOULD FIX] + 4 [CONSIDER].
- M1 (no boot diagnostic) → **fixed** (boot diagnostic added in MainServer.Main).
- M2 (SendOtherPlayerStatusesToNewPlayer omits Scene from projection) → **moot after rework** — Scene moved out of PlayerStatusInfo; Reply no longer carries it at all.
- M3 (vanilla 0.31.0 compat downgraded to [CONSIDER] on closer reading) → noted.
- S4 (no relay-stats diagnostic) → deferred; out of scope for Phase 1, queued for Phase 4 if cohort signals.
- S5 (1s scene-change window undocumented in XML) → **fixed** (XML comment in OptimizationSettings expanded).
- S6 (operator surprise on auto-creation of OptimizationSettings.xml) → covered by M1 fix (boot diagnostic surfaces the file).
- C7 (per-message-type kill switch) → out of scope; flagged in spec §10.
- C8 (third-party fork interop) → noted; documented in enum XML.
- C9 (ResearchAndDevelopment enum value unused) → kept for future-proofing per spec.

### upgrade lens
- Verdict: 1 [MUST FIX] + 2 [SHOULD FIX] + 2 [CONSIDER].
- **M1 (PlayerStatusReply array corruption from embedded tail-byte) → fixed via REWORK** — Scene moved out of PlayerStatusInfo into PlayerStatusSetMsgData. The PlayerStatusInfo file gained a multi-line DO-NOT-add-fields-here comment block to prevent future regression. This was the load-bearing finding; without the fix, every cohort upgrade would corrupt other players' status lists on join.
- S2 (no boot diagnostic) → same as consumer M1; **fixed**.
- S4 (cohort mixed-version asymmetry during upgrade window) → documented in spec §6.5 compat matrix.
- S5 (OnEnabled fires SendOwnStatus with Scene=Unknown) → **fixed** in StatusSystem.OnEnabled.
- C6 (PlayerStatusReply scenario walkthrough for rollback) → checked; clean.

## Known limitations + deferred items

- **1s scene-change latency** — `CheckPlayerStatus` polls at 1000ms. A client transitioning Flight → SpaceCenter receives Position/Flightstate spam for up to 1s after transition. Acceptable for Phase 1; future improvement could hook `GameEvents.onGameSceneLoadRequested` for sub-100ms convergence.
- **Per-message-type kill switch granularity** — single `SceneAwareRelayEnabled` flag. If a regression surfaces specific to one message type, the only option is "disable the whole feature." Acceptable for Phase 1; revisit if soak surfaces a per-type regression.
- **MockClientTest end-to-end coverage** — deferred to soak window. The unit-test surface (`SceneAwareRelayTest`) covers the load-bearing filter decision; a 3-client harness e2e is queued but not blocking Phase 1 ship.

## Risk classification

- **Blast radius:** server-wide network filter on the dominant message types. A correctness bug would manifest as missing vessel updates for legitimate Flight/TS recipients OR as no filtering at all (perf neutral).
- **Reversibility:** instant — operator sets `SceneAwareRelayEnabled=false`, restarts settings load (if added) OR restarts server; reverts to baseline.
- **Wire compat:** preserved bidirectionally (additive tail-byte with backward-read-compat).
- **Test coverage:** decision math (ShouldRelayToScene) 100% branch-covered; integration path covered by the existing 87 ServerTest + 6 LmpCommonTest baseline still green.
- **Soak guidance:** watch for "I sat at KSC and friend's vessel disappeared from tracking station" (would indicate Phase 1 wrongly filtering TrackingStation — but the test pins TS=true) AND "the Status window's other-player display is broken" (would indicate PlayerStatusReply array corruption regressed — but the rework covers this).
