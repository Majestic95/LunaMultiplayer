# Phase 2 — Kolony subspace policy (R1) — pre-spec

**Status:** PRE-IMPLEMENTATION. Drafted 2026-05-18 (session 24) after operator decision to forgo two-client smoke for Phases 0 / 1 / 1.5 R0 and proceed to Phase 2.

**Branch:** `feature/per-agency-mks` (worktree `F:\luna-multiplayer-mks`), tip `d1822cdd` at the time of drafting.

**Scope:** Pin concrete file:line anchors on both the MKS and LMP sides for the R1 fix described in the handoff doc (`mks-lmp-compatibility-handoff.md` v3.3 §R1 + §Phase 2). Verified design + test plan + acceptance criteria. **No code changes in this commit** — same audit-via-pre-spec discipline that validated Phase 1 XMLs and Phase 1.5 R0.

**Source pins (do not re-clone if still current):**

- **MKS** `ed0f6aa6` at `F:\tmp\mks-external\MKS\`
- **USITools** `4ad5cdd8` at `F:\tmp\mks-external\USITools\`
- **LMP** `feature/per-agency-mks` tip `d1822cdd`
- **Upstream coordination check (cleared):** `AdmiralRadish` since 2026-04-01 has exactly ONE commit in the `Warp` area — [`2eeb1d05`](https://github.com/LunaMultiplayer/LunaMultiplayer/commit/2eeb1d05) — UI-only display fix in `LmpClient/Systems/Warp/WarpEntryDisplay.cs` (7+ / 3-). No `Warp` logic refactor in flight; no `KolonyTools` activity. Safe to land Phase 2 without coordination.

---

## 1. The bug R1 fixes — verified end-to-end trace

Already documented in handoff §R1 (line 125-129); summarised here so reviewers don't context-switch.

**Setup:** Alice is at her base on Duna, time-warping at 100,000x in subspace 5 (server UT 1,000,000). Bob is in orbit around Kerbin in subspace 3 (server UT 200,000), 1x. Both have `MKSModule` parts on their respective vessels.

**The loop:**

1. Alice's vessel `FixedUpdate` runs `KolonizationManager.Instance.FetchLogEntry(aliceVesselId, dunaBodyIndex)` ([KolonizationManager.cs:67-88](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationManager.cs#L67-L88)). On a fresh entry, `LastUpdate` and `KolonyDate` are set to `Planetarium.GetUniversalTime()` (lines 74-75), which reflects Alice's subspace UT (~1,000,000).
2. Each tick, MKS code (`MKSModule` and friends, governed by `lastCheck` patterns like USITools' [ModuleLogisticsConsumer.cs:12,60,73](file:///F:/tmp/mks-external/USITools/Source/USITools/Logistics/ModuleLogisticsConsumer.cs)) computes accrual deltas against `Planetarium.GetUniversalTime() - LastUpdate`. Alice's deltas reflect her warp-accelerated UT advance.
3. After Alice's mutation, `KolonizationManager.TrackLogEntry` ([KolonizationManager.cs:90-114](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationManager.cs#L90-L114)) writes the updated entry into `KolonizationScenario.Instance.settings` (line 113).
4. LMP's 30-second scenario SHA pass picks up the changed `KolonizationScenario` blob and broadcasts it to other clients (currently — Phase 3 will partition this per-agency).
5. Bob's instance applies the scenario blob. Bob's KolonizationInfo now contains Alice's entry with `LastUpdate = 1,000,000`.
6. Bob's instance reads UT from his own subspace (~200,000). When his code aggregates kolony entries for the same body (`GetGeologyResearchBonus` at line 116, `GetBotanyResearchBonus`, `GetKolonizationResearchBonus`), the math operates on numbers minted in two different time bases. Cross-vessel aggregation drifts.
7. If Bob later approaches Duna and his vessel becomes physics-loaded near Alice's base, his `Vessel.FixedUpdate` might recompute kolony bonuses with stale-relative-to-his-UT data. Production effects come from delta-time accrual being calibrated to one player's UT base and consumed in another's.

**Net effect:** Cross-player kolony aggregation drifts; scenario sync merges incoherently; "same kolony accrues twice" symptom from handoff §R1 line 127. Even within a single agency under per-agency mode (Phase 3 landed), the issue persists because time-base divergence is upstream of agency partition — the `Planetarium.GetUniversalTime()` reads happen in client-local subspaces regardless of where the entries get stored.

**Why a simpler fix doesn't work:**

- **Pin all UT reads to the server's wall-clock UT.** Would require patching every `Planetarium.GetUniversalTime()` call in MKS — dozens of sites across `KolonyTools` + `USITools`. Brittle to USI updates; high surface area.
- **Single-writer per scenario (Update-lock holder).** Possible (handoff §R1 line 129 mentions it as an "and/or") but doesn't help when two players control separate kolony vessels at the same body — both legitimately write, but in different time bases.
- **Server-side authoritative tick.** Out of scope; "long-term server tick" is handoff Phase 5 territory.

**Why Phase 2 (subspace join on kolony proximity) works:**

When Bob approaches Alice's kolony radius, Bob's subspace snaps forward to match Alice's. Now both `Planetarium.GetUniversalTime()` reads happen in the same subspace; subsequent accrual + scenario-write operations are time-base-coherent. The cost is one UT jump for Bob (well-protected by the jank-mitigation stack in §3). The benefit is that all subsequent kolony math is time-base-aligned across the cohort while both players remain in physics range. Drift only re-accumulates if a player walks away and resumes warping in a different subspace — and at that point, neither is actively writing to the other's kolony in a way that double-counts.

This is a defensive measure, not a complete fix. Phase 3 (R2 ShareKolony per-agency routers) closes the scenario-sync drift completely under `PerAgencyCareer=true`. Phase 2 ships sooner and helps both modes.

---

## 2. The correct fix — sibling routine to `WarpIfSpectatingToController`

**Anchor methods on the LMP side:**

| File | Line | Symbol |
|------|------|--------|
| [WarpSystem.cs](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs) | 47-72 | `CurrentSubspace` setter — broadcasts `ChangeSubspaceMsg`, calls `ProcessNewSubspace()` |
| [WarpSystem.cs](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs) | 121-133 | `OnEnabled` — where new Update-routines get registered |
| [WarpSystem.cs](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs) | 142-152 | `WarpIfSpectatingToController` — the structural precedent for Phase 2 |
| [WarpSystem.cs](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs) | 272-282 | `WarpIfSubspaceIsMoreAdvanced(int)` — one-direction-only snap (forward only) |
| [WarpSystem.cs](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs) | 336-338 | `GetPlayerSubspace(string)` — name → subspace-id lookup |
| [WarpSystem.cs](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs) | 399-403 | `ProcessNewSubspace` — calls `TimeSyncSystem.SetGameTime(CurrentSubspaceTime)` |
| [WarpSystem.cs](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs) | 412-426 | `SafeToSync(int)` — atmospheric/orbital-decay gate; **load-bearing for Phase 2** |
| [LockQueryUpdate.cs](file:///F:/luna-multiplayer-mks/LmpCommon/Locks/LockQueryUpdate.cs) | 39-42 | `GetUpdateLockOwner(Guid)` — vessel-id → player-name |

**Anchor on the MKS side** (only used by the Phase 2 predicate, NOT patched):

| File | Line | Symbol |
|------|------|--------|
| [MKSModule.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Converters/MKSModule.cs) | 13 | `public class MKSModule : PartModule` — the part-module that identifies a kolony anchor |

### 2.a New routine — `WarpIfNearForeignKolony`

Add a sibling routine in `WarpSystem.cs` modelled on `WarpIfSpectatingToController` (line 142). Registered in `OnEnabled` on the same 1000ms cadence:

```text
// In OnEnabled, after the existing spectator routine:
SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, WarpIfNearForeignKolony));
```

Routine body (pseudocode):

```text
private void WarpIfNearForeignKolony():
    // 1. Network state gate — same threshold as MKS-R0.
    if MainSystem.NetworkState < ClientState.LocksSynced: return

    // 2. Active-vessel gate.
    if HighLogic.LoadedScene != GameScenes.FLIGHT: return
    var active = FlightGlobals.ActiveVessel
    if active == null: return

    // 3. Spectator-routine takes priority — don't double-fire.
    if VesselCommon.IsSpectating: return

    // 4. Baseline-warp gate — don't snap mid-warp (the player is already
    //    actively manipulating time; another snap would compound jank).
    if TimeWarp.CurrentRate > 1.0f + 0.1f: return

    // 5. Find the most-advanced foreign-controlled MKS vessel in physics range.
    var target = FindMostAdvancedForeignKolonySubspace(
                     active,
                     FlightGlobals.VesselsLoaded,
                     SettingsSystem.CurrentSettings.PlayerName)
    if target.subspaceId <= 0: return
    if target.subspaceId == CurrentSubspace: return

    // 6. KSP-side safety gate — reuses WarpSystem.SafeToSync.
    if !SafeToSync(target.subspaceId): return

    // 7. Snap forward only (WarpIfSubspaceIsMoreAdvanced enforces).
    //    Wrap in pack/unpack for jank protection.
    SnapToForeignKolonySubspace(active, target.subspaceId, target.playerName)
```

### 2.b Pure helper — `FindMostAdvancedForeignKolonySubspace`

Extracted for testability per the LMP convention (BUG-051b / BUG-003-004 / R0 precedents). Lives on `WarpSystem` (or a new sibling helper class — TBD at implementation time; one helper that mirrors `ShouldSteadyStateRetry`'s shape).

**Signature (testable):**

```text
public static (int subspaceId, string playerName) FindMostAdvancedForeignKolonySubspace<TVessel>(
    Guid activeVesselId,
    IReadOnlyList<TVessel> loadedVessels,
    Func<TVessel, Guid> getId,
    Func<TVessel, bool> hasMksAnchor,
    string localPlayerName,
    Func<Guid, string> getUpdateLockOwner,
    Func<string, int> getPlayerSubspace,
    IReadOnlyDictionary<int, double> subspaceTimes)
```

Returns `(0, null)` if no eligible foreign-controlled kolony vessel exists. Otherwise returns the `(subspaceId, playerName)` pair for the foreign-controlled MKS vessel whose subspace has the highest `subspaceTimes` value.

**Predicate per loaded vessel:**

1. `v.id != activeVesselId` (not our own active vessel)
2. `hasMksAnchor(v)` (has at least one `MKSModule` part)
3. `getUpdateLockOwner(v.id)` is non-null, non-empty, and != `localPlayerName`
4. `getPlayerSubspace(ownerName) > 0` (owner is in a tracked subspace)
5. `subspaceTimes.ContainsKey(targetSubspace)` (we know that subspace's UT)

Among all qualifying vessels, pick the one whose subspace has the maximum UT. Ties broken by owner-name ordinal sort for determinism.

### 2.c Pre-snap pack + delayed unpack — `SnapToForeignKolonySubspace`

Mirrors the BUG-008-A pack-on-load + delayed-unpack pattern ([PqsAlignmentRoutine.cs:23](file:///F:/luna-multiplayer-mks/LmpClient/VesselUtilities/PqsAlignmentRoutine.cs#L23)). The active vessel goes on rails BEFORE the UT jump so KSP's physics integrator doesn't get a step-function input; the unpack happens after physics settles.

Pseudocode:

```text
private IEnumerator SnapToForeignKolonySubspace(Vessel active, int target, string ownerName):
    // 1. Operator notification BEFORE the jump.
    LunaScreenMsg.PostScreenMessage(
        $"[fix:MKS-R1] Syncing time with {ownerName}'s kolony...",
        3f, ScreenMessageStyle.UPPER_CENTER)
    LunaLog.Log($"[LMP]: [fix:MKS-R1] Snapping local subspace -> {target} (foreign kolony controller: {ownerName})")

    // 2. Pack the active vessel. Skipped if already packed (no-op).
    bool wasUnpacked = !active.packed
    if wasUnpacked:
        active.GoOnRails()

    // 3. Change subspace. This invokes the existing CurrentSubspace setter,
    //    which broadcasts ChangeSubspaceMsg and calls SetGameTime.
    WarpIfSubspaceIsMoreAdvanced(target)

    // 4. Yield one FixedUpdate so KSP applies the new UT and physics
    //    re-establishes against the new time base. Matches the
    //    PqsAlignmentRoutine pattern.
    yield return new WaitForFixedUpdate()

    // 5. Unpack if we packed it. Wrap in try/catch — KSP can refuse if the
    //    physics state is not yet stable; in that case leave packed and let
    //    the natural physics-range tick re-evaluate (same fallback as
    //    PqsAlignmentRoutine).
    if wasUnpacked && active.packed:
        try:
            active.GoOffRails()
        catch:
            LunaLog.LogWarning($"[LMP]: [fix:MKS-R1] Vessel refused unpack after subspace snap; KSP will retry on next physics tick.")

    LunaScreenMsg.PostScreenMessage(
        $"[fix:MKS-R1] Joined subspace {target}.",
        2f, ScreenMessageStyle.UPPER_CENTER)
```

**Why pack/unpack works:** packing a vessel disables its Unity rigidbody physics integration. `CurrentSubspace = x` triggers `ProcessNewSubspace` → `TimeSyncSystem.SetGameTime(newUt)` which advances `Planetarium.fetch.time`. With the vessel packed, KSP recomputes orbital state from the new UT (cheap analytic step) instead of integrating physics across the gap. After the WaitForFixedUpdate, KSP has re-established the vessel's on-rails state at the new time, and unpacking restores Unity physics from that clean state.

**Why ONE FixedUpdate is enough:** the BUG-008-A reference uses the same single-yield pattern. KSP's `Vessel.UpdateCaches` runs in `Vessel.FixedUpdate`, so one FixedUpdate after `GoOnRails` lets KSP re-sample everything for the new UT.

### 2.d Where to gate dual-mode silence (no gate needed)

Phase 2 is a NETWORK-MODE fix — it only does anything when LMP is connected to a server with at least one other player. The `MainSystem.NetworkState < ClientState.LocksSynced` early-return at step 1 of the routine handles single-player (no network) implicitly: `NetworkState` stays at `Disconnected` and the routine no-ops. No `PerAgencyCareer` flag interaction is needed because the fix doesn't touch per-agency state — it just nudges subspace selection.

**Per-agency interaction (defense-in-depth):** under `PerAgencyCareer=true`, foreign-agency kolony vessels are correctly identified by the Update-lock check (Stage 5.17a structurally prevents cross-agency Update-lock acquires, so "foreign Update-lock holder" ≡ "foreign agency member" for stamped vessels). For Unassigned-sentinel vessels (`OwningAgencyId = Guid.Empty`), the rule collapses to "whoever holds the Update lock right now" — also correct.

---

## 3. Jank-mitigation stack (defense in depth)

This is the operator-facing scope of Phase 2. Subspace snaps without these protections will produce visible physics jank for the snapping client — the actual reason this pre-spec exists.

### 3.a Stable-state gate — `SafeToSync`

[WarpSystem.cs:412-426](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs#L412-L426) is the canonical "is it safe to advance UT?" predicate. Returns false for:

- Atmospheric flight (`vessel.situation > FLYING`) — UT jumps mid-atmosphere reshape trajectory unpredictably.
- Orbits where the new UT would put periapsis below the body's atmospheric/terrain envelope — `CelestialUtilities.GetMinimumOrbitalDistance(body, 1f) < orbit.PeR`. Avoids "you got hours of warp; your orbit is now decaying" surprises.

Returns true (safe) for:

- Non-flight scenes (no vessel to jank).
- Landed / Splashed / Pre-launch.
- Atmospheric flight at `<= FLYING` (i.e., low atmosphere where minor UT advance is tolerable).
- Stable orbits with PeR above min-orbital-distance.

**Phase 2 reuses this gate verbatim.** No copy-paste, no parallel predicate.

### 3.b Baseline-warp gate

Don't fire when the player is actively time-warping (`TimeWarp.CurrentRate > 1.0f + 0.1f`). They're already manipulating time; another snap mid-warp would compound the perceptual jank. The 0.1 tolerance matches the existing `ShouldSteadyStateRetry` predicate's tolerance pattern.

### 3.c Pre-snap pack + delayed unpack

§2.c above. KSP's analytic on-rails state advances cheaply; physics integrator gets a clean restart at the new UT. Mirrors BUG-008-A's proven pack-on-load behaviour for the immortality-loss-during-load case.

### 3.d One-direction-only via `WarpIfSubspaceIsMoreAdvanced`

[WarpSystem.cs:272-282](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs#L272-L282) only fires when `Subspaces[newSubspace] > CurrentSubspaceTimeDifference`. **Snapping backward is structurally impossible.** No progress can be lost; no orbital state goes BACK in time. The case where you're ahead of the foreign-kolony controller is handled by them eventually advancing their warp — Phase 2 is asymmetric on purpose.

### 3.e Operator notification (`LunaScreenMsg`)

Two screen messages frame the snap so the player understands the UT jump is intentional + has someone to blame ("Bob's kolony"). Without this, the camera repositions, the clock jumps, and the player has no model for why.

Log-tagged with `[fix:MKS-R1]` for operator-grep consistency with R0.

### 3.f Cooldown (to prevent spam)

A foreign-kolony player who's slightly more advanced than us in subspace might cause us to snap, then they advance again, then we snap again, etc. To avoid pump-pump-pump jank, the routine should track the last-snap timestamp and refuse to fire again within a cooldown window (proposed: 10 seconds). On the same routine cadence (1000ms), this caps snaps at 6/minute even in the worst case. Implemented as a static field on `WarpSystem`, reset on `OnDisabled`.

### 3.g What we deliberately DO NOT do

- **No "snap backward" fallback for "we are ahead of foreign kolony."** Handled by their eventual warp; symmetric handoff would risk lost progress for one side every snap.
- **No "snap-with-delta-threshold-rejection."** Considered (refuse if UT delta > 1 hour) and rejected: any threshold leaves the failure mode open above it, and the pack/unpack already protects against the worst-case physics jank. If a player has been warping for hours alone and then enters someone else's kolony radius, the snap is appropriate — they wanted to catch up.
- **No "smooth lerp" between subspaces.** LMP's subspace model is discrete; partial-subspace state would require protocol design. Out of scope.

---

## 4. Per-agency dovetail — Phase 2 is gate-independent; Phase 3 supersedes it on the scenario-partition side

Under `PerAgencyCareer=false` (default, shared-scenario mode):
- Phase 2 is the load-bearing R1 mitigation. Time-base agreement keeps cross-vessel kolony aggregation coherent across the cohort while players are co-present.
- Phase 3 doesn't exist yet; without Phase 2, scenario sync drift is unchecked.

Under `PerAgencyCareer=true` + Career mode + Phase 3 landed (future):
- Each agency has its own `KolonizationInfo` partition. Cross-agency observation doesn't produce double-accrual at the scenario level.
- Phase 2 still provides defense-in-depth: any single agency with multiple players co-present at the same body still benefits from time-base agreement.
- Foreign-agency kolony proximity is correctly identified by the Update-lock check via Stage 5.17a structural enforcement.

Under Sandbox mode:
- `KolonizationScenario` doesn't run, so R1 doesn't manifest. Phase 2 still fires harmlessly (it doesn't read kolony state, just MKSModule presence). If a sandbox kolony part exists with `MKSModule`, the routine would still snap subspaces — defensible behaviour (visiting another player's kolony should still time-sync).

---

## 5. Out-of-scope items (explicitly NOT covered by Phase 2)

| Surface | Why not Phase 2 | Where it lives |
|---------|-----------------|----------------|
| Per-agency `KolonizationScenario` partition | R2 / Phase 3. Phase 2 alone cannot prevent shared-scenario merge drift; only proximity-time-agreement. | Phase 3 (`AgencyKolonyRouter`). |
| Server-authoritative kolony tick | Phase 5 optional. "Long-term server tick if needed" per handoff §R1 line 129. Not needed yet. | Phase 5. |
| Reverse handoff (snap backward when foreign player is behind) | Risks lost progress; relies on foreign player advancing. Not a regression — same shape as the existing spectator routine's one-direction snap. | Out-of-scope by design. |
| Subspace join on entering kolony radius via map view / SOI transition (not physics-load range) | Map-view kolony observation doesn't trigger `FixedUpdate` on the foreign vessel — no R1 drift. Only relevant for physics-loaded co-presence. | Out-of-scope. |
| MKS Module Manager patches that alter `MKSModule`'s namespace or class identity | Brittleness — if MKS renames `MKSModule`, our `FindPartModuleImplementing<MKSModule>` lookup breaks. Same brittleness class as R0's `TypeByName` lookup. | Documented in §6 below. |
| WOLF logistics network kolony presence | R4 / Phase 4. WOLF has its own scenario partition. | Phase 4. |

---

## 6. Brittleness — MKS module identity

Phase 2 reads the predicate "vessel has a kolony anchor" as `vessel.FindPartModuleImplementing<MKSModule>() != null`. MKS' `MKSModule` is the canonical kolony anchor; it's been at `KolonyTools.Converters.MKSModule` in this codebase form for years.

Like Phase 1.5 R0's `TypeByName` lookup against USITools, a future MKS refactor could:

- Rename `MKSModule` (already happened internally — there are legacy aliases like `ModuleColonyRewards`, `MKSAutoRepair`, `MksAutoRepair`; not the kolony anchor).
- Move it to a different namespace.
- Replace it with a new `IKolonyAnchor` interface and re-implement against that.

**Mitigation:** Phase 2 should use string-typed `AccessTools.TypeByName("KolonyTools.MKSModule")` lookup at module-resolution time, cache the resolved `Type`, and check `part.modules.OfType(resolvedType)` per-vessel. If the lookup fails at module-load time (MKS not installed or version mismatch), emit `[fix:MKS-R1]` warning at the same spot the R0 warning lives, and the routine no-ops thereafter. This makes Phase 2 self-disabling under MKS absence + version mismatch.

LmpClient does not import MKS as a compile-time dep (same constraint as R0), so the runtime-resolution is forced. Same shape as `HarmonyPatcher.PatchModuleLogisticsConsumer` from R0.

---

## 7. Test plan

### 7.a Unit tests in `LmpClientTest` (net472, MSTest)

Pure-helper coverage on `FindMostAdvancedForeignKolonySubspace<TVessel>`. Same in-memory test pattern as R0's `FilterToLocallyOwned<T>` — pass a lightweight test record + closures, avoid constructing real KSP `Vessel` instances.

**Test cases (new file `LmpClientTest/Phase2KolonySubspaceJoinDecisionTest.cs`):**

1. **`NoVesselsLoaded_ReturnsSkip`** — empty loaded list → `(0, null)`.
2. **`OnlyOwnActiveVessel_ReturnsSkip`** — only the active vessel itself is in the list → `(0, null)`.
3. **`SingleForeignVesselNoMks_ReturnsSkip`** — foreign vessel, but no `hasMksAnchor` → `(0, null)`. Pins the "kolony anchor required" rule.
4. **`SingleForeignMksLocallyOwned_ReturnsSkip`** — vessel has MKS, but Update lock is locally held → `(0, null)`. Pins the "foreign Update lock required" rule.
5. **`SingleForeignMksRemoteOwner_ReturnsSubspace`** — vessel has MKS, Update lock held by Bob, Bob's subspace is more advanced → `(bobSubspace, "Bob")`.
6. **`ForeignMksRemoteOwnerNoSubspace_ReturnsSkip`** — owner identified but `getPlayerSubspace` returns 0 → `(0, null)`. Pins the "subspace must be tracked" rule.
7. **`MultipleRemoteKolonies_PicksMostAdvanced`** — Alice in subspace 5 (UT=1000), Bob in subspace 3 (UT=500), Carol in subspace 7 (UT=2000). Returns `(carolSubspace, "Carol")`.
8. **`TieOnSubspaceTime_BreaksByOwnerName`** — two foreign vessels in subspaces with the same UT. Determinism: ordinal name sort. Useful so soak logs are reproducible.
9. **`MksVesselWithUnknownSubspaceMissingFromMap_ReturnsSkip`** — owner is named but their subspace isn't in `subspaceTimes` dict → `(0, null)`. Pins the lookup safety.
10. **`OneEligibleAmongDecoys_ReturnsTheEligible`** — mixed list: foreign-MKS-no-subspace + foreign-no-MKS + local-MKS + foreign-MKS-eligible. Returns the eligible one.
11. **`NullLoadedListGuard`** — null input list → `(0, null)`, no NRE.
12. **`NullActiveVesselId_ReturnsSkip`** — `Guid.Empty` as active id → degenerate but safe; foreign-MKS vessels still considered.

Brings LmpClientTest 108 → ~120.

### 7.b Routine-level integration coverage

The pure helper covers decision math. The routine itself (`WarpIfNearForeignKolony`) plus `SnapToForeignKolonySubspace` are KSP-bound and only validatable at the integration level (mock-client + KSP scene). Not covered by unit tests. Two-client soak per §7.c is the integration validation.

`MockClientTest` is not in scope here — the test would need to simulate KSP's FixedUpdate + vessel physics + scene + active vessel concept, which is too much harness work for one slice. Document this gap explicitly: routine-level behaviour is validated by smoke, not by automated test.

### 7.c Integration smoke (operator-driven, ~30 min)

**Single-client baseline (negative control):**
- Connect to local server, place an MKS base, no other clients.
- Expected: routine never fires (no foreign vessel). `KSP.log` shows zero `[fix:MKS-R1] Snapping...` lines.

**Two-client primary test:**
- Client A lands a vessel with `MKSModule` parts on Duna; warps to mid-afternoon (~6 hours into the day, ~22 megaseconds ahead of fresh-save).
- Client B launches a vessel from KSC and lands it near A's base (within physics-load range — 2.5km surface default).
- B's `WarpIfNearForeignKolony` routine fires within 1s of physics-load.
- **Acceptance:**
  - `KSP.log` on B shows: `[fix:MKS-R1] Snapping local subspace -> X (foreign kolony controller: A)`.
  - B's `LunaScreenMsg` shows: `[fix:MKS-R1] Syncing time with A's kolony...` then `[fix:MKS-R1] Joined subspace X`.
  - B's `Planetarium.GetUniversalTime()` matches A's within ~1s after the snap.
  - B's vessel does not lose orbit / fall through terrain / clip / NRE on the snap. Physics looks visibly clean (the pack/unpack protection works).
  - B's chat / lock UI continues to function (no system stalls from the UT jump).

**Lock-handoff sub-test:**
- During the above, A releases Update lock on her vessel (KSP map → switch to KSC). B's routine should stop firing (no foreign Update-lock holder).
- A acquires Update on her vessel again → routine fires again on next tick.
- **Acceptance:** no pump-pump-pump cycle (cooldown enforces 10s min between snaps).

**Solo-unaffected sub-test (handoff §Phase 2 acceptance):**
- Single client, solo-subspace warp far ahead. Routine never fires. Behavior identical to pre-Phase-2.

### 7.d Log signal to grep for

```bash
grep -E "\[fix:MKS-R1\]" KSP.log
```

Expected output post-fix in a two-client scenario:
- Module-load: zero or one `[fix:MKS-R1] MKSModule type resolved` line (graceful if MKS not installed).
- Runtime: one `[fix:MKS-R1] Snapping local subspace ->` line per snap event; one `Joined subspace` line after.
- Zero unexpected warnings (warnings indicate MKS-version mismatch or pack/unpack failure).

---

## 8. Acceptance criteria

Lifted from handoff §Phase 2 acceptance, refined here:

- [ ] All ~12 `LmpClientTest/Phase2KolonySubspaceJoinDecisionTest.cs` cases pass (LmpClientTest `108 → ~120`).
- [ ] `dotnet build` clean on `LmpClient` (no NEW warnings against the pre-existing 7).
- [ ] `dotnet build` clean on `Server` (no NEW warnings against pre-existing 29-30).
- [ ] Server boot banner shows `MKS-R1` in `[fork] ... fixes active: ...` (server-side advisory, same convention as R0).
- [ ] `/fork` JSON includes `"MKS-R1"` in `ActiveFixes`.
- [ ] KSP.log contains the module-resolution log line once at boot when LMP + MKS are both loaded.
- [ ] Two-client smoke: B's UT snaps to A's within ~1s of physics-load; no terrain clip, no orbital decay, no NRE during the snap.
- [ ] Two-client smoke: cooldown prevents > 6 snaps/minute in the worst case.
- [ ] Solo-client smoke: routine never fires.
- [ ] No regression on the existing `WarpIfSpectatingToController` path (the two routines are siblings, not collaborators — they have distinct preconditions).

---

## 9. Effort estimate (revised from handoff line 209)

Handoff says ~1 week. With this pre-spec in hand:

| Slice | Effort | Notes |
|-------|--------|-------|
| `WarpSystem` additions (routine + pure helper + pack/unpack coroutine + module-type-resolution cache + `ForkBuildInfo` entry) | ~3-4 hours | All in `WarpSystem.cs` + supporting files. One commit. |
| `LmpClientTest/Phase2KolonySubspaceJoinDecisionTest.cs` (~12 cases) | ~2-3 hours | Same test scaffold as R0's `MksR0DepotFilterTest`. |
| Parallel multi-lens review (general + consumer + upgrade) | ~30 min agent time + ~30 min applying findings | Same review discipline as R0. |
| Single-client smoke validation | ~15 min operator | Confirms zero false fires. |
| Two-client smoke validation | **OPERATOR BLOCKER** | Same KSPPATH2 / second-machine constraint as the rest of the MKS track. Bundle with the existing 3 backlogged smoke items (`[[project-mks-smoke-backlog]]`). |

**Total dev time: ~6-8 hours implementation + review.** Down from the handoff's 1-week estimate because the pre-spec closed the design questions.

The hard remaining gate is the **two-client smoke** — bundled with the backlogged items.

---

## 10. What this pre-spec deliberately does NOT design

- **Implementation code.** Per audit-via-pre-spec discipline; next session writes the actual `.cs` files against these anchors.
- **Phase 3 `AgencyKolonyRouter` design.** Different surface, different risk class, different commitment.
- **Reverse-direction snap (snap back when foreign is behind us).** Out of scope per §3.g.
- **WOLF kolony presence interaction (Phase 4).** Out of scope per §5.
- **`ForkBuildInfo` server-banner format change.** Just append `"MKS-R1"` to `ActiveFixes`; same one-line edit as R0.

---

## 11. Open questions (flag before implementation if any of these turn out wrong)

1. **Does `FlightGlobals.VesselsLoaded` actually populate before `WarpSystem.OnEnabled` registers the routine?** Verified: `VesselsLoaded` is populated whenever a vessel enters physics range; the routine fires every 1000ms after enable, so it self-corrects on the next tick if the list is empty at first fire. Not a real risk; documented for the implementer.
2. **Does `MKSModule` exist on EVERY kolony-relevant vessel, or only on some?** Need to verify at implementation: a "kolony" might also be defined by `ModuleColonyRewards` (rewards module) or other anchors. Best response: start with `MKSModule` (the most common), and if soak shows missed kolonies, extend to a union. **5 minutes of MKS-side grepping at implementation time.**
3. **Does the screen-message + 3-second display time interact badly with KSP's own "subspace change" messages?** LMP already emits subspace messages via `WarpEvents`; check there's no race or duplicate visual. **Verify against `WarpEvents` source at implementation.**
4. **Cooldown reset on `OnDisabled` — sufficient, or do we also need to reset on player-disconnect?** Probably sufficient because `OnDisabled` fires on disconnect. Verify.

---

## 12. Cross-references

- **Handoff doc** (`mks-lmp-compatibility-handoff.md` v3.3) — architectural framing (§R1 + §Phase 2).
- **Phase 1.5 R0 pre-spec** (`mks-lmp-compatibility-phase-1.5-prespec.md`, commit `890288c0`) — same audit-via-pre-spec template; structural-precedent shape.
- **Phase 1.5 R0 implementation** (commit `d1822cdd`) — three-reviewer-lens template (general + consumer + upgrade) to mirror here.
- **CLAUDE.md Stack Notes** — "Pure-helper extraction for client-internal decision math" (s9 / 2026-05-17) — same pattern applied here for `FindMostAdvancedForeignKolonySubspace<TVessel>`.
- **CLAUDE.md Stack Notes** — BUG-008-A pack-on-load + delayed-unpack pattern (`PqsAlignmentRoutine`) — direct precedent for §2.c jank protection.
- **`feedback_audit_via_prespec.md`** — pre-spec discipline; this doc is the Phase 2 instance.
- **`feedback_review_lens_framing.md`** — multi-lens review at implementation time. Consumer here = "the player flying near another player's MKS base who doesn't want to fall through terrain"; upgrade-lens = "the operator with an existing universe whose kolony state pre-dates Phase 2."
- **`project_mks_smoke_backlog.md`** — bundle two-client smoke for Phase 2 with the three existing backlogged items (KSPPATH2 constraint).
- **`WarpSystem.WarpIfSpectatingToController`** ([WarpSystem.cs:142](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Warp/WarpSystem.cs#L142)) — exact structural precedent for `WarpIfNearForeignKolony`.
- **`PqsAlignmentRoutine`** ([F:\luna-multiplayer-mks\LmpClient\VesselUtilities\PqsAlignmentRoutine.cs](file:///F:/luna-multiplayer-mks/LmpClient/VesselUtilities/PqsAlignmentRoutine.cs)) — pack/unpack pattern precedent.
