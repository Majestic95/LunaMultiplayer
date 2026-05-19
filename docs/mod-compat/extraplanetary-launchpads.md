# Extraplanetary Launchpads (EPL) — compat layer analysis

**Identification.** [Extraplanetary Launchpads](https://github.com/taniwha/Extraplanetary-Launchpads) (taniwha/Extraplanetary-Launchpads). Mod that lets players construct new vessels at a launchpad off the launch site — orbital construction, surface bases, recycler-equipped pads that dismantle vessels back into resources. Career-mode-relevant because builds consume raw resources (`RocketParts`, `Metal`, etc.) and produce launchable vessels.

---

## Luna Compat status

| In Luna Compat Harmony list? | In Part Sync list? | Server plugin flag? |
|------------------------------|---------------------|---------------------|
| **Yes** (per [lunacompat-inventory.md](lunacompat-inventory.md) — "Static random seed (recycling)") | Yes — EPL parts ride standard part-sync | No |

The existing LunaCompat handling pins the `UnityEngine.Random.Range` calls inside the recycler so all clients observe the same dismantling sequence. **Sufficient under per-agency** with no extension owed for that concern (see §11 below).

---

## State ownership

| Where state lives | Shape |
|-------------------|-------|
| `Source/Settings.cs:37` (`ELSettings : ScenarioModule`) | **Client preferences only**. `OnSave` (line 212) writes to a local file at `<DataPath>/Settings.cfg`, NOT into the save game / scenario blob. Fields: `KAS_Present`, `use_KAC`, `PreferBlizzy`, hull-rendering / window-position toggles. |
| `Source/BuildControl.cs` | Build progress + cost serialise onto the **pad vessel's** ConfigNode (`BuildControl.cs:861-863`). Workshop productivity read from `builder.vessel.FindVesselModuleImplementing<ELVesselWorkNet>()` at line 986 — kerbal labor accrues against the pad vessel. |
| `Source/Recycler/StateMachine.cs:257` | Recovered resources deposit into the recycler-pad-vessel's own inventory: `recycler_resources = new RMResourceSet(recycler.vessel, recycle_parts)`. |
| `Source/Survey/SurveyStake.cs:26` | Survey stakes are `ELSurveyStake : PartModule` on one-part vessels — normal KSP vessels with PartModule state. |
| `Source/Survey/SurveyTracker.cs:35` | `ELSurveyTracker.sites` (singleton dict) is a **view** over `FlightGlobals.Vessels` enumeration, rebuilt at scene load. Not authoritative state. |
| `Source/Recipes/` (folder) | Build recipe **definitions** (X kg `RocketParts` produces Y mass) loaded from `GameDatabase`. World-immutable defs, identical across all clients via mandatory-mods sync. |

**No career singletons touched.** Zero references to `Funding.Instance.Funds` and zero references to `ResearchAndDevelopment.Instance.Science` anywhere in EPL source. Builds cost raw vessel-local resources only.

**Tech tree:** `UI/PartSelector.cs:251` reads `ResearchAndDevelopment.PartTechAvailable(ap)` to filter the part-picker UI to researched parts. This is a READ of the active player's `ResearchAndDevelopment.Instance` — already per-agency-partitioned by Stage 5.17e routers on each client mirror.

---

## This fork touchpoints

- **Scenario:** none. EPL's only `ScenarioModule` writes external preferences, not in-save state.
- **Vessel:** all build / recycle / workshop state attached to the pad/recycler vessel. `lmpOwningAgency` (Stage 5.16b) already stamps these.
- **Career singletons:** none. No band-1 router interaction owed.
- **Custom relay:** not used. EPL relies on stock KSP vessel persistence + scene-load enumeration for state recovery.

---

## Interaction with PerAgencyCareer

**Substantially safe in stock form.** Every load-bearing piece of state is vessel-attached, so the existing `lmpOwningAgency` partition covers it automatically. No new `AgencyState` field, no new router, no projector entry, no Harmony patch in this fork.

**But.** Under 5.17a write-rejection (soak Finding 2 helper), cross-agency use of EPL equipment creates an **exploit vector**:

1. Agency A's pilot lands on Agency B's launchpad and clicks "Build" in the EPL workshop UI.
2. `ELBuildControl.BuildCraft` ([BuildControl.cs:833](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/BuildControl.cs)) flips `state = State.Building` and the build begins on the local client.
3. Across many physics ticks, `DoWork_Build` debits B's pad resources via `padResources.TransferResource(res.name, -amount)` at [BuildControl.cs:337](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/BuildControl.cs). Each tick's resource write is rejected server-side by 5.17a (cross-agency write to B's pad), but the LOCAL `padResources` view drains anyway.
4. On completion, `BuildAndLaunchCraft` ([BuildControl.cs:684](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/BuildControl.cs)) instantiates the new vessel via `ShipConstruction.AssembleForLaunch`. The new-vessel proto **is** accepted by the server (fresh vessel; first-stamps with Agency A).

**Net result:** Agency A spawns a free vessel; Agency B's pad resources are unchanged on the server (so B can keep building too). Worse: even a repeatedly-aborted build attempt drains B's local-view pad resources during the Building ticks, producing a **silent griefing vector** distinct from the new-vessel exploit. Identical shape for recycler use (Agency A's craft enters B's recycler trigger field, `OnTriggerStay` at [Recycler.cs:99](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/Recycler/Recycler.cs) calls `CanRecycle` → `RecycleField.enabled = false` + `sm.CollectParts(p)` — local part destruction proceeds while resource-recovery relay rejected on B's side).

---

## Failure modes (multiplayer)

1. **Cross-agency pad build exploit (above).** Free vessel + no resource cost. Per-agency-specific — does not exist under shared-agency mode because the relay isn't rejected there.
2. **Cross-agency recycler exploit.** Symmetric to #1 — local part destruction succeeds; recovered resource deposit relay rejected.
3. **Survey stake visibility.** Stakes are normal vessels, so all agencies see all stakes in the tracking station under stock LMP behavior. Per-agency intent (each agency sees only their own stakes) would be an opt-in **asymmetric-visual** filter — not a state leak.
4. **Recycler RNG determinism.** Already handled by existing LunaCompat seed pin at [Recycler/StateMachine.cs:387,392](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/Recycler/StateMachine.cs). Slightly over-defensive under per-agency (only the recycler's owning agency's writes land anyway), but harmless.

---

## Proposed layering

| Layer | Owner repo | Deliverable |
|-------|-------------|--------------|
| Core LMP fork | **No work owed** | EPL state is already vessel-partitioned by `lmpOwningAgency`. The 5.17a write-rejection that surfaces the exploit (above) is correct server-side behavior. |
| Luna Compat (Harmony) | ✅ **S7 shipped** (`Majestic95/LunaCompat@1dc3196`, branch `feature/per-agency-mod-compat`) | Two Harmony hooks aborting cross-agency build / recycle operations with operator-visible feedback. Implementation extends the existing `Mods/ExtraplanetaryLaunchpads/ExtraplanetaryLaunchpadsIntegration.cs` (recycler-RNG seed-pin code unchanged) with a sibling `EplPerAgencyPreflight` static helper. |
| Luna Compat server plugin | No | No deterministic server authority owed for EPL — no RNG drive beyond what the existing client-side seed pin covers. |
| Operational | Closed-by-S7 | The interim "only build / recycle on YOUR agency's equipment" operator rule is now enforced client-side. |

### S7 hook targets (corrected during multi-lens review 2026-05-19)

The original audit cited `ELBuildControl.BuildAndLaunchCraft` (build prefix) and `RecyclerFSM.CollectParts` (recycler prefix). Both were **wrong** in a way that would have shipped broken behavior:

| Hook | Original (wrong) | Corrected (shipped) | Reason |
|------|------------------|---------------------|--------|
| **Build** | Prefix on `ELBuildControl.BuildAndLaunchCraft` ([BuildControl.cs:684](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/BuildControl.cs)) | Prefix on `ELBuildControl.BuildCraft` ([BuildControl.cs:833](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/BuildControl.cs)) | `BuildAndLaunchCraft` runs at the END of the build, AFTER `DoWork_Build` has already drained `padResources` across many physics ticks. Hooking it would have closed the new-vessel spawn but left the pad-resource-drain griefing vector wide open. `BuildCraft` is the upfront state-flip (`state = State.Building`) — aborting before that flip blocks the entire debit loop. |
| **Recycler** | Prefix on `RecyclerFSM.CollectParts` ([Recycler/StateMachine.cs:373](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/Recycler/StateMachine.cs)) | Postfix on `ELRecycler.CanRecycle(Vessel)` ([Recycler/Recycler.cs:83](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/Recycler/Recycler.cs)) | `OnTriggerStay` at [Recycler.cs:110-112](https://github.com/taniwha/Extraplanetary-Launchpads/blob/master/Source/Recycler/Recycler.cs) sets `RecycleField.enabled = false` BEFORE invoking `CollectParts`. Returning false from a `CollectParts` prefix leaves the trigger collider permanently disarmed — the FSM doesn't re-enter `state_idle` (the only state with `RecycleField.enabled = true` re-set logic) without the `event_parts_exhausted` chain, which an aborted `CollectParts` never fires. Postfixing `CanRecycle` back to `false` instead stops `OnTriggerStay`'s `if (... && CanRecycle ...)` block before the disarm, leaving the trigger armed for the recycler's owner. |

### S7 implementation summary

`LunaCompat/Mods/ExtraplanetaryLaunchpads/ExtraplanetaryLaunchpadsIntegration.cs` — adds `EplPerAgencyPreflight` static helper (~210 lines) with:

- **Prefix on `ELBuildControl.BuildCraft`** — reads `__instance.builder.vessel` via reflection, looks up `lmpOwningAgency` via `AgencySystem.Singleton.TryGetOwningAgency`, aborts with a red `LogScreenMessage` (`Cannot build on agency {DisplayName}'s launchpad.`) when cross-agency.
- **Postfix on `ELRecycler.CanRecycle(Vessel)`** — overrides `__result = false` when the recycler's owning agency != local agency. Per-vessel-id toast suppression (static `_lastRecyclerToastedVesselId` Guid) prevents per-physics-tick screen spam from `OnTriggerStay`'s repeated `CanRecycle` calls.
- **Permissive axes** (mirrors S5 / spec §10 Q3): gate=off / no local agency / dict-miss / Unassigned-sentinel pad/recycler / same-agency all pass through.
- **Drift defense**: `AccessTools` null-checks log a startup `Warning` with version context (audit was against `taniwha/Extraplanetary-Launchpads` SHA `0bb3c5b0`); per-call `try/catch` returns the upstream verdict on exception. The pre-existing recycler-RNG seed-pin code is unchanged.

**Acceptance.** Two-agency repro: Agency A pilot attempts build on Agency B's pad → red toast `Cannot build on agency {B-name}'s launchpad.`, no state transition to Building, no resource drain. Agency A pilot brings craft into Agency B's recycler trigger field → red toast `Cannot recycle at agency {B-name}'s recycler.`, `RecycleField.enabled` stays true, no part destruction. Same-agency build/recycle works normally. Unassigned-sentinel (pre-0.31 sentinel) pad/recycler bypasses the check — any agency may interact (spec §10 Q3). Gate-off pass-through preserves shared-mode behavior bit-for-bit.

---

## Tests

1. **Single-agency, single-player:** EPL works identically to stock LMP (regression check). Build, recycle, survey, launch — all flows green.
2. **Two-agency, same-agency build:** Agency A pilot lands on Agency A's pad, builds. Resources decrement, vessel spawns, ownership = Agency A on both clients.
3. **Two-agency, cross-agency build (post-S7):** Agency A pilot lands on Agency B's pad, attempts build → toast appears, no resource change, no vessel spawn.
4. **Recycler symmetry:** repeat tests 2-3 against a recycler-equipped pad.
5. **Survey stake placement:** Agency A drops a survey stake on Mun. Both agencies see the stake (asymmetric-visual; documented behavior, not a bug).
6. **Tech gating:** Agency A has Tech Tree node X researched, Agency B does not. Both open the EPL part picker on identical pads. A sees the part listed; B does not (their `ResearchAndDevelopment.Instance` is per-agency-projected).
7. **Recycler determinism regression:** existing LunaCompat seed-pin behavior unchanged under per-agency.

---

## Tracking

### Last validated

- **Fork commit:** `d42479fd` (2026-05-19, S1 ship — per-agency baseline this audit is taken against).
- **EPL upstream:** `taniwha/Extraplanetary-Launchpads`, master branch, SHA `0bb3c5b0bf083e4284611682cc4f65f6b4a9d77b`. Cloned to `F:/tmp/mks-external/EPL` ([[reference-mks-external-clones]]).
- **EPL files source-walked:** `Source/Settings.cs`, `Source/BuildControl.cs`, `Source/Recycler/StateMachine.cs`, `Source/Survey/SurveyStake.cs`, `Source/Survey/SurveyTracker.cs`, `Source/UI/PartSelector.cs`, `Source/Recipes/` (definitions only). Grep across full `Source/` confirmed zero `Funding.Instance.Funds` / `ResearchAndDevelopment.Instance.Science` references.
- **LunaCompat reference:** `f:/tmp/mks-external/LunaCompat@25e164bf` — existing "Static random seed (recycling)" handling confirmed sufficient under per-agency.

### Decisions ratified — 2026-05-19

| Question | Answer |
|----------|--------|
| Does EPL need a `ScenarioModule` projector entry in this fork? | **No.** EPL's only `ScenarioModule` writes external preferences (not into the save), and all in-save state is vessel-attached. |
| Does EPL need a new `AgencyState` field / router? | **No.** Career state is vessel-local; `lmpOwningAgency` partition already covers it. |
| Does EPL touch `Funding.Instance` or `ResearchAndDevelopment.Instance`? | **No.** Source grep returned zero references. Builds cost raw resources only; tech-gating is read-only against a per-agency-projected singleton. |
| Is the existing LunaCompat "Static random seed (recycling)" handling sufficient? | **Yes.** Recycler RNG-determinism doesn't require per-agency-specific extension; under 5.17a write-rejection only the owning agency's writes land anyway. |
| Build merge-cross-agency preflight (S7) — proactive or wait-for-soak? | **Proactive.** Cross-agency pad/recycler use is a silent free-vessel exploit; ship the LunaCompat preflix alongside S5/S6 in the same session. ~30 lines. **Shipped 2026-05-19 in LunaCompat commit `1dc3196` — final implementation ~210 lines after hook-target corrections caught by multi-lens review.** |
| Are survey stakes a per-agency visibility concern? | **Defer.** Asymmetric-visual carve-out candidate, not a state leak. Revisit if operators report stake-clutter from co-located cohorts. |

Implementation slice: see [implementation-spec.md](implementation-spec.md) §S7.
