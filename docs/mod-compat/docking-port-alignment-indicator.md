# Docking Port Alignment Indicator (DPAI) — compat layer analysis

**Identification.** [DPAI](https://github.com/bfishman/Docking-Port-Alignment-Indicator) renders a target-alignment HUD next to the navball during docking; also lets the player name their docking ports for the target picker. Originally NavyFish, current maintenance: `bfishman/Docking-Port-Alignment-Indicator`.

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
| `Source/DockingPortAlignmentIndicator/DockingPortAlignmentIndicator.cs` (`[KSPAddon(Startup.Flight, false)] : MonoBehaviour`) | Flight-scene addon. **No `[KSPField(isPersistant)]`.** UI prefs flow through a local `Settings.Configuration` → config file on disk. |
| `Source/DockingPortAlignmentIndicator/ModuleDockingNodeNamed/ModuleDockingNodeNamed.cs` (`: PartModule`) | Three `[KSPField(isPersistant = true)]`: `string portName` (user-renamable port name), `bool initialized`, `string controlTransformName`. Attached to docking ports via ModuleManager patch (`GameData/.../Patches/`). |
| `Source/DockingPortAlignmentIndicator/DPAI_Panel.cs`, `Drawing.cs`, `BitmapFont.cs`, `Toolbar.cs`, `SettingsWindow.cs` | Pure UI / drawing helpers. No state. |
| `Source/DockingPortAlignmentIndicator/DPAI_RPM/` | RasterPropMonitor (RPM) IVA prop integration. Local-only display. |

**No `ScenarioModule`. No contracts, no science, no career surface.**

---

## This fork touchpoints

- Scenario: **none.**
- Vessel: `ModuleDockingNodeNamed`'s three persistent fields ride standard PartModule sync. Renamed port names propagate to all peers via vessel proto.
- Custom relay: not used.

---

## Interaction with PerAgencyCareer

**Zero career interaction.** Identical compat class to KER, MechJeb2, Trajectories.

**Visible peer-side cosmetic:** agency-A renames their port "Hub Forward"; agency-B sees that name on agency-A's vessel because `portName` is part of the vessel proto. Expected and desirable behaviour (target picker is identifiable across clients).

---

## Failure modes (multiplayer)

1. **Renamed port collision** — two ports on the same vessel renamed to the same string. Cosmetic; doesn't affect docking mechanics.
2. **`initialized` bool divergence** — if used as a "first-time-render" sentinel, persisting it through the wire could cause a peer to skip a first-render init step. Low severity; resolves on next scene change.
3. **DPAI HUD targeting** — DPAI's display code reads `FlightGlobals.ActiveVessel.targetObject` locally. Each client sees their own active vessel's alignment — no peer interaction.

---

## Proposed layering

| Layer | Owner repo | Deliverable |
|-------|-------------|--------------|
| Core LMP fork | no | None owed. |
| Luna Compat (Harmony/MM) | no | None owed. |
| Luna Compat server plugin | no | None owed. |
| Operational | yes | Modlist policy enforces DPAI + version uniformity. |

---

## Tests

1. Single-agency: rename a docking port, reconnect, name survives.
2. Cross-agency: agency-A renames port "Hub Forward"; confirm agency-B sees that name on agency-A's vessel in their target picker.
3. Two ports on different vessels with identical names: target picker shows both; player disambiguates via vessel name.

---

## Tracking

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **DPAI upstream:** `bfishman/Docking-Port-Alignment-Indicator`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** none — DPAI has no per-agency surface.
- **DPAI files inspected:** `Source/DockingPortAlignmentIndicator/` file inventory, `Source/.../DockingPortAlignmentIndicator.cs` (KSPAddon MonoBehaviour, no isPersistant), `Source/.../ModuleDockingNodeNamed/ModuleDockingNodeNamed.cs` (PartModule, three isPersistant fields).
- **Findings this pass:**
  1. DPAI is a flight-scene HUD addon plus a docking-port-extending PartModule for renaming.
  2. No `ScenarioModule`, no career interaction.
  3. The three persistent PartModule fields ride standard vessel proto sync.
- **Net verdict:** no work owed at any layer.
- **Gaps still open:** none.
