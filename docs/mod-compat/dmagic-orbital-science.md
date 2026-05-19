# DMagic Orbital Science — compat layer analysis

**Identification.** [DMagic Orbital Science](https://github.com/DMagic1/Orbital-Science) adds science parts (magnetometers, imaging platforms, seismic sensors, etc.) plus a suite of **Contract Configurator contract definitions and custom CC parameter types**. It also tracks **asteroid science diminishing returns** and **discovered anomaly records** in its own `ScenarioModule`.

The mod sits at the intersection of three Stage-5 surfaces: CC contracts ([`AgencyContractRouter`](../../Server/System/Agency/AgencyContractRouter.cs)), science subjects ([`AgencyResearchRouter`](../../Server/System/Agency/AgencyResearchRouter.cs)), and the custom DMagic `ScenarioModule`. The first two are already routed correctly; the third is the new finding.

---

## Luna Compat status

| In Luna Compat Harmony list? | In Part Sync list? | Server plugin flag? |
|------------------------------|---------------------|---------------------|
| No | No (confirm against upstream) | No |

Not listed in [Luna Compat](https://github.com/TheXankriegor/LunaCompat) ([overlap inventory](lunacompat-inventory.md)).

---

## State ownership

### `Source/Scenario/DMScienceScenario.cs` (`: ScenarioModule`)

> **Re-walked 2026-05-19 against local clone at `F:/tmp/mks-external/DMagicOrbitalScience` SHA `a4e805b9` (current master tip).** csproj orphan check (the FFT-style trap that retired S3) passes — `DMScienceScenario.cs` IS in `Source/DMagicOrbital.csproj`'s `<Compile Include="..."/>` list (line 129). Exactly one ScenarioModule in the compiled set. Wire-format details below were verified verbatim against the actual OnSave/OnLoad bodies (`DMScienceScenario.cs:68-122` + `:124-182`); the original 2026-05-18 audit row had several field-type and shape errors corrected in the "Re-walked 2026-05-19" subsection at the bottom of this doc. **Verdict: S4 ships as planned, with the corrected entry shapes.**

Class header (verbatim verified):

```csharp
[KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]
public class DMScienceScenario : ScenarioModule
```

Persisted in OnLoad/OnSave (verbatim wire shape per re-walk):

| Data | Wire shape |
|------|-------|
| **Asteroid science results** | `Asteroid_Science` parent container with `DM_Science` children. Each `DM_Science` child has flat fields: `title` (string — used as the dict key), `bsv` (float, "BaseValue"), `scv` (float, "SciVal"), `sci` (float, "Science"), `cap` (float, "Cap"). All four numeric fields are **float, not double** (corrects the 2026-05-18 audit). |
| **Anomaly records** | `Anomaly_Records` parent container with `DM_Anomaly_List` per-body wrappers. Each `DM_Anomaly_List` has a `Body` field (int, `flightGlobalsIndex`) + nested `DM_Anomaly` children. Each `DM_Anomaly` child has flat fields: `Name` (string), `Lat` / `Lon` / `Alt` (doubles, serialized as `"N5"`-formatted strings with 5 decimal places). The wire is **2-level nested per-body** (corrects the 2026-05-18 audit's flat-composite-key assumption). |

### Other source surface

| File | Role | Notes |
|------|------|-------|
| `Source/Scenario/DMScienceData.cs` | Asteroid-science data class | Field types: `private string title; private float scival, science, cap, basevalue;`. Properties expose Title (read-only) + SciVal/Science (internal-set) + BaseValue/Cap (read-only). No Save/Load methods of its own — the parent ScenarioModule does the serialization. |
| `Source/DMAnomalyObject.cs` (root of Source, NOT under Scenario/) | Anomaly data class | Field types include `private double lat, lon, alt;` + `private string name;` + `private CelestialBody body;`. Two constructors — one from KSP's `PQSSurfaceObject` (live discovery) + one from deserialization params (5-arg). Path corrected from 2026-05-18 audit's `Source/Scenario/` claim. |
| `Source/Scenario/DMRecoveryWatcher.cs`, `DMTransmissionWatcher.cs` | KSP event listeners | Catch recover/transmit, update the scenario's asteroid science tracking. |
| `Source/Part Modules/` (folder) | Custom PartModules for DMagic science parts | Standard vessel-side fields; ride PartModule sync. |
| `Source/Contracts/` (folder) | Custom CC contracts | Ride `AgencyContractRouter` via Q6 (Offered shared, Active per-agency). |
| `Source/Parameters/` (folder, 8 files) | Custom CC parameter types: `DMAnomalyParameter`, `DMAsteroidParameter`, `DMCollectScience`, `DMCompleteParameter`, `DMLongOrbitParameter`, `DMOrbitalParameters`, `DMPartRequestParameter`, `DMSpecificOrbitParameter` | These define the CC PARAM nodes serialised into contract blobs. |
| `Source/DMConfigLoader.cs` | KSPAddon init | Loads `DMContractDefs.cs` config from disk. Per-install. |
| `Source/DMSeismicHandler.cs` | Seismic experiment handler | Vessel-side; standard PartModule + transient state. |

---

## CC parameter key naming check

DMagic's custom CC parameters use **PascalCase** keys (`Body`, `Situation`, `Biome`) instead of the stock-CC `targetBody`, `situation`, `biome`. **Verified safe** against the fork: `LmpClient/Systems/Scenario/ScenarioSystem.cs::BodyIndexKeys` and `BodyContextKeys` use `StringComparer.OrdinalIgnoreCase`, so DMagic's `Body`/`Biome` keys still match the sanitisation paths. No fork-side key-name addition required.

---

## This fork touchpoints

### Already correct under existing fork code

| DMagic surface | Path |
|-----------------|------|
| DMagic CC contracts (Offered / Accepted / Active / Completed lifecycle) | `AgencyContractRouter.TryRoute` — Q6 split. **No DMagic-specific code.** |
| Science subjects from DMagic experiments (e.g. `dmRPWS@KerbinInSpaceLow`) | `AgencyResearchRouter.TryRouteScienceSubject` — per-agency. **No DMagic-specific code.** |
| DMagic PartModules on vessels (magnetometer state, telescope pointing) | Standard vessel proto sync. |
| Custom CC PARAM body-context validation | `BodyContextKeys` already catches DMagic's PascalCase keys via OrdinalIgnoreCase. |

### NEW concern: `DMScienceScenario`

The scenario module ships **asteroid science** and **anomaly records** that are conceptually agency-shaped (each agency's "I've already milked this asteroid for science" and "I've discovered Mun monolith #3" log). Today, `DMScienceScenario` is **not in `AgencyScenarioProjector.CareerScenarios`** — every agency sees the unioned shared pool.

Result: agency A studies an asteroid for full science; agency B then visits the same asteroid and finds it already "milked." Agency A discovers an anomaly; agency B's anomaly UI shows it as already discovered. **Cross-agency leak, identical pattern to SCANsat coverage and FFT antimatter factory.**

---

## Interaction with PerAgencyCareer

**Career-isolated surfaces (already correct):**

- DMagic contracts and parameters ride `AgencyContractRouter`.
- DMagic experiment science subjects ride `AgencyResearchRouter`.

**Currently shared (the new finding):**

- Asteroid diminishing-returns tracking.
- Discovered anomaly records.

Both are persistent player-progress data and both are conceptually agency-scoped.

---

## Failure modes (multiplayer)

1. **Cross-agency asteroid science milking** — agency A captures full diminishing-returns on an asteroid; agency B visits the same asteroid and receives reduced science as if they had already studied it. Career-progress leak.
2. **Cross-agency anomaly record sharing** — agency A discovers an anomaly; agency B's UI shows the anomaly as known (or vice versa), reducing exploration value.
3. **Last-writer-wins on `DMScienceScenario` blob** — two clients updating asteroid science simultaneously: their blobs race through `ScenarioDataUpdater.RawConfigNodeInsertOrUpdate`. Standard scenario race; bounded by hash-check.
4. **DMagic CC contract popups from malformed PARAM** — sanitisation catches PascalCase keys via OrdinalIgnoreCase. **Already covered.**
5. **DMagic PartModule fields on vessel proto** — standard sync; no special handling needed.

---

## Proposed layering

| Surface | Owner verdict |
|---------|----------------|
| DMagic CC contracts | **Already handled.** `AgencyContractRouter` covers Q6. |
| DMagic science subjects | **Already handled.** `AgencyResearchRouter`. |
| DMagic CC parameter sanitisation | **Already handled.** OrdinalIgnoreCase BodyContextKeys. |
| **DMagic `DMScienceScenario` (asteroid science + anomaly records)** | **Open product decision.** Implementation sketch below. |
| DMagic vessel-side PartModules | **No action.** |

### Implementation sketch — if `DMScienceScenario` needs per-agency isolation

Pattern matches the existing 5.17e-6 splices (`StrategySystem`, `ProgressTracking`) and the FFT sketch from [near-future-and-far-future.md](near-future-and-far-future.md).

1. **New `AgencyState` fields** (mod-namespaced for forward compatibility):
   ```csharp
   /// <summary>
   /// Per-agency asteroid diminishing-returns tracking sourced from
   /// DMagic Orbital Science. Keyed by DMagic's per-asteroid identifier
   /// (typically the asteroid name / part flightID composite). Owned by
   /// AgencyDMagicRouter; projected via the DMScienceScenario splice.
   /// </summary>
   public Dictionary<string, AgencyDMagicAsteroidEntry> DMagicAsteroidScience { get; }
       = new Dictionary<string, AgencyDMagicAsteroidEntry>(StringComparer.Ordinal);

   /// <summary>
   /// Per-agency discovered anomalies, keyed by celestial body index
   /// and anomaly name composite.
   /// </summary>
   public Dictionary<string, AgencyDMagicAnomalyEntry> DMagicAnomalies { get; }
       = new Dictionary<string, AgencyDMagicAnomalyEntry>(StringComparer.Ordinal);
   ```
   Entry types mirror DMagic's persisted shape (asteroid: title + base/sci/accumulated/cap; anomaly: name + lat/lon/altitude + body index).

2. **New router**: `Server/System/Agency/AgencyDMagicRouter.cs`. Intercepts inbound scenario writes to `DMScienceScenario` and splits them into per-agency entries. Mirrors `AgencyContractRouter.TryRoute` shape.

3. **New projector entry**: add `"DMScienceScenario"` to `AgencyScenarioProjector.CareerScenarios`; route to `SpliceDMagicScienceIntoScenario(serializedText, targetAgency)`. The splice strips the shared scenario's content and re-emits the agency's own asteroid + anomaly entries.

4. **Dual-mode silence**: gate=off → router returns false → existing shared-scenario path runs unchanged.

5. **Test**:
   - Two agencies with DMagic installed under `PerAgencyCareer=on`.
   - Agency A captures asteroid X for full diminishing-returns. Agency B visits X — receives full diminishing-returns independently.
   - Agency A discovers anomaly Y. Agency B's anomaly UI does not show Y as known.
   - Gate=off: behaviour matches stock Luna MP — shared pool.

Estimated scope: similar parity with `SpliceAgencyStrategiesIntoScenario` (~80 lines router, ~100 lines projector splice given two child collections, plus two `AgencyState` field types + `ServerTest` pinning).

### Alternative — defer until DMagic in active modlist

If DMagic isn't a Phase-1 mod for the playtest, log the leak and re-evaluate when the modlist freezes.

---

## Tests

1. **Asteroid science isolation** (gate=on): two agencies; agency A milks asteroid X; agency B visits X and gets full science. **Today: fails.**
2. **Anomaly record isolation** (gate=on): agency A discovers anomaly Y; agency B's UI does not pre-mark Y. **Today: fails.**
3. DMagic CC contract per-agency lifecycle: agency A accepts a DMagic contract; agency B's mission control does not show it as theirs. **Today: passes** (covered by `AgencyContractRouter` Q6).
4. DMagic experiment science subject per-agency archive: agency A transmits a magnetometer report; agency B's archive does not gain that subject. **Today: passes** (covered by `AgencyResearchRouter`).
5. DMagic CC PARAM with PascalCase keys: malformed payload stripped cleanly. **Today: passes** (OrdinalIgnoreCase BodyContextKeys).

---

## Tracking

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **DMagic upstream:** `DMagic1/Orbital-Science`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** `LmpClient/Systems/Scenario/ScenarioSystem.cs::BodyIndexKeys`/`BodyContextKeys` (verified OrdinalIgnoreCase — DMagic's PascalCase keys match), `Server/System/Agency/AgencyContractRouter.cs` (Q6 shared-vs-per-agency for DMagic contracts), `Server/System/Agency/AgencyResearchRouter.cs::TryRouteScienceSubject` (covers DMagic experiment subjects).
- **DMagic files inspected:** `Source/` file inventory + subfolder listings, `Source/Scenario/DMScienceScenario.cs` (KSPScenario + persisted asteroid+anomaly shape), `Source/Parameters/` file inventory (8 CC parameter types), `Source/Parameters/DMCollectScience.cs` (key naming).
- **Findings this pass:**
  1. DMagic CC contracts + experiment science subjects are **already correctly per-agency-routed** by existing Stage-5 plumbing — no new code for those surfaces.
  2. DMagic's PascalCase CC param keys are **already validated** by OrdinalIgnoreCase BodyContextKeys.
  3. **`DMScienceScenario` ships shared asteroid-science + anomaly records** — same architectural pattern as SCANsat coverage and FFT antimatter factory. Full implementation sketch above.
- **Gaps still open (product calls):** all resolved 2026-05-18 — see below.

### Decisions ratified — 2026-05-18 (extended 2026-05-19 re-walk)

| Question | Answer |
|----------|--------|
| Should `DMScienceScenario` (asteroid science + anomaly records) be per-agency isolated? | **Yes — apply the SCANsat per-agency precedent.** Build the implementation sketch in the body of this doc as `Server/System/Agency/AgencyDMagicRouter.cs` + new `AgencyState.DMagicAsteroidScience` + new `AgencyState.DMagicAnomalies` + new projector entry for `DMScienceScenario`. Each agency starts with empty asteroid-science and empty anomaly records on first connect. |
| §A Asteroid science field types (2026-05-19 re-walk) | **float, not double** for all four numeric fields (`bsv` / `scv` / `sci` / `cap`). Verified by direct read of `DMScienceData.cs:39-40` (`private float scival, science, cap, basevalue;`) + `DMScienceScenario.OnLoad:152-155` (`float bsv = scienceResults_node.parse("bsv", (float)1);`). |
| §B Anomaly wire shape (2026-05-19 re-walk) | **2-level nested**, NOT flat composite-key. Per-body `DM_Anomaly_List` wrapper carries the `Body` flightGlobalsIndex; per-anomaly `DM_Anomaly` children carry Name + Lat/Lon/Alt. Verified by direct read of `DMScienceScenario.OnSave:92-119`. The agency-side AgencyState dict CAN still use a flat composite-key (e.g. `"$bodyIndex|$name"`) for storage simplicity — the projector reconstructs the nested wire shape on emit by grouping per-body. |
| §C Anomaly numeric serialization format (2026-05-19 re-walk) | **`"N5"` format on serialize**, `parse(...(double)0)` on read. The "N5" specifier is **culture-sensitive** under stock KSP — under a comma-decimal server locale, `0.12345.ToString("N5")` produces `"0,12345"` which the projector splice MUST round-trip without corrupting. Per **Invariant 9** (BUG-013), the projector's emit MUST force `InvariantCulture` regardless of what stock DMagic does. |
| §D `DMAnomalyObject.cs` location (2026-05-19 re-walk) | Lives at `Source/DMAnomalyObject.cs` (root of Source folder), NOT `Source/Scenario/DMAnomalyObject.cs` as the original audit claimed. Cosmetic correction — file is in the compiled csproj at line 101. |
| §E Companion event-watcher safety (2026-05-19 re-walk) | `DMRecoveryWatcher` + `DMTransmissionWatcher` subscribe to `GameEvents.OnScienceRecieved`. Both call `DMScienceScenario.SciScenario.submitDMScience(...)` which mutates the asteroid-science dict. **Per-agency relevance**: the fork-side router intercepts the `RawConfigNodeInsertOrUpdate` ingress, NOT these client-side watchers; the watchers fire on the local client, mutate the local `DMScienceScenario`, and the next periodic SHA pass broadcasts the result. Under Path B suppression the server's shared scenario stays frozen at operator seed — the per-agency state catches the broadcast at router-ingress time. No additional fork-side hook required for the watchers. |

Implementation slice: see [implementation-spec.md](implementation-spec.md) §S4.

### Re-walked 2026-05-19 — verified findings

Walked branch `master` HEAD `a4e805b9f819692a1546b741683da64b783694fe` against local shallow clone at `F:/tmp/mks-external/DMagicOrbitalScience` per the [[reference-mks-external-clones]] precedent.

**csproj Compile-list orphan check (PASS).** `Source/DMagicOrbital.csproj` enumerates 62 compiled source files. `DMScienceScenario.cs` is at line 129, `DMScienceData.cs` is at line 128, `DMAnomalyObject.cs` is at line 101. Grepping the compiled set for `: ScenarioModule` returns exactly ONE match — `DMScienceScenario : ScenarioModule` at `Source/Scenario/DMScienceScenario.cs:41`. **No FFT-style orphan.**

**Singleton dependency check (PASS).** Unlike FFT's phantom `AntimatterFactory.Instance`, DMScienceScenario's persistence does NOT delegate to any class outside the compiled source tree. All referenced classes (`DMScienceData`, `DMAnomalyList`, `DMAnomalyStorage`, `DMAnomalyObject`) are defined in the same repo and in the csproj Compile list.

**Verbatim OnSave/OnLoad shape** (file paths into the local clone):
- `Source/Scenario/DMScienceScenario.cs:68-122` (OnSave) builds two top-level containers (`Asteroid_Science` + `Anomaly_Records`). Asteroid block iterates `recoveredDMScience.Values` and emits one `DM_Science` child per dict entry; anomaly block iterates `DMAnomalyList` storage and emits nested `DM_Anomaly_List`/`DM_Anomaly` children.
- `Source/Scenario/DMScienceScenario.cs:124-182` (OnLoad) inverse — reads `Asteroid_Science → DM_Science` children + `Anomaly_Records → DM_Anomaly_List → DM_Anomaly` nested children.

**Side-channel persistence search:**
- `File.WriteAllText` / `File.WriteAllBytes` / `StreamWriter` / `FileStream`: zero matches.
- `GameEvents.onGameStateSaved` subscriptions: zero matches.
- Watcher classes (`DMRecoveryWatcher`, `DMTransmissionWatcher`) subscribe only to `GameEvents.OnScienceRecieved`; they mutate the scenario dict but never write to disk outside KSP's standard ScenarioModule.OnSave hook.

**CC parameter inventory verification** (per the audit's 8-parameter list): all 8 verified in `Source/DMagicOrbital.csproj`'s Compile list. All extend `ContractParameter` (CC API base class) — audit was correct. No fork-side change needed — already routed by `AgencyContractRouter`.

**PartModule inventory** (`Source/Part Modules/` folder + root-level part files): standard `[KSPField(isPersistant=true)]` annotations + a handful of custom OnSave/OnLoad overrides confined to `ScienceData` containers (per-vessel transient science). All vessel-proto-rideable under `lmpOwningAgency` from Stage 5.16b. No fork-side action.
