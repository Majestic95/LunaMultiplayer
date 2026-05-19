# MechJeb2 — compat layer analysis

MechJeb2 largely behaves as **local autopilot** — much of its state attaches to **`Vessel` / `FlightComputer`** and does not automatically flow through Luna MP’s canonical scenario store. Luna Compat’s README does **not** list MJ2 Harmony coverage as of last cross-check (`lunacompat-inventory.md`). Treat MJ2 compat as **greenfield investigation** coordinated with upstream LMP patterns (locks, warp, vessels).

---

## Why MJ2 stresses multiplayer

Rough categories:

1. **Steering/throttle / RCS / SAS** — LMP exchanges vessel kinematics via its vessel sync pathway; MJ constantly writes flight control. Potential tension: MJ local authority vs interpolated remote state (“fighting” autopilot corrections).
2. **Manœuvre nodes and warp** — Node editing and warp requests must respect LMP warp protocol and locking; MJ can enqueue sequences that contradict server or peer concurrency rules.
3. **Staging** — Automated staging changes vessel hierarchy; staging must converge with authoritative proto updates.
4. **Docking / target designation** — Target selection is often local-only; docking alignment may drift under latency.

None of those are SCANsat-like “scenario blob” bugs; they are **control-plane** and **vessel-state** coherence problems.

---

## Existing fork hooks to consider before new message types

| Mechanism | Use for MJ |
|-----------|------------|
| `ModMsgData` (`ModCliMsg`) | Optional channel for MJ-specific synchronisation payloads if MJ exposes a sane serialisable subset (`ModMessage` relay semantics). Prefer small, bounded payloads. |
| Vessel locks / ownership | Decide whether MJ is allowed full pilot authority only for **locks holder** (`Server/System/Lock*` + client equivalents). Docs align with whoever owns manoeuvre-heavy ops. |
| Warp | MJ warp must remain inside LMP’s warp coordination; MJ patches may need Harmony to redirect automatic warp triggers to game-approved paths. |

`LmpClient/Properties/TypeForwarding.cs` already forwards vessel message types — external mods referencing `LmpCommon` types should keep binding against forwarded assemblies correctly; not MJ-specific but relevant for MJ plugins.

---

## Design options (pick one stance per capability)

These are deliberate product choices — document the chosen stance in PR text when implementing.

### A — No explicit MJ layer (recommended starting baseline)

Assume MJ autopilot mirrors **solo** semantics: whoever controls the vessel per LMP conventions may MJ it; observers see physics outcome only. Lowest maintenance; highest “fighting controls” friction if multiple clients touch controls.

### B — Client-local clamp

Harmony shim on MJ throttle/warp staging that **gates** MJ actions unless `YOUR lock`/`YOUR vessel pilot` — keeps server dumb. Fits community patch-mod pattern (possibly alongside Luna Compat if scope grows).

### C — Scripted relay for shared planners

MJ “Ascent Guidance” as **shared manoeuvre strip** synced via `ModMsgData` among crew — speculative; high protocol design cost.

---

## Open questions checklist

Answer before writing code:

1. **Gameplay rule:** MJ allowed in career for all pilots, observers only, or disabled on shared vessels?
2. **Authority:** MJ writes allowed only while holding which LMP locks?
3. **Warp:** Should MJ-triggered warp be forced through LMP’s UI flow only?
4. **Persistence:** MJ window positions / settings — acceptable divergent-local (per Luna Compat’s pattern for GUI mods) vs must match?
5. **MechJebEmbedded / MJ arming** parts — do ModuleManager/LMP replicate every field today, or is an explicit PartModule definition needed (Luna Compat style)?

---

## Suggested incremental path

1. **Inventory MJ PartModules** affecting flight that already replicate via vanilla LMP part sync.
2. **Repro matrix** single-player MJ vs two-client MJ vs observer — note desync artefacts (staging, manoeuvre burns, docking).
3. If issues cluster around **staging/warp/control**, prioritise Harmony on MJ’s entry points toward LMP-held APIs rather than inventing MJ-specific server schemas.
4. If issues cluster around **scenario/career** (unlikely vanilla MJ), revisit `AgencyScenarioProjector`.

---

## Mod-source walk (MJ2 repo `MuMech/MechJeb2`)

Walked branch `dev` via WebFetch on 2026-05-18.

### Entry point + persistence

| Class / file | Role | Persistence shape |
|--------------|------|--------------------|
| `MechJeb2/MechJebCore.cs` (`MechJebCore : PartModule, IComparable<MechJebCore>`) | The core PartModule that hosts every MJ "ComputerModule" instance per vessel. **Not a ScenarioModule.** | Three tiers (`OnLoad` / `OnSave` overrides): (1) `MechJebLocalSettings` ConfigNode embedded in the vessel proto — travels with the part; (2) `mechjeb_settings_type_{vesselName}.cfg` on local disk; (3) `mechjeb_settings_global.cfg` on local disk. Only one `[KSPField(isPersistant)] bool running`. |
| `MechJeb2/MechJebAR202.cs` and the `MechJebModule*.cs` set (~30 files) | ComputerModules: SmartASS, ManeuverPlanner, AscentAutopilot, LandingAutopilot, DockingAutopilot, etc. | Discovered by reflection (`LoadComputerModules` → `_moduleRegistry`); each has its own `OnLoad(local, type, global)` / `OnSave(local, type, global)` and inserts a child node under the corresponding settings tier. None ride wire surfaces of their own. |
| Control hook | `vessel.OnFlyByWire += OnFlyByWire` registered when MJ becomes the active vessel module. `Drive(state)` then iterates ComputerModules and writes to `FlightCtrlState`. | Pure local, only fires for the local KSP's active vessel. |

### Per-agency cross-walk

| MJ surface | Path through Luna MP | Per-agency verdict |
|-------------|-----------------------|---------------------|
| `MechJebLocalSettings` inside vessel proto | Travels with the part — replicated through standard vessel proto sync. Picks up `lmpOwningAgency` along with everything else. | **Already correct under Stage 5.18b vessel ownership.** No agency-specific code needed. |
| `mechjeb_settings_type_*.cfg`, `mechjeb_settings_global.cfg` | Local disk only — **never crosses the wire.** | Per-install, never multiplayer-visible. No-op. |
| `OnFlyByWire` writes to `FlightCtrlState` | Only registered when MJ is on the active vessel module — i.e. the local player is flying that vessel. LMP control of a vessel already follows locks; the local player can't be the active controller of a vessel without holding the appropriate lock. | **No new gate needed.** The existing LMP lock system gates MJ writes for free. The "fighting controls" worry from option A is bounded by lock ownership. |
| Manoeuvre node planning | MJ writes via stock `Vessel.patchedConicSolver.AddManeuverNode`. Manoeuvre nodes are part of vessel proto and sync to peers like any other vessel state. | Standard vessel sync. Two clients fighting over the same vessel's nodes is gated by who holds the vessel's update lock. |
| Warp (`TimeWarp.SetRate`) | MJ calls the stock API. LMP has its own warp protocol; MJ-initiated `SetRate` is indistinguishable from a player keypress and routes through the same coordination path. | No MJ-specific handling required. |
| ComputerModule inter-vessel coordination | **None found in source** — no `ScenarioRunner.Instance` or cross-vessel events. `vessel.GetMasterMechJeb()` selects one MJ core per vessel, all local. | No wire surface to worry about. |

### Career / per-agency surface

**Zero.** MJ writes nothing to `Funding`, `ResearchAndDevelopment`, `Reputation`, `ContractSystem`, `StrategySystem`, `ProgressTracking`, or `ScenarioUpgradeableFacilities`. None of `AgencyState`'s fields map to anything MJ touches. The original doc's "If issues cluster around scenario/career, revisit `AgencyScenarioProjector`" can be downgraded: that path is unreachable from MJ.

### Failure modes (revised against source)

1. **Two clients holding control of the same vessel via lock-stealing** — fixed at the LMP lock layer, not at MJ. MJ inherits whatever the lock system enforces.
2. **MJ-generated manoeuvre node arrives at a peer before the burn it implies starts firing** — standard vessel-update latency; not MJ-specific. Cosmetic node-flicker on peers.
3. **Auto-staging during high warp** — MJ stages locally; staged vessel state replicates via proto. Risk: timing skew between MJ's "stage now" decision and the canonical proto update propagating; if a peer is rendering the vessel at that moment, brief visual desync. Bounded.
4. **Settings-file drift across installs** — operator policy enforces version uniformity for MJ2 itself, but the `mechjeb_settings_*.cfg` files are per-install. Players will diverge cosmetically (UI window positions, default tolerances) — non-issue.

### Compat layer recommendation

| Surface | Owner verdict |
|---------|----------------|
| MJ persistence in vessel proto | **No action** — vanilla LMP vessel sync handles `MechJebLocalSettings` for free. |
| MJ flight-control writes | **No action** — gated by existing LMP locks via the active-vessel pathway. |
| MJ warp / staging | **No action** — flows through stock APIs that LMP already coordinates. |
| MJ per-agency career interaction | **No surface** — MJ does not touch career or scenario state. |
| Per-install settings cfg files | **No action** — local-only, never wire-visible. |

**Net:** MJ2 is plumbing-clean against this Luna MP fork **and** orthogonal to per-agency career. The original doc's five open product questions (career permission, lock authority, warp policy, persistence parity, PartModule replication) can be re-evaluated:

| Original question | Re-evaluation after source walk |
|--------------------|----------------------------------|
| 1. Gameplay rule: MJ allowed for all pilots / observers / disabled? | Still a product call, but no technical gate from MJ — it's a policy on what the modlist contains, not what code does. |
| 2. Authority: MJ writes allowed under which LMP locks? | **Resolved by source.** Existing active-vessel + control-lock gates apply automatically. |
| 3. Warp routing through LMP UI? | **Resolved by source.** MJ uses stock `TimeWarp.SetRate`; LMP intercepts the same API as for manual warp. |
| 4. Window positions / settings divergence? | **Resolved by source.** Per-install cfg files — accept divergence; no action. |
| 5. PartModule field replication completeness? | **Resolved by source.** Only `bool running` is `isPersistant`; the rest lives in MJ's own `MechJebLocalSettings` node which proto sync carries verbatim. |

### Design questions raised by the source walk

None. MJ2 is the simplest of the four mods analysed — no per-agency design call is owed.

---

## Tracking

| Item | Notes |
|------|--------|
| Luna Compat overlap | Not listed Harmony target — confirm each release |

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **MJ2 upstream:** `MuMech/MechJeb2`, `dev` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** `LmpCommon/Message/Data/ModMsgData.cs` (relay shape — not actually needed for MJ2 in the end), `LmpClient/Properties/TypeForwarding.cs`, `Server/System/LockSystem.cs` (active-vessel lock gate — load-bearing for the MJ2 verdict).
- **MJ2 files inspected:** `MechJeb2/MechJebCore.cs` (class header, persistent fields, OnLoad/OnSave shape, ComputerModule iteration, `OnFlyByWire` registration), top-level file inventory of `MechJeb2/`.
- **Findings this pass (all new vs prior doc):**
  1. MJ2 is a `PartModule`, not a ScenarioModule. Single `[KSPField(isPersistant)] bool running` plus a `MechJebLocalSettings` ConfigNode under the part.
  2. Persistence is three-tiered: vessel-proto-embedded (`MechJebLocalSettings`), per-vessel-type cfg on disk, global cfg on disk. Only the first crosses the wire — automatically, via standard vessel proto sync.
  3. Control authority is gated by `OnFlyByWire` registration on the active vessel; the active-vessel state already follows LMP control locks. **MJ2 writes inherit LMP lock authority for free.**
  4. No inter-vessel coordination, no `ScenarioRunner.Instance` hooks, no career/scenario surface.
  5. **All five original product questions resolved or downgraded to non-issues** (see table above).
- **Gaps still open:** none.
- **Net verdict:** no core fork change owed; no Luna Compat work owed; no per-agency design call owed. The "next-step research question" from the prior pass is closed.
