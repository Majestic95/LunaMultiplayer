# Phase 1.5 R0 — Depot-list authority fix — pre-spec

**Status:** PRE-IMPLEMENTATION. Drafted 2026-05-18 (session 22) after Phase 0 acceptance closed.

**Branch:** `feature/per-agency-mks` (worktree `F:\luna-multiplayer-mks`).

**Scope:** The handoff doc (`mks-lmp-compatibility-handoff.md` v3.3 §R0 + §Phase 1.5) describes the architectural fix; this doc pins concrete file:line anchors on both the USITools and LMP sides, the Harmony registration pattern, and the test plan. **No code changes in this commit** — the goal is to make the next implementation session a clean execute against verified anchors, mirroring the audit-via-pre-spec discipline that validated Phase 1 (`feedback_audit_via_prespec.md` in operator memory).

**Source pins (do not re-clone if still current):**

- **USITools** `4ad5cdd8` at `F:\tmp\mks-external\USITools\Source\USITools\Logistics\ModuleLogisticsConsumer.cs`
- **MKS** `ed0f6aa6` at `F:\tmp\mks-external\MKS\`
- **LMP** `feature/per-agency-mks` tip `95fc9206`

---

## 1. The bug R0 fixes — verified end-to-end trace

Already documented in handoff §R0 (lines 111-123); summarised here so reviewers don't have to context-switch.

**Setup:** Client A holds Update lock on vessel A (its own); client B holds Update on vessel B. Both vessels are landed in the same 150 m physics-load bubble. Vessel A has a `ModuleLogisticsConsumer` (i.e. any USI-consuming part — most converters / habitats); vessel B is a warehouse (`USI_ModuleResourceWarehouse` part).

**The loop:**

1. A's `ModuleLogisticsConsumer.FixedUpdate` ([ModuleLogisticsConsumer.cs:54-124](file:///F:/tmp/mks-external/USITools/Source/USITools/Logistics/ModuleLogisticsConsumer.cs#L54-L124)) fires every physics tick.
2. → `CheckLogistics` ([:128-232](file:///F:/tmp/mks-external/USITools/Source/USITools/Logistics/ModuleLogisticsConsumer.cs#L128-L232)) at line 134 calls `GetResourceStockpiles()` (and at :140 calls `GetPowerDistributors()` if a `ModulePowerCoupler` is present).
3. → `GetResourceStockpiles` ([:234-280](file:///F:/tmp/mks-external/USITools/Source/USITools/Logistics/ModuleLogisticsConsumer.cs#L234-L280)) walks `LogisticsTools.GetNearbyVessels(ScavangeRange, false, vessel, true)` and returns **every** vessel in range carrying an `USI_ModuleResourceWarehouse` part. Vessel B is in the list.
4. → `FetchResources` ([:378-455](file:///F:/tmp/mks-external/USITools/Source/USITools/Logistics/ModuleLogisticsConsumer.cs#L378-L455)) at `:416` writes `pr.amount -= demand` on **B's** `PartResource.amount`, **executed locally on A** (not on B).
5. B's `VesselResourceMessageSender` pulses every ~2.5 s and the next pulse arrives at A. A's `VesselResourceMessageHandler` (audit v3.2: lines 19-20) does NOT early-return because B is not A's controlled vessel — it applies the queue → A's local view of B reverts.
6. A's next `FixedUpdate` writes the deduction again → oscillation.
7. Symmetrically, `PushResources` ([:457-508](file:///F:/tmp/mks-external/USITools/Source/USITools/Logistics/ModuleLogisticsConsumer.cs#L457-L508)) at `:495` writes `res.amount += add` on output recipients, with identical asymmetry.

**Net effect:** B's warehouse balances oscillate by the consumer's per-tick demand/surplus; both clients see different mid-tick values; the 30 s scenario SHA pass compounds the drift; the player whose vessel was being passively drained / topped-up has no agency in their own economy.

**Why a prefix on `FixedUpdate` (the obvious-looking fix) does NOT work:** even gated on A holding A's Update lock, the consumer's own `FixedUpdate` still fires (A owns A) and still ends up writing to B's parts. The mutation site is downstream of the lock check; the only safe gate is to remove B from the *list* the mutator iterates.

---

## 2. The correct fix — postfix-filter the list

**Anchor methods (both `public` but NOT `virtual` — brittleness flagged in §6):**

| File | Line | Signature |
|------|------|-----------|
| [ModuleLogisticsConsumer.cs](file:///F:/tmp/mks-external/USITools/Source/USITools/Logistics/ModuleLogisticsConsumer.cs) | 234 | `public List<Vessel> GetResourceStockpiles()` |
| [ModuleLogisticsConsumer.cs](file:///F:/tmp/mks-external/USITools/Source/USITools/Logistics/ModuleLogisticsConsumer.cs) | 283 | `public List<Vessel> GetPowerDistributors()` |

**Filter logic (same for both methods):**

After USI computes the unfiltered candidate list, postfix mutates `__result` in place by removing every entry where the local player does not hold the Update lock for that vessel.

```text
postfix(ref List<Vessel> __result):
    if MainSystem.NetworkState < ClientState.Connected: return        // single-player / disconnected: no-op
    if __result == null or __result.Count == 0:           return      // nothing to filter
    local := SettingsSystem.CurrentSettings.PlayerName
    __result.RemoveAll(v => v == null
                         || v.id == Guid.Empty
                         || !LockSystem.LockQuery.UpdateLockBelongsToPlayer(v.id, local))
```

**Three load-bearing rules embedded in `RemoveAll` predicate:**

1. **`v.id == Guid.Empty` is excluded.** A vessel without a valid id can't be looked up in `LockStore.UpdateLocks` — `UpdateLockBelongsToPlayer` would return false, which is the right answer, but the explicit guard makes the intent legible.
2. **"No lock owner" is NOT permission.** `UpdateLockBelongsToPlayer` returns false when the lock doesn't exist (LockQueryUpdate.cs:23 → LockQuery.cs:34-37). This matches the handoff rule "Never treat 'no lock owner' as go-ahead in MP (transient after unload — audit finding)" — if a vessel is unlocked, *some* player will hold its Update lock on the next tick (LMP issues Update locks aggressively); the gap is microseconds; pump-on-unowned is the wrong default.
3. **`MainSystem.NetworkState < ClientState.Connected` short-circuits.** When LMP is loaded but no server is connected (KSP launched standalone with LMP installed), the original MKS behavior must be preserved unchanged. Same gate the existing `ContractPreLoader_Filter.Prefix` uses (LmpClient/Harmony/ContractPreLoader_Filter.cs:84).

**What about the consumer's own vessel?** `CheckLogistics` iterates the returned list and mutates parts on those vessels' `PartResource.amount`. The consumer's own vessel is *also* in `__result` (USI's intent is for the consumer to scavenge from itself first when its own warehouses qualify), and the local player by definition holds Update on their own vessel, so it survives the filter. No special case needed.

**What if the local player has no name (pre-connect)?** `SettingsSystem.CurrentSettings.PlayerName` is initialised at LMP startup and is non-empty before any vessel reaches FixedUpdate post-connect. The `NetworkState < Connected` short-circuit covers the pre-connect window.

---

## 3. Patch registration — string-typed Harmony, mirror `ContractPreLoader`

The patch lives in a new file in `LmpClient/Harmony/` and is registered imperatively, because (a) `USITools.ModuleLogisticsConsumer` is not a compile-time dependency of LmpClient — string-typed lookup via `HarmonyLib.AccessTools.TypeByName` is the only option, and (b) the patch must be a no-op when USITools is not installed (vanilla LMP / non-MKS configs).

### 3.a New file: `LmpClient/Harmony/ModuleLogisticsConsumer_DepotListPostfix.cs`

**Naming:** follows the existing convention `<TargetClass>_<TargetMethod>.cs` but compresses to `_DepotListPostfix.cs` because **one file holds postfixes for both `GetResourceStockpiles` and `GetPowerDistributors`** (identical filter, no point splitting). The file name advertises the *purpose* per [feedback_user_facing_naming.md](file:///C:/Users/austi/.claude/projects/f--luna-multiplayer/memory/feedback_user_facing_naming.md) — "depot list" matches the operator-facing language the handoff doc and CLAUDE.md use for the R0 family.

**Structure (mirrors `ContractPreLoader_Filter.cs`):**

- `public static class ModuleLogisticsConsumer_DepotListPostfix`
- Two `internal static` postfix methods (one per anchor) — both take `ref List<Vessel> __result` and call a shared `FilterToLocallyOwned(...)` private static helper that holds the actual filter logic. This split makes the filter a **pure testable helper** in line with the LMP "extract to pure helper for testability" pattern from CLAUDE.md Stack Notes (precedents: `VesselPositionUpdate.ComputeMaxInterpolationDuration` for BUG-003/004; `WarpSystem.ShouldSteadyStateRetry` for BUG-051b; `PqsAlignmentRoutine.NeedsRealignment` for BUG-008 Phase A).
- Filter helper signature: `internal static void FilterToLocallyOwned(List<Vessel> result, string localPlayerName, Func<Guid, bool> isUpdateLockHeldByLocal)` — takes `Func` for the lock check so LmpClientTest can pass a stub without dragging in the full LockSystem singleton.
- The two postfix methods just compose: `(__result == null) ? return : FilterToLocallyOwned(__result, SettingsSystem..PlayerName, vid => LockSystem.LockQuery.UpdateLockBelongsToPlayer(vid, SettingsSystem..PlayerName));`
- Tagged log line on first patch-apply: `[fix:MKS-R0]` + count of vessels seen / filtered — wires into the `ForkBuildInfo` `[fix:...]` operator-grep convention.

### 3.b Registration: append to `LmpClient/Base/HarmonyPatcher.cs:PatchOptionalMods`

Add **one** call alongside `SuppressClickThroughBlockerPopup()` and `PatchContractPreLoader()` (`HarmonyPatcher.cs:19-23`):

```text
private static void PatchOptionalMods()
{
    SuppressClickThroughBlockerPopup();
    PatchContractPreLoader();
    PatchModuleLogisticsConsumer();     // <-- NEW
}

internal static void PatchModuleLogisticsConsumer()
{
    try
    {
        var mlcType = HarmonyLib.AccessTools.TypeByName("USITools.ModuleLogisticsConsumer");
        if (mlcType == null)
        {
            LunaLog.Log("[LMP]: USITools.ModuleLogisticsConsumer type not found — USITools not installed, skipping R0 depot-list filter.");
            return;
        }

        var stockpilesMethod = HarmonyLib.AccessTools.Method(mlcType, "GetResourceStockpiles");
        var powerMethod      = HarmonyLib.AccessTools.Method(mlcType, "GetPowerDistributors");
        if (stockpilesMethod == null || powerMethod == null)
        {
            LunaLog.LogWarning("[LMP]: USITools.ModuleLogisticsConsumer methods not found — USITools version mismatch?");
            return;
        }

        var stockpilesPostfix = new HarmonyLib.HarmonyMethod(typeof(LmpClient.Harmony.ModuleLogisticsConsumer_DepotListPostfix), "PostfixStockpiles");
        var powerPostfix      = new HarmonyLib.HarmonyMethod(typeof(LmpClient.Harmony.ModuleLogisticsConsumer_DepotListPostfix), "PostfixPower");
        HarmonyInstance.Patch(stockpilesMethod, postfix: stockpilesPostfix);
        HarmonyInstance.Patch(powerMethod,      postfix: powerPostfix);
        LunaLog.Log("[LMP]: [fix:MKS-R0] Patched USITools.ModuleLogisticsConsumer.GetResourceStockpiles + GetPowerDistributors — depot/power lists filtered to local Update-lock holder.");
    }
    catch (Exception e)
    {
        LunaLog.LogWarning($"[LMP]: [fix:MKS-R0] Could not patch USITools.ModuleLogisticsConsumer: {e.Message}");
    }
}
```

**Why structurally identical to `PatchContractPreLoader`:**

- Same `TypeByName` graceful no-op when target absent.
- Same `Method(type, name)` lookup with mismatch warning.
- Same `HarmonyInstance.Patch(method, postfix: ...)` imperative registration.
- Same try/catch outer guard so the rest of `PatchOptionalMods` continues if this one fails.
- Same `[LMP]:` log prefix convention; added `[fix:MKS-R0]` tag so the operator-facing `grep -F "[fix:"` workflow picks it up.

### 3.c Append `MKS-R0` to `ForkBuildInfo.ActiveFixes`

`Server/ForkBuildInfo.cs` carries the operator-facing fixes list ("fixes active: ..." banner + `/fork` JSON endpoint). The current MKS branch shows the per-agency-career banner but no MKS-related fix entries. Append `"MKS-R0"` so:

- The server boot banner advertises that this server expects clients running the R0 patch.
- The web dashboard `/fork` JSON includes it.
- Future MKS fixes (R1/R2/R3) follow the same naming: `MKS-R1`, `MKS-R2`, ...

This is one line in `Server/ForkBuildInfo.cs` (precedent: every other entry in `ActiveFixes`).

---

## 4. Per-agency dovetail — handled by Stage 5.17a structurally; no extra gate

Under `PerAgencyCareer=true` + Career mode (Stage 5.17e-1 combined gate):

- `vessel.OwningAgencyId` is stamped on every relayed proto by `VesselDataUpdater.RawConfigNodeInsertOrUpdate` (Stage 5.16b).
- `LockSystem.AcquireLock` rejects cross-agency vessel-scoped Update lock acquires (Stage 5.17a) — non-agency-members cannot hold Update on this vessel.
- The R0 postfix filters on Update-lock-belongs-to-local — which is **identical** to "OwningAgencyId belongs to my agency" under those gates.

Under `PerAgencyCareer=false` (shared-agency mode, default):

- Any player can hold Update on any vessel via existing lock-handoff.
- The R0 postfix correctly filters to the Update-lock holder (which is whoever last acquired it).

Under Sandbox mode (regardless of `PerAgencyCareer` flag):

- Stage 5.17e-1 dual-mode-off gate: per-agency wire surface inert, no `OwningAgencyId` stamping.
- R0 postfix still works — it only depends on Update-lock state, which is gate-independent.

**No additional per-agency-specific filter** is needed in R0. The handoff doc v3.3 §R0 (lines 123) calls this out; the audit confirms it: `UpdateLockBelongsToPlayer` is the right primitive across all three modes. Defense-in-depth additions (e.g. also-check-`OwningAgencyId`) are technically possible but add no security under any mode the Stage 5.17a guard structurally enforces — leave them out per CLAUDE.md "Don't add error handling, fallbacks, or validation for scenarios that can't happen" coding principle.

---

## 5. Out-of-scope items (explicitly NOT covered by R0 fix)

| Surface | Why not R0 | Where it lives |
|---------|-----------|----------------|
| Orbital `ModuleLogisticsConsumer` | `CheckLogistics` at MLC:131 early-exits on `!vessel.LandedOrSplashed` — orbital consumers never reach `GetResourceStockpiles`/`GetPowerDistributors` call sites. **Verified during pre-spec:** both methods are called from exactly one site each in MKS + USITools combined (`CheckLogistics:134` and `:140`); no external callers. The `LandedOrSplashed` early-exit is the full guard. Document in a test case rather than gating in code. | Out-of-scope. |
| `ScenarioOrbitalLogistics.Update` → `transfer.Deliver` | Resource-mutation surface independent of `ModuleLogisticsConsumer`. Audit v3.2 in handoff §R2 lines 135-141 — this is R2's territory + Phase 3 `AgencyOrbitalRouter`. | R2 / Phase 3. |
| `KolonizationScenario` accrual | Time-based; sensitive to subspace UT drift not depot list. | R1 / Phase 2. |
| WOLF `CrewRoute` / `Depot` / `Hopper` etc. | Per-agency scenario partition + concurrent-mutation problem under shared-scenario SHA. | R4 / Phase 4. |
| Other USI Harmony hot paths (`ModuleResourceConverter.OnFixedUpdate` etc.) | Not the R0 mechanism — those are own-vessel mutations whose existing field-sync via PartSync XMLs is sufficient. | Out-of-scope for this fix. |

---

## 6. Brittleness — upstream PR commitment

Handoff §11 item 8 (line 291) and §R0 (line 119) both flag this: `ModuleLogisticsConsumer.GetResourceStockpiles` and `GetPowerDistributors` are **`public` but NOT `virtual`** (verified at `:234` and `:283`). The R0 postfix anchors directly on these method bodies. A USITools refactor that:

- Renames either method, or
- Inlines the call site into `CheckLogistics`, or
- Replaces the `List<Vessel>` return with a different shape (e.g. `IEnumerable<Vessel>` — Harmony postfix on `IEnumerable` is harder to filter), or
- Adds a new `IResourceProvider` interface as the seam and re-implements `CheckLogistics` against that

... silently breaks the fix. The postfix would still register (the method still exists), but the filter wouldn't run at the right point in the call graph, and the bug would return without any log signal.

**Mitigation:**

1. **Land the local fix** (this pre-spec) for the current USITools `4ad5cdd8` pin.
2. **Open an upstream USITools PR** that promotes both methods to `public virtual` (or `protected virtual` + a `public` non-virtual delegate), so a future Harmony override can safely override the method bodies without re-anchoring on the legacy seam. The PR is small (one-keyword change × 2) but socially significant — confirms USITools authors accept multiplayer compatibility as a soft requirement.
3. **Re-pin the audit SHA** if the upstream PR lands and we adopt a newer USITools version, and re-verify the postfix anchors compile against the new shape.

The upstream PR is documented as a long-term track in handoff §11 item 8. It is *not* a Phase 1.5 R0 implementation blocker — the local postfix works today regardless.

---

## 7. Test plan

### 7.a Unit tests in `LmpClientTest` (net472, MSTest)

Mirrors the LMP "pure helper for testability" pattern. `FilterToLocallyOwned(List<Vessel>, string, Func<Guid, bool>)` is testable without instantiating `LockSystem` or `SettingsSystem`.

**Wrinkle:** `Vessel` is a KSP type. LmpClientTest already has access to KSP-types via the same `External/KSPLibraries/` reference path (it's the test counterpart to LmpClient), so this isn't a new dep. But constructing real `Vessel` instances in a test is heavy. Two options:

- **A. Test against a lighter shape.** Refactor `FilterToLocallyOwned` to take `List<T>` + `Func<T, Guid> getId` and do the filter on the id projection. Then the unit test can pass `List<Guid>`-like or a custom test record. This is cleaner but the production call site has to project `v => v.id` at the call.
- **B. Test by constructing `Vessel` via reflection.** Possible but ugly.

**Recommended: option A.** Filter signature becomes:

```text
internal static void FilterToLocallyOwned<T>(List<T> result, string localPlayerName, Func<T, Guid> getId, Func<Guid, bool> isUpdateLockHeldByLocal)
```

Production call site: `FilterToLocallyOwned(__result, local, v => v?.id ?? Guid.Empty, vid => LockSystem.LockQuery.UpdateLockBelongsToPlayer(vid, local))`. Test call site: `FilterToLocallyOwned(list, "Alice", id => id, vid => alicesVessels.Contains(vid))`.

**Test cases (new file `LmpClientTest/MksR0DepotFilterTest.cs`):**

1. **`Empty_list_returns_empty`** — null and zero-count both safe, no NRE.
2. **`Single_local_vessel_preserved`** — one vessel, local holds its lock → list unchanged.
3. **`Single_remote_vessel_removed`** — one vessel, remote holds its lock → list empty.
4. **`Mixed_list_only_local_survives`** — three vessels, local holds one → list contains exactly that one.
5. **`Unowned_vessel_removed`** — vessel exists, no lock holder (`Func` returns false for that id) → removed. Pins the "no owner ≠ permission" rule.
6. **`GuidEmpty_vessel_removed`** — vessel with `Guid.Empty` id → removed, no exception thrown.
7. **`Null_vessel_in_list_removed`** — defensive against MKS returning null entries → removed, no NRE.
8. **`All_local_preserved`** — every vessel is local → list unchanged.
9. **`All_remote_removed`** — every vessel is remote → list empty.
10. **`Lock_check_func_called_once_per_vessel`** — invariant: filter doesn't re-call the predicate; pins perf expectation for hot path (FixedUpdate fires 50× / sec).

LmpClientTest count growth: `91 → ~101` (+10). Brings the suite to ~101 tests on `net472` — same growth shape as Stage 5.18c.

### 7.b Integration smoke (operator-driven, ~30 min)

**Single-client baseline (negative control):**
- Connect to local server, place a USI converter + warehouse on the SAME vessel; toggle converter.
- Expected: zero behavior change vs. vanilla MKS — converter consumes from warehouse normally (warehouse is own-vessel; passes filter trivially).
- `KSP.log` should show `[LMP]: [fix:MKS-R0] Patched ...` once at boot, and zero filter-related warnings during converter operation.

**Two-client primary test (handoff line 205 acceptance):**
- Client A: lands a craft with a `ModuleLogisticsConsumer` (any USI consumer).
- Client B: lands a warehouse vessel (with `USI_ModuleResourceWarehouse` parts) within 150 m of A.
- Both clients confirm both vessels are physics-loaded.
- A keeps Update on A, B keeps Update on B (no manual lock handoff).
- Toggle A's consumer ON; observe both clients' warehouse resource values for 60 s.
- **Acceptance:** B's warehouse `MaterialKits` (or whatever resource) stays stable on both clients. No oscillation, no `[ERR] FetchResources` in either KSP.log.

**Lock-handoff sub-test (handoff §Phase 1.5 line 203 acceptance):**
- During the 60 s of the primary test, A acquires Update on B (KSP map view → "Switch To" → B → "Fly").
- Filter behavior should immediately change: A's consumer now CAN pull from B (A holds Update on both). Warehouse on B starts draining as the consumer fills.
- A releases Update on B (returns to flight on A). Filter excludes B again. Warehouse on B stops changing.
- **Acceptance:** no double-spend during the handoff transition (B's amount accounts for exactly what A pulled, no negative balance, no resource creation).

**Soak coupling:** bundle these with the deferred Phase 1 converter-toggle field-propagation test from Phase 0. One two-client session validates both Phase 1 acceptance and Phase 1.5 R0 acceptance.

### 7.c Log signal to grep for in `KSP.log`

Operator acceptance grep (one-liner):

```text
grep -E "\[fix:MKS-R0\]|FetchResources|StoreResources" KSP.log
```

Expected output post-fix:
- Exactly one `[fix:MKS-R0] Patched ...` line at boot.
- Zero `FetchResources` ERR / EXC lines.
- Zero unexpected filter warnings.

---

## 8. Acceptance criteria

Lifted from handoff §Phase 1.5 line 205, refined here:

- [ ] All 10 `LmpClientTest/MksR0DepotFilterTest.cs` cases pass (LmpClientTest `91 → ~101`).
- [ ] `dotnet build` clean on `LmpClient` (no NEW warnings against the pre-existing 7).
- [ ] `dotnet build` clean on `Server` (no NEW warnings against pre-existing 30).
- [ ] Server boot banner shows `MKS-R0` in `[fork] ... fixes active: ...`.
- [ ] `/fork` JSON includes `"MKS-R0"` in `ActiveFixes`.
- [ ] KSP.log contains exactly one `[LMP]: [fix:MKS-R0] Patched USITools.ModuleLogisticsConsumer.GetResourceStockpiles + GetPowerDistributors — ...` line at boot when LMP + MKS are both loaded.
- [ ] Two-client smoke: B's warehouse resources do not oscillate while A's consumer is active (60 s observation window).
- [ ] Two-client smoke: lock-handoff during pump does not produce negative or duplicated resource amounts.
- [ ] No regression in single-player KSP-with-LMP-installed (handoff §3.a gate: short-circuit when `NetworkState < Connected`).
- [ ] No regression in vanilla LMP without MKS (no USITools type found → graceful no-op log line).

---

## 9. Effort estimate (revised from handoff line 201)

Handoff says ~1.5 weeks. With this pre-spec in hand, implementation cost narrows:

| Slice | Effort | Notes |
|-------|--------|-------|
| New `ModuleLogisticsConsumer_DepotListPostfix.cs` + `PatchModuleLogisticsConsumer()` in `HarmonyPatcher.cs` + `ForkBuildInfo` entry | ~2 hours | One commit, one independent-review-agent pass per [feedback_independent_review.md](file:///C:/Users/austi/.claude/projects/f--luna-multiplayer/memory/feedback_independent_review.md). |
| `LmpClientTest/MksR0DepotFilterTest.cs` (10 cases) | ~2 hours | Same commit if review concurs, or split if independent reviewer prefers. |
| Single-client smoke validation | ~15 min | Operator-side; already feasible against the existing F:/luna-multiplayer-server-runtime/ setup. |
| Two-client smoke validation | **OPERATOR BLOCKER** | Same dependency as Phase 0 deferred item — needs either a second KSP install (`KSPPATH2` route) or a second machine with another player. |
| Upstream `virtual`/`protected` PR to USITools | Independent, parallel | Out-of-band — open whenever; not blocking on local fix. |

**Total dev time: ~4 hours implementation + review.** The 1.5-week handoff estimate budgeted for the audit + design phase (this doc); with that closed, the implementation is one focused session.

The hard remaining gate is the **two-client smoke** (same `KSPPATH2`-or-second-machine constraint as Phase 0). Plan to bundle both validations into one operator session.

---

## 10. What this pre-spec deliberately does NOT design

- **Phase 1.5 R0 implementation code.** Per-spec is structural; the next session writes the actual `.cs` files against these anchors.
- **Phase 1 converter-toggle deferred test.** That's a separate validation track (Phase 0 acceptance carry-over).
- **Phase 1.5 R1 / R2 / R3 fixes.** Those are different hot paths and want their own pre-specs when picked up.
- **CRP localization check (handoff line 186).** Optional Phase 0 check that hasn't been done; not material to R0.
- **Upstream USITools `virtual` PR.** Mentioned for completeness (§6); not on the implementation critical path.

---

## 11. Open questions (flag before implementation if any of these turn out wrong)

1. ~~**Is `ModuleLogisticsConsumer.GetResourceStockpiles` ever called from outside `CheckLogistics`?**~~ **CLOSED during pre-spec.** Grep over MKS `ed0f6aa6` + USITools `4ad5cdd8`: exactly one call site per method (`ModuleLogisticsConsumer.cs:134` for `GetResourceStockpiles`, `:140` for `GetPowerDistributors`); no external callers. The `!LandedOrSplashed` early-exit at MLC:131 is the full guard for the orbital case.

2. **Are there other LMP-side mutators that write to `pr.amount` on remote vessels?** A targeted grep over `LmpClient/` for `\.amount\s*=` (excluding own-vessel writes) would identify any LMP-side hot paths that also need the same kind of authority filter. R0 only fixes the *MKS-originated* mutation; if LMP itself has the same bug elsewhere, that's a separate fix family. — **5 minutes of grepping at implementation time.**

3. **Does `vessel.id` match the Guid that LMP stores in `LockStore.UpdateLocks`?** Verified true in existing LMP code (every `LockSystem.AcquireLock` call uses `vessel.id`), but a one-test sanity-check ahead of implementation prevents an embarrassing miss. — **Covered by unit test 2/3/4 above (lock-held / not-held cases).**

---

## 12. Cross-references

- **Handoff doc** (`mks-lmp-compatibility-handoff.md` v3.3) — architectural framing (§R0 + §Phase 1.5 + §11 item 8).
- **Phase 0 acceptance** — closed s22 ([project_mks_compat_branch.md](file:///C:/Users/austi/.claude/projects/f--luna-multiplayer/memory/project_mks_compat_branch.md) memory).
- **CLAUDE.md Stack Notes** — "Pure-helper extraction for client-internal decision math" pattern (LMP s9 / 2026-05-17 entry) — same pattern applied here for `FilterToLocallyOwned<T>`.
- **CLAUDE.md Stack Notes** — "Server-relay path needs an explicit cross-agency write-side guard" (s19 entry) — adjacent precedent for "authority filter on a list before the mutator iterates it".
- **`feedback_audit_via_prespec.md`** — pre-spec discipline; this doc is the Phase 1.5 R0 instance.
- **`feedback_review_lens_framing.md`** — when reviewing the implementation commit, spawn parallel consumer-lens + upgrade-lens reviews. Consumer here = "the user who toggles a converter and expects MKS to behave correctly in MP"; upgrade-lens = "the user upgrading a single-player MKS save to multiplayer who expects existing colonies not to break".
- **ContractPreLoader_Filter** ([LmpClient/Harmony/ContractPreLoader_Filter.cs](file:///F:/luna-multiplayer-mks/LmpClient/Harmony/ContractPreLoader_Filter.cs)) — exact structural precedent for third-party-mod Harmony patches.
