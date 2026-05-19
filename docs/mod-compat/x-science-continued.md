# [x] Science! Continued — compat layer analysis

**Identification.** “[x] Science” refers to **[x] Science! Continued** — a client-side science report / checklist UI (tracks experiments, recovered science, vessel-held data). Active forks are distributed as *Continued* (e.g. linuxgurugamer line on SpaceDock / CurseForge / CKAN). It is **not** a planet pack or a multiplayer patch mod.

---

## Luna Compat status

Not listed in [Luna Compat](https://github.com/TheXankriegor/LunaCompat) Harmony or Part Sync tables (as of overlap review in [`lunacompat-inventory.md`](lunacompat-inventory.md)). No dedicated server plugin expectation.

**Interpretation.** Treat this mod as **read-mostly UX on top of stock R&amp;D / scenario state** unless source review shows undisclosed network hooks (unlikely).

---

## Interaction with Luna MP baseline

- Uses KSP APIs and local scenario content to summarise **ScienceSubject**, experiment completion, onboard containers, etc.
- Does **not** introduce authoritative science payloads on its own branch in LMP; stock `Share*` progress pathways still originate from KSP’s normal science completion flow.

Risk class is therefore **presentation + timing**, not bespoke wire format.

---

## Interaction with PerAgencyCareer (this fork)

When `PerAgencyCareer` is **on**:

1. **Science subject archive** routes per sender into `AgencyState.ScienceSubjects` via `AgencyResearchRouter.TryRouteScienceSubject` (Stage 5.17e‑5); peers do **not** receive that relay. The authoritative per-agency view is reconstructed when the client pulls scenarios and **`AgencyScenarioProjector`** splices the requesting player’s subjects into **`ResearchAndDevelopment`** text.

2. **What [x] Science reads** — if it reads the **currently loaded `ResearchAndDevelopment` scenario blob** after projection applies, checklists should match **that agency’s** completed subjects and related progress the same way the stock science UI does.

### Failure modes / compatibility concerns

| Symptom | Likely cause |
|---------|----------------|
| Checklist briefly “wrong” after reconnect | UI built before projected R&amp;D arrives; clears after scenario refresh / scene change — confirm on test. |
| Checklist mixes another agency for **completed/archived** science | Would imply the mod ignores projected R&amp;D and reconstructs state from unparsed/shared sources. **Source walk below shows it does NOT** — it reads `ResearchAndDevelopment.GetSubjects()`, which is post-projection. Risk class: low. |
| Checklist shows "Crew Report at Eve" for **onboard, unrecovered** science that belongs to another agency | **Confirmed risk** — see mod-source walk. The vessel-data path reads `FlightGlobals.Vessels` + `HighLogic.CurrentGame.flightState.Save()`, neither of which is filtered by `lmpOwningAgency`. Cosmetic-only (clicking does not transfer ownership), but visible. |
| Sandbox / gate interaction | Dual-mode routing: sandbox + gate-on deliberately **falls through** to legacy relays for science subjects in tests (`AgencyResearchRouter` callers + `MockClientTest`). [x] Science in sandbox with mixed gate settings — verify expected product behaviour. |

### Do we need a dedicated “compat layer”?

**Default:** **No new core Luna MP code** solely for [x] Science unless playtesting exposes a reproducible stale-read path (then prefer a **minimal Harmony shim in a sidecar**, e.g. Luna Compat or a fork-specific addon, forcing window refresh after LMP finishes scenario ingestion — only if warranted).

Priority is **verification**, not preemptive patching.

---

## Mod-source walk ([x] Science repo `linuxgurugamer/KSP-X-Science`)

Walked branch `master` via WebFetch on 2026-05-18. Source paths below are inside that repo.

### Entry point + state ownership

| Class / file | Role | Persistence shape |
|--------------|------|--------------------|
| `X-Science/ScienceChecklistAddon.cs` (`[KSPAddon(KSPAddon.Startup.MainMenu, true)] : MonoBehaviour`) | Main addon. **Not a ScenarioModule.** | None in the save game. UI prefs flow through a separate `Config` class → config file on disk (`Config.SetWindowConfig` / `GetWindowConfig`). |
| `X-Science/ScienceContext.cs` | Builds the science checklist. | Pure read; holds in-memory dictionaries rebuilt from KSP state per scene/refresh. |
| Other files (`BiomeMapper.cs`, `ExperimentFilter.cs`, `ScienceInstance.cs`, `WindowSettings.cs`, etc.) | Helpers for filtering / display. | All in-memory or flow through `Config`. |

**No PartModule. No ScenarioModule. No ConfigNode written into the save game.** Confirms the doc's prior "read-mostly UX" claim — the mod doesn't introduce a new persistence target.

### Data read paths

`ScienceContext.cs` gathers data via three stock KSP entry points (quoted from the source):

1. **Completed / archived subjects** — `ResearchAndDevelopment.GetSubjects()` returns the per-game subject archive.
2. **Loaded-vessel onboard science** — `FlightGlobals.Vessels.Where(x => x.loaded)` → `v.FindPartModulesImplementing<IScienceDataContainer>().GetData()`.
3. **Unloaded-vessel onboard science** — `HighLogic.CurrentGame.flightState.Save(node)` then walks the resulting ConfigNode for science data inside protoVessel part modules.

### Cross-walk against this fork's per-agency surfaces

| Data path | Per-agency behaviour under the fork |
|------------|-------------------------------------|
| (1) `ResearchAndDevelopment.GetSubjects()` | **Already correct.** This reads the per-game R&D scenario, which `AgencyScenarioProjector.SpliceAgencyTechIntoResearchAndDevelopment` has rewritten with the requesting agency's `Science` / `Tech` / `Science` (subjects) on send. Checklist's "completed" column reflects this agency's archive only. |
| (2) `FlightGlobals.Vessels` (loaded) | **Cosmetic leak.** Luna MP relays vessels of all agencies into a player's `FlightGlobals.Vessels`. The mod enumerates ALL loaded vessels' science containers. A vessel owned by agency B that is loaded for agency A shows its onboard `ScienceData` in agency A's checklist. The data cannot be recovered without the owning client (vessel control follows `lmpOwningAgency`) — so the leak is "I can see they collected this" rather than "I can steal it." |
| (3) `flightState.Save()` (unloaded) | **Same shape as (2)** — the snapshot serialises every `protoVessel` known to the local KSP, regardless of agency ownership. Same cosmetic-only severity. |

### Failure modes

1. **(1) is clean.** No leak through the projected R&D path. Confirms the doc's prior assertion.
2. **(2) + (3) leak onboard science visibility through `FlightGlobals.Vessels` / `flightState`.** Cosmetic-only — display, not actionable.
3. **Window prefs (`Config`)** persist to disk per local install, not in the save. Two players at the same KSP install would share window prefs; in multiplayer each client has its own install, so non-issue.
4. **Refresh timing** — the doc's original concern about "checklist briefly wrong after reconnect" remains valid. `ScienceContext` rebuilds on game events; if those events fire before LMP's `LoadScenarioDataIntoGame` finishes, the checklist initialises from pre-projection state. Resolves on next scene change or game-event-triggered refresh.

### Compat layer recommendation

| Surface | Owner verdict |
|---------|----------------|
| Per-agency archived-subject correctness | **Already handled** by `AgencyResearchRouter.TryRouteScienceSubject` + projector R&D splice. No [x] Science specific code. |
| Cosmetic onboard-science display of foreign-agency vessels | **Open product decision** — see below. Optional fix is a Harmony shim that filters `ScienceContext`'s loaded-vessel enumeration by `lmpOwningAgency` (mod-side, would belong in Luna Compat or a fork-side sidecar, not in core LMP). |
| Refresh timing | **Optional sidecar** — a one-line Harmony postfix on `LoadScenarioDataIntoGame` triggering `ScienceContext.Refresh()` would close the reconnect-blip window. Only if observed in playtest. |
| Window prefs persistence | **No action.** Per-install. |

### Design questions raised by the source walk

1. **Should foreign-agency onboard science be visible in `[x]` Science's checklist?** Status quo today: yes (cosmetic leak through `FlightGlobals.Vessels`). Per-agency-strict alternative: filter by `lmpOwningAgency` via Harmony. Implication: "private exploration" is more thorough but the player can no longer see "what is humanity collecting overall" — which some operators may consider a feature, not a bug.
2. **Is the reconnect-blip worth a Harmony shim?** Or accept it as a known cosmetic and rely on the next scene change for refresh?

---

## Recommended operator policy

- Modlist uniformity already enforced (see [README.md](README.md) "Operator policy — modlist uniformity"). `[x]` Science version match is therefore automatic.
- In **Career per-agency** sessions, clarify to players that **completed/archived** checklist entries mirror **agency-local** archived science (correct via projection), but **onboard, unrecovered** science of other agencies' loaded/unloaded vessels remains visible (cosmetic leak via stock vessel enumeration). Set expectations rather than hiding it.

---

## Test matrix (minimum)

Perform with Luna Compat off (unless unrelated mods require it):

1. Player A completes an experiment · transmits/recovers · confirm A’s checklist only.
2. Player B (different agency) never gains that completion in UI or vessel science until B performs/recovers it.
3. Reconnect · checklist still agency-correct after full sync.
4. Repeat in **sandbox** vs **Career per-agency** if your server exposes both gates.

---

## Tracking

| Item | Notes |
|------|--------|
| Last doc pass | Scenario projection + AgencyResearchRouter model |
| Code anchors | `Server/System/Agency/AgencyResearchRouter.cs`, `AgencyScenarioProjector` R&amp;D splice, `AgencyState.ScienceSubjects` |

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **[x] Science upstream:** `linuxgurugamer/KSP-X-Science`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** `Server/System/Agency/AgencyResearchRouter.cs::TryRouteScienceSubject`, `Server/System/Agency/AgencyScenarioProjector.cs::SpliceAgencyTechIntoResearchAndDevelopment` (Science subject splice).
- **[x] Science files inspected:** `X-Science/ScienceChecklistAddon.cs` (KSPAddon, not ScenarioModule; Config file persistence not save-game), `X-Science/ScienceContext.cs` (three stock-API read paths quoted in mod-source walk above), top-level file inventory of `X-Science/`.
- **Findings this pass:**
  1. The "completed/archived" path is clean — projector handles it.
  2. The "onboard, unrecovered science of foreign-agency vessels" path leaks cosmetically through stock `FlightGlobals.Vessels` / `flightState.Save()` enumeration. Not previously called out in this doc.
  3. No save-game persistence target in `[x]` Science — confirms no projector entry is needed for the mod itself.
- **Gaps still open (product calls):** all resolved 2026-05-18 — see below.

### Decisions ratified — 2026-05-18

| Question | Answer |
|----------|--------|
| Filter foreign-agency onboard science from checklist? | **Yes — ship Harmony postfix on `ScienceContext`** (Luna Compat sidecar, not core LMP fork). Skip vessels whose `lmpOwningAgency` ≠ local player's agency during the vessel-data enumeration. Keeps the SCANsat per-agency-isolation theme consistent. |
| Ship a reconnect-blip closer Harmony? | **Defer** until observed in playtest. The blip resolves on next scene change; not worth pre-emptive Harmony maintenance burden. |

Implementation slice: see [implementation-spec.md](implementation-spec.md) §`[x]` Science.
