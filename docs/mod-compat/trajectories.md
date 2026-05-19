# Trajectories — compat layer analysis

**Identification.** [Trajectories](https://github.com/neuoy/KSPTrajectories) predicts atmospheric-entry trajectories (drag + lift) and renders the predicted impact point. Originally by Youen / Kobymaru / PiezPiedPy; current maintenance: `neuoy/KSPTrajectories`.

The audit surface centres on a quirk: **Trajectories is registered as a `ScenarioModule`** even though it doesn't persist scenario-level state — KSP's `[KSPScenario]` attribute is the cleanest way to get a per-scene-attached lifecycle hook.

---

## Luna Compat status

| In Luna Compat Harmony list? | In Part Sync list? | Server plugin flag? |
|------------------------------|---------------------|---------------------|
| No | No | No |

Not listed in [Luna Compat](https://github.com/TheXankriegor/LunaCompat) ([overlap inventory](lunacompat-inventory.md)).

---

## State ownership

| Where state lives | Shape |
|-------------------|-------|
| `src/Plugin/Trajectories.cs` (`[KSPScenario(AddToAllGames, FLIGHT)] : ScenarioModule`) | Registered as a `ScenarioModule` attached to ALL game modes, **FLIGHT scene only**. **`OnSave` is commented out** — the module writes **nothing** to the save game. `OnLoad` is a lifecycle hook that initialises subsystems (`DescentProfile.Start`, `MapOverlay.Start`, etc.) and loads the global `Settings`. |
| `src/Plugin/TrajectoriesVesselSettings.cs` (`: PartModule`) | Thirteen `[KSPField(isPersistant)]`: `Initialized`, `EntryAngle`, `EntryHorizon`, `HighAngle`, `HighHorizon`, `LowAngle`, `LowHorizon`, `GroundAngle`, `GroundHorizon`, `RetrogradeEntry`, `TargetBody` (string), three target position doubles (x/y/z), `ManualTargetTxt`. |
| `src/Plugin/Settings.cs` | Global settings — `config.xml` on disk under `GameData/Trajectories/`. Per-install; not save-game-attached. |

**No contracts, no science definitions, no career-state writes.**

---

## This fork touchpoints

- Scenario: Trajectories is registered as a `ScenarioModule`, so it appears in `ScenarioStoreSystem.CurrentScenarios`. Each `SendScenarioModules` invocation runs the projector across it, but **`AgencyScenarioProjector.CareerScenarios` does not include `Trajectories`** — projection passes the blob through unchanged. Combined with the empty `OnSave`, the relayed blob is whatever KSP's base `ScenarioModule.Save` produces — generally empty / minimal-shape.
- Vessel: `TrajectoriesVesselSettings` rides standard PartModule sync — all 13 persistent fields travel with the vessel proto.
- Custom relay: not used.

---

## Interaction with PerAgencyCareer

**Zero career interaction.** No `AgencyState` field maps to anything Trajectories touches.

**Peer-visible per-vessel target leakage (cosmetic only):**

`TargetBody` + target position coordinates are PartModule fields, so they're part of vessel proto. Agency-B inspecting agency-A's vessel would, in principle, find Trajectories has a target set on that vessel — visible in the right-click menu where `TrajectoriesVesselSettings` exposes fields. Not actionable; Trajectories' display rendering targets `FlightGlobals.ActiveVessel` only.

---

## Failure modes (multiplayer)

1. **Empty-scenario relay churn** — if KSP's base `ScenarioModule.Save` writes mode-dependent metadata, the `Trajectories` blob can differ between two clients (one in FLIGHT, one in SPACECENTER), producing spurious `SendScenarioModules` updates. Bounded by `SendModulesConfigNodes`'s SHA hash check — non-event in practice.
2. **`TargetBody` referencing a body the other client doesn't have** — disallowed by the mandatory-modlist policy ([README.md](README.md)). Defence-in-depth covered by stock vessel proto resilience.
3. **Aero model selection** — Trajectories auto-detects FAR vs stock at startup. If two clients have different aero stacks (FAR on one, stock on the other), their predicted trajectories diverge. Disallowed by modlist policy.
4. **`TrajectoriesVesselSettings` field drift on proto relay** — standard part sync covers `[KSPField(isPersistant)]`; no special handling needed.

---

## Proposed layering

| Layer | Owner repo | Deliverable |
|-------|-------------|--------------|
| Core LMP fork | no | The `Trajectories` ScenarioModule blob is benign; no projector entry needed. |
| Luna Compat (Harmony/MM) | no | None owed. |
| Luna Compat server plugin | no | None owed. |
| Operational | yes | Modlist + aero-stack uniformity already enforced. |

---

## Tests

1. Single-agency: set a Trajectories target on a vessel, reconnect, target survives.
2. **Cross-agency target visibility check**: agency-A sets a target on their vessel; confirm agency-B's Trajectories overlay doesn't try to draw a prediction for agency-A's vessel as if it were their own. Trajectories' code should target the local active vessel only, but worth confirming.
3. Aero-stack uniformity (modlist test): all clients use the same aero stack (stock or FAR). Mismatched stacks disallowed.

---

## Tracking

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **Trajectories upstream:** `neuoy/KSPTrajectories`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** none — Trajectories has no per-agency surface.
- **Trajectories files inspected:** `src/Plugin/` file inventory, `src/Plugin/Trajectories.cs` (KSPScenario + ScenarioModule class, **commented-out OnSave**), `src/Plugin/TrajectoriesVesselSettings.cs` (PartModule with 13 persistent fields covering descent profile + target).
- **Findings this pass:**
  1. Trajectories is technically a `ScenarioModule` but writes no save-game state (`OnSave` is commented out). The mod uses `[KSPScenario]` for lifecycle, not for persistence.
  2. Per-vessel settings (descent profile, target body, target position) live in a `PartModule` and ride standard vessel proto sync.
  3. Global settings persist to a per-install XML; never wire-visible.
  4. No career, contract, science, or `AgencyState` interaction.
- **Net verdict:** no work owed at any layer. Effectively in the same compat class as MechJeb2 and KER.
- **Gaps still open:** none.
