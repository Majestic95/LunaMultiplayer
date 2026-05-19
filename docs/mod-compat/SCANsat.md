# SCANsat — compat layer analysis

SCANsat spans **orbit parts**, **background scanning**, **planet-wide coverage persistence**, and **Contract Configurator (CC)** contract parameters — different layers interact differently with Luna MP and Luna Compat.

---

## What Luna Compat already owns

[Luna Compat](https://github.com/TheXankriegor/LunaCompat) lists SCANsat under:

- **Harmony patches** — sync active scanners, background scanning, progress.
- **Part module sync** — SCANsat modules appear in Part Sync list.
- **Server plugin** — SCANsat marks the server-plugin column (install `LunaCompatServerPlugin`; configure under `Universe\LunaCompat\ModSettingsStructure.xml` per Luna Compat docs).

**Default stance for this fork:** treat SCANsat vessel/module/network behaviour as **Luna Compat’s problem space** unless a fork regression proves otherwise.

---

## What this Luna MP fork already touches (upstream-adjacent)

### CC contract PARAMETER hygiene (client)

`LmpClient/Systems/Scenario/ScenarioSystem.cs` treats SCANsat-ish parameter keys (`coverage`, `scanType`, `scanMode`) as body-context signals for **Contract Configurator** payloads. Missing `targetBody` / body keys next to those fields can crash CC loaders; the fork defends against malformed blobs.

Design implication: SCANsat-heavy CC contracts riding the **Offered/shared** scenario pool remain sensitive to sanitisation whenever **scenario text** originates from heterogeneous clients or tooling (e.g. planet packs, malformed saves).

Reference keys in code: **`BodyContextKeys`** includes SCANsat-related keys beside other CC orbital/science keys.

---

## Per-agency career (Stage 5) interaction

### Contract routing (`AgencyContractRouter`)

Offered / Generated contracts remain on the **shared** scenario path so **`ContractPreLoader`** remains consistent (`AgencyContractRouter` Q6 commitments). Active and post-accept states are **per-agency**.

**Risk surface for SCANsat contracts**

- If a SCANsat/CC contract crosses from Offered → Active, progression may depend on scan state that Luna Compat syncs vessel-side. Align **acceptance semantics** across agencies — two players must not silently diverge scan completion if only one vessel holds the authoritative scanner snapshot.
- If **scenario projection** for `ContractSystem` is incomplete for a milestone (see projector comments regarding deferred splice vs agency wire echo), contract UI may diverge temporarily from canonical server state until client mirror milestones land. Re-check `AgencyScenarioProjector.cs` header comments whenever testing SCANsat chains.

### Scenario projection (`AgencyScenarioProjector`)

Scenarios this projector currently rewrites on send (verified against `Server/System/Agency/AgencyScenarioProjector.cs::CareerScenarios`):

| Scenario module | Projection shape |
|-----------------|------------------|
| `Funding` | `funds` root scalar regex replace |
| `ResearchAndDevelopment` | `sci` regex + strip-and-splice of `Tech` / `Science` / `ExpParts` child nodes + merge per-agency `PurchasedParts` as `part = X` values inside each per-agency Tech block |
| `Reputation` | `rep` root scalar regex replace |
| `StrategySystem` | Strip `STRATEGIES → STRATEGY` children, splice per-agency `AgencyState.Strategies` |
| `ProgressTracking` | Strip ALL `Progress` children, splice per-agency `AgencyState.Achievements` |
| `ScenarioUpgradeableFacilities` | Per-facility `lvl` override using `AgencyState.FacilityLevels` |
| `ContractSystem` | Stage 5.18d slice (j): keep shared-pool `CONTRACTS` entries whose state ∈ {`Offered`, `Generated`}, strip the rest, partition per-agency `AgencyState.Contracts` by state (`Active` → `CONTRACTS`; everything else → `CONTRACTS_FINISHED`) |

The `ContractSystem` line was the only deferred surface listed in this doc’s prior pass — that slice has now landed (`SpliceAgencyContractsIntoScenario`), in addition to the per-agency wire echo via `AgencyContractMsgData` that 5.17d shipped.

**Resolved 2026-05-18; implementation deferred to Stage 5 S2 slice.** Coverage lives in `SCANcontroller`'s OnSave/OnLoad as `Progress → Body` children carrying the opaque `Map` bitmap blob (corrected from earlier wording "per-body `Progress` child nodes" — see "Re-walked 2026-05-19" below). Pre-S2, `SCANcontroller` is NOT in `AgencyScenarioProjector.CareerScenarios`; every agency receives shared coverage. Decision §1 (Decisions table below) makes coverage per-agency once S2 ships.

---

## Mod-source walk (SCANsat repo `KSPModStewards/SCANsat`)

Walked branch `master` via WebFetch on 2026-05-18. **Re-walked 2026-05-19 against local shallow clone** at `F:/tmp/mks-external/SCANsat` (commit `0d67371`) — corrections from the re-walk are inlined below; obsolete claims from the original pass are struck through where they would mislead future readers. The structural facts now in the "Re-walked 2026-05-19" subsection are the **authoritative spec source for the S2 implementation**; downstream slices (S3 FFT, S4 DMagic) follow the same audit-via-prespec discipline against their own sources.

### Re-walked 2026-05-19 — verified blob structure

Authoritative ground truth, cited line numbers reference `F:/tmp/mks-external/SCANsat` at SHA `0d67371`.

**SCANcontroller is the only ScenarioModule SCANsat ships.** The other class names that turn up in a broad grep:
- `SCANquickload.cs:15` — `Debug_AutoLoadPersistentSaveOnStartup : MonoBehaviour`, gated by `#if DEBUG`. Not a ScenarioModule.
- `SCAN_UI/SCANsatRPM.cs:31` — `JSISCANsatRPM : InternalModule`, RasterPropMonitor IVA integration. Not a ScenarioModule.

So all SCANsat scenario-side persistence lives in `SCANcontroller.OnSave` (`SCANsat/SCANcontroller.cs:783-865`) / `OnLoad` (`:605-781`).

**Three root containers** at the `SCANcontroller` blob level:

```
SCANcontroller {
  [~30 KSPField root-level UI scalars: mainMapVisible, bigMapColor, zoomMapType, overlaySelection, ...]
  Scanners {
    Vessel { guid=..., name=..., Sensor { type=..., fov=..., min_alt=..., max_alt=..., best_alt=..., require_light=... } ...N sensors }
    Vessel { ... }
  }
  Progress {
    Body { Name=..., Map=<opaque>, Disabled=..., MinHeightRange=..., MaxHeightRange=..., ClampHeight=...?, PaletteName=..., PaletteSize=..., PaletteReverse=..., PaletteDiscrete=..., LandingTarget=...? }
    Body { ... }
  }
  SCANResources {
    ResourceType { Resource=..., MinColor=..., MaxColor=..., Transparency=..., MinMaxValues="bodyIndex|min|max,bodyIndex|min|max,..." }
    ResourceType { ... }
  }
}
```

Key facts the audit's first pass got wrong or omitted:

1. **`Progress` is a SINGLE container with `Body` children**, not "multiple `Progress` nodes at root level, one per body." The 2026-05-18 audit's wording "per-body `Progress` child nodes" was the right description, but the spec consumer (`implementation-spec.md` S2 section) read it ambiguously. Verbatim source: `node.AddNode(node_progress)` after a loop builds `Body` children into `node_progress` (`SCANcontroller.cs:842`).

2. **`Scanners → Vessel → Sensor` is three-level nested**, not two. Each `Vessel` node holds 1-N `Sensor` children (a single vessel can carry an altimetry sensor AND a survey sensor AND a resource scanner simultaneously). Verbatim: `foreach (SCANsensor sensor in sv.sensors) { ConfigNode node_sensor = new ConfigNode("Sensor"); ... node_vessel.AddNode(node_sensor); }` (`SCANcontroller.cs:797-806`). The original audit's row for the Scanners surface said "keyed by vessel GUID (sensor type, FOV, altitude range)" which flattened the nested Sensor records into the Vessel scalar fields — implementation-wrong.

3. **`SCANResources` is a third root container the audit didn't enumerate.** Contains `ResourceType` children: `Resource` name + display config (`MinColor`/`MaxColor`/`Transparency`) + `MinMaxValues` (`SCANcontroller.cs:847-861`). `MinMaxValues` is a pipe-and-comma-delimited string of `bodyIndex|min|max` tuples produced by `saveResources(SCANresourceGlobal r)` (`SCANcontroller.cs:2194-2208`) and parsed by `loadCustomResourceValues(ConfigNode node)` (`:2210-2289`). These values are body-resource display ranges, not player-discovered amounts (they're set from `SCANresourceBody.MinValue`/`MaxValue` which are defaulted from the resource config and operator-mutable via the SCANsat UI). **Treat as shared.**

4. **Field name corrections** vs the 2026-05-18 audit's working assumptions:
   - Body child uses `Name` (the celestial body name string), not `BodyName`.
   - Body child uses `Map` (the opaque coverage blob), not `body_scan`. `Map` is the output of `SCANdata.shortSerialize()` — Base64-encoded CLZF2-compressed `BinaryFormatter`-serialized `Int16[360,180]` with `/`→`-` and `=`→`_` URL-safe substitution (`SCAN_Data/SCANdata.cs:1020-1028`). Round-trip safe and opaque from the LMP side — we round-trip it as a string, never decode it.
   - `LandingTarget` is ONE string `"lat,lon"` (e.g. `"12.3400,-45.6700"`), not separate `LandingTargetLatitude` / `LandingTargetLongitude` fields. Emitted only when MechJeb is loaded AND a vessel waypoint exists (`SCANcontroller.cs:823-828`).
   - `ClampHeight` is emitted only when `body_scan.TerrainConfig.ClampTerrain` is non-null (`SCANcontroller.cs:834-836`). Optional on every body.
   - Sensor field names are lowercase-underscore: `type`, `fov`, `min_alt`, `max_alt`, `best_alt`, `require_light` (not PascalCase). Vessel uses lowercase `guid` and `name`.

5. **30+ KSPField root-level UI scalars on `SCANcontroller`** (`:52-121`) — `mainMapVisible`, `bigMapColor`, `zoomMapType`, `overlaySelection`, etc. These persist via KSP's stock KSPField mechanism at the ScenarioModule root level (NOT inside any child container) when KSP serialises the scenario. They are pure UI preferences (window visibility, palette selection, projection mode); they are not player-progress. **Treat as shared.** The per-agency design intentionally does not partition them; under gate=on every client sees whatever the operator-seed scenario contained, plus their own runtime mutations get suppressed by the router (Path B). Tradeoff: minor visual difference between clients vs gate=off — acceptable.

6. **`SCAN_Settings_Config`** (`SCANsat/SCAN_Settings_Config.cs`) is an EXTERNAL config file at `GameData/SCANsat/PluginData/Settings.cfg`, not part of any scenario blob. It hooks `GameEvents.onGameStateSaved` to write the file, but the file lives outside the savegame entirely. Untouched by Stage 5 per-agency design.

7. **SCANsat ships ZERO Contract Configurator contracts** — confirmed by repo scan: no `ContractParameter` subclass, no CC config files. The `BodyContextKeys` sanitisation in `LmpClient/Systems/Scenario/ScenarioSystem.cs` defends against the external `CC_SCANsat` pack. Per-agency contract routing under `AgencyContractRouter` already handles SCANsat-pack contracts via the generic Q6 path.

8. **PartModule persistence is bottle-shaped via vessel-proto** and does not need fork-side intervention:
   - `SCANsat : PartModule, IScienceDataContainer` (`SCAN_PartModules/SCANsat.cs`): one `[KSPField(isPersistant)] bool scanning`. Rides vessel-proto.
   - `ModuleSCANresourceScanner : SCANsat` (`SCAN_PartModules/SCANresourceScanner.cs`): inherits `scanning`. No additional persistent fields.
   - `SCANexperiment` (`SCAN_PartModules/SCANexperiment.cs`): persists `List<ScienceData>` via stock `ScienceData.Save`. Rides vessel-proto.
   - `SCANresourceDisplay` (`SCAN_PartModules/SCANresourceDisplay.cs`): no persistent fields.
   - `SCANRPMStorage` (`SCAN_PartModules/SCANRPMStorage.cs`): persists `SCANsatRPM → Prop` children (per-IVA-prop map state). Rides vessel-proto. UI-only state — does not impact per-agency partitioning.

### State inventory

> **Note (2026-05-19 re-walk).** The SCANcontroller persistence-shape cell was rewritten against verbatim source; the prior wording flattened multi-Sensor nesting and missed the third root container (`SCANResources`). See the "Re-walked 2026-05-19" subsection above for the authoritative description; the row below is a one-line summary.

| Class / file | Role | Persistence shape |
|--------------|------|--------------------|
| `SCANsat/SCANcontroller.cs` (`SCANcontroller : ScenarioModule`) | Single global scenario, `[KSPScenario(AddToAllGames \| AddToExistingGames, FLIGHT, SPACECENTER, TRACKSTATION)]` — the ONLY ScenarioModule SCANsat ships. | Three root containers: `Scanners → Vessel → Sensor` (3-level, 1-N sensors per vessel), `Progress → Body` (with opaque `Map` bitmap), `SCANResources → ResourceType` (display config — shared). Plus ~30 root-level KSPField UI scalars. See "Re-walked 2026-05-19" subsection for verbatim source citations + the per-field structural reference S2 implementation builds against. |
| `SCANsat/SCANsat.cs` (`SCANsat : PartModule, IScienceDataContainer`) | Scanner sensor PartModule | One `[KSPField(isPersistant = true)] bool scanning`. PartModule-side `OnLoad/OnSave` persist a `List<ScienceData> storedData` (held science before transmit/recover). Transparent to LMP via vessel-proto. |
| `SCANsat/SCAN_PartModules/SCANresourceScanner.cs` (`ModuleSCANresourceScanner : SCANsat, IAnimatedModule`) | Resource-flavor sensor part | No additional `isPersistant` fields beyond what `SCANsat` adds — inherits the `scanning` bool. |
| `SCANsat/SCAN_PartModules/SCANexperiment.cs` (`SCANexperiment : PartModule, IScienceDataContainer`) | Science experiment PartModule | Persists `List<ScienceData>` via stock `ScienceData.Save`. Rides vessel-proto. |
| `SCANsat/SCAN_PartModules/SCANRPMStorage.cs` (`SCANRPMStorage : PartModule`) | RasterPropMonitor IVA prop state | Persists `SCANsatRPM → Prop` children (per-IVA-prop UI state). Rides vessel-proto; UI-only. |
| `SCANsat/SCAN_PartModules/SCANresourceDisplay.cs` | UI-only display module | No persistent fields. |
| `SCANsat/SCAN_Data/SCANdata.cs` | In-memory coverage / data class | Holds the live `Int16[360,180]` bitmap that gets `shortSerialize()`'d on save (Base64-CLZF2-BinaryFormatter blob with URL-safe char escaping). Not directly persisted — round-trips through `SCANcontroller`. |
| `SCANsat/SCAN_Data/{SCANanomaly, SCANresourceBody, SCANterrainConfig, SCANwaypoint, SCANtype, SCANresourceGlobal, SCANresourceType, SCANROC, SCANexperimentType}.cs` | Per-body / per-resource / per-anomaly support types | All consumed through `SCANcontroller`'s scenario blob — no independent save targets. |
| `SCANsat/SCANquickload.cs` (`Debug_AutoLoadPersistentSaveOnStartup : MonoBehaviour`) | DEBUG-only `[KSPAddon]`, gated by `#if DEBUG` | NOT a ScenarioModule. No persistence surface. |
| `SCANsat/SCAN_UI/SCANsatRPM.cs` (`JSISCANsatRPM : InternalModule`) | RPM IVA integration | NOT a ScenarioModule. Persists via `SCANRPMStorage` PartModule above. |
| `SCANsat/SCAN_UI/*` (other) | GUI windows | Purely local; preferences fold back into `SCANcontroller`'s KSPField UI scalars. |
| `SCANsat/SCAN_Settings_Config.cs` | `[KSPAddon(MainMenu, true)]` singleton; writes external `GameData/SCANsat/PluginData/Settings.cfg` on `GameEvents.onGameStateSaved`. | NOT a scenario surface. Outside savegame. Untouched by Stage 5 work. |

**No `Contracts/` subfolder in the SCANsat repo.** SCANsat's README says "Contract Configurator contracts for scanning each planet" but ships them via a separate community CC pack (the historical [`SCANsatContracts` pack](https://forum.kerbalspaceprogram.com/topic/72679-1125-scansat-v211-real-scanning-real-science-at-warp-speed-september-20-2025/) line). That means **the `coverage` / `scanType` / `scanMode` keys in this fork's `BodyContextKeys` are sanitising payloads produced by an *external* CC mod, not SCANsat itself.** Operators expecting SCANsat-themed contracts must install the CC pack separately and the modlist must include it — this is identical to the OPM modlist-uniformity rule.

### Network surfaces — what would Luna MP need to relay?

Cross-walked against this fork's wire layer:

| SCANsat surface | Current Luna MP routing | Per-agency impact |
|------------------|--------------------------|--------------------|
| `SCANcontroller` scenario blob | Goes through the regular `ScenarioStoreSystem.CurrentScenarios` relay (`Server/System/ScenarioSystem.cs::SendScenarioModules`). Server reads + writes via `ScenarioDataUpdater.RawConfigNodeInsertOrUpdate`. | **Not in `AgencyScenarioProjector.CareerScenarios`.** All agencies receive the same coverage bitmaps + the same vessel-GUID-keyed Scanners set. |
| PartModule `scanning` bool, science container | Vessel proto sync — replicated through standard vessel sync, no special handling. | Vessel ownership is already per-agency in 5.18b via `lmpOwningAgency`; the *scanner* travels with the vessel, but the *coverage it produces* lives in the shared scenario above. |
| Background scan progress increment | Run client-side by SCANsat; mutates `SCANcontroller`'s in-memory `SCANdata`. On next `SendScenarioModules` tick the new bitmap arrives at the server, which overwrites the canonical scenario. | Last-writer-wins on the shared scenario — two agencies scanning the same body race each other. Luna Compat's Harmony patch + server plugin's role is to mediate this; without those, the race is observable. |
| Vessel-GUID-keyed `Scanners` node | Saved/loaded through `SCANcontroller`. Vessels owned by agency A have their scanner state visible to agency B's `SCANcontroller` blob today. | If per-agency vessel privacy is desired (Stage 5.18d slice (i) hints at full-sync-on-reconnect for the relay-vs-store contract), the `Scanners` node would need parallel filtering at projection time. |

### Cross-walk against `AgencyState`

| `AgencyState` field | SCANsat surface that maps to it |
|----------------------|-----------------------------------|
| `Funds` / `Science` / `Reputation` | Indirect — SCANsat's experiments generate `ScienceData` that flows through the **stock** `ShareScienceSubject` path, which Stage 5.17e-5's `AgencyResearchRouter.TryRouteScienceSubject` already routes per-agency. No SCANsat-specific addition needed for science attribution; "experiment recovered on Eve" credits the recovering agency's `ScienceSubjects` regardless of who scanned it. |
| `ScienceSubjects` | As above — SCANsat's anomaly experiments funnel through `ScienceSubject` IDs (`SCANsatAltimetryLoRes@Kerbin`, etc.). Already covered. |
| `Contracts` | If the external SCANsat CC contract pack is installed, those contracts hit `AgencyContractRouter` like any other CC contract. Q6 commitments apply: Offered slots stay shared, post-Accept persists per-agency. **The contract's parameter (e.g. `SCANsatCoverage` requiring 80% coverage of Duna) reads from `SCANcontroller`'s shared bitmap on the accepting client.** That's the leak: two agencies who both accept "scan Duna" share progress through the bitmap even though their contract entries are per-agency. |
| `TechNodes`, `PurchasedParts`, `ExperimentalParts` | Not touched by SCANsat. |
| `Strategies`, `Achievements`, `FacilityLevels` | Not touched by SCANsat. |
| `Coverage` *(planned, S2 — Decision §1)* | Per-body coverage state, including `Map` blob + per-body palette/terrain UI prefs per Decision §8. Keyed by `BodyName` (string, Ordinal). Builds on this row's prior "no per-agency representation today" framing once S2 lands. |
| `Scanners` *(planned, S2 — Decision §1/§3)* | Per-vessel active-scanner records with nested `List<AgencyScannerSensorRecord>` per Decision §9. Keyed by `VesselId` (Guid). Migrates with vessel under `transferagency` per D3. |

### Failure modes

Concrete predictions for multiplayer behaviour, classified by likelihood + severity:

1. **Cross-agency contract progress leak through shared coverage** (likely; medium severity). Two agencies each accept a `SCANsatCoverage` contract for the same body; either's scanning progresses both. Source: shared `SCANcontroller` blob feeds the CC parameter's `OnUpdate` check. Mitigation requires per-agency coverage state.
2. **Race on `SCANcontroller` last-writer-wins** (likely without Luna Compat plugin; rare with it). Two clients send different `body_scan` bitmaps within one `RawConfigNodeInsertOrUpdate` window. Luna Compat's server plugin exists precisely to merge here.
3. **Vessel-GUID `Scanners` node staleness** (medium likely; low severity). When an agency-owned vessel is destroyed/lost, the `Scanners` entry persists in `SCANcontroller` until the owning client next saves. Cosmetic — no game-state divergence.
4. **CC pack mismatch produces malformed param blobs** (already mitigated). `BodyContextKeys` strips contracts that ship `coverage`/`scanType` without `targetBody`. Validated against `LmpClient/Systems/Scenario/ScenarioSystem.cs::FindMissingBodyReference`.
5. **Per-vessel `scanning` bool desync** (low likely; low severity). Vessel proto replication keeps the bool consistent on relay; can drift transiently under heavy lag but resolves on next vessel update.

### Compat layer recommendation

Re-stated per surface with the source walk grounding:

| Surface | Owner verdict |
|---------|----------------|
| Active / background scanner sync, scanner part progress, GUID `Scanners` node mediation | **Luna Compat (Harmony + server plugin)** — already shipped. No new fork code needed. |
| Malformed CC `coverage`/`scanType`/`scanMode` PARAMs | **Luna MP fork client** — already shipped in `ScenarioSystem.FindMissingBodyReference`. |
| Per-agency lifecycle of accepted SCANsat CC contracts (router-side) | **Luna MP fork server** — already covered by generic `AgencyContractRouter` via Q6. No SCANsat-specific code. |
| Shared vs per-agency planet coverage bitmap | **Resolved 2026-05-18 → per-agency** (Decision §1). S2 ships the `AgencyState.Coverage` field + `AgencyScanRouter` + projector splice for `SCANcontroller`. |
| `Scanners` node filtering by agency | **Resolved 2026-05-18 → per-agency-owned vessels** (Decision §3). S2's projector filters to the requesting agency's owned vessels; `transferagency` migrates vessel-keyed scanner records per D3. |

### Design questions raised by the source walk

All resolved — see the **Decisions ratified** table below. Summary for quick scan:

1. ~~Coverage scoping per-agency vs shared~~ — Decision §1 (per-agency).
2. ~~Per-agency layer vs baseline-plus-overlay~~ — Decision §2 (each agency starts at 0%; no shared baseline).
3. ~~Scanner-node filtering scope~~ — Decision §3 (per-agency-owned vessels only).
4. ~~External SCANsat CC pack as mandatory modlist entry~~ — resolved earlier; see [README.md](README.md) "Operator policy — modlist uniformity."
5. ~~Luna Compat plugin coordination~~ — Decision §5 (fork-side router takes precedence; operator disables LunaCompat's SCANsat server-plugin entry in `Universe\LunaCompat\ModSettingsStructure.xml` under `PerAgencyCareer=on`).
6. ~~`SCANResources` scope~~ (added 2026-05-19) — Decision §6 (shared, not partitioned).
7. ~~Root-level UI scalars scope~~ (added 2026-05-19) — Decision §7 (shared, frozen at operator seed under gate=on).
8. ~~Body-field scope: just `Map`, or all Body fields per-agency?~~ (added 2026-05-19) — Decision §8 (all Body fields).
9. ~~Multi-Sensor-per-Vessel representation~~ (added 2026-05-19) — Decision §9 (nested `List<AgencyScannerSensorRecord>` on `AgencyScannerEntry`).

---

## Recommended split of responsibilities

| Concern | Owner |
|---------|--------|
| Sync scanning parts, timers, orbit modules, RPC-style progress between clients | Luna Compat (Harmony + cfg + server plugin) |
| Malformed SCANsat CC contract nodes on ingest | Luna MP fork client (`ScenarioSystem` validation path) |
| Which agency owns an accepted SCANSat contract lifecycle | Luna MP fork server (`AgencyContractRouter` + future client mirror completeness) |
| Shared vs per-agency “world map uncovered %” semantics | **Resolved → per-agency** (Decision §1). S2 implementation slice ships the per-agency `AgencyState.Coverage` persistence; see [implementation-spec.md](implementation-spec.md) §S2. |

---

## Test plan checklist (later implementation)

With Luna Compat + this fork aligned on versions:

1. Fresh connect / disconnect — scanners resume consistently (existing Luna Compat goal).
2. Two agencies accept different SCANsat contract lines — expect no cross-agency leak of acceptance state (`PrivateAgencyResources=true`).
3. CC-only contract with `coverage`/`scanType` but missing `targetBody` — rejected or stripped cleanly (no popup storm).
4. Planet pack mismatch — legacy body index sanitisation triggers as designed.

---

## Tracking

| Item | Notes |
|------|--------|
| Last code walk | `ScenarioSystem.BodyContextKeys`, `AgencyContractRouter`, `AgencyScenarioProjector` |

### Last validated

- **Fork commit:** `3628da08` (2026-05-19 re-walk); `c36d6f97` (2026-05-18 original pass).
- **SCANsat upstream:** `KSPModStewards/SCANsat`, master branch HEAD `0d67371911e9cf9a8a08cc7f7e23a9cccb006dae`. Shallow clone at `F:/tmp/mks-external/SCANsat` per the `[[reference-mks-external-clones]]` precedent. Original 2026-05-18 audit was WebFetch-based; 2026-05-19 re-walk uses the local clone for thorough multi-file traversal.
- **Fork files re-read:** `LmpClient/Systems/Scenario/ScenarioSystem.cs` (BodyIndexKeys + BodyContextKeys + `FindMissingBodyReference`), `Server/System/Agency/AgencyContractRouter.cs` (SharedScenarioStates = {Offered, Generated}), `Server/System/Agency/AgencyScenarioProjector.cs` (CareerScenarios set + `SpliceAgencyContractsIntoScenario`), `Server/System/ScenarioSystem.cs` (`SendScenarioModules`), `LmpCommon/Message/Data/ModMsgData.cs` (relay shape).
- **SCANsat files inspected (2026-05-19 re-walk):** `SCANsat/SCANcontroller.cs:40-2289` (KSPScenario attribute + OnLoad/OnSave + KSPField root scalars + `saveResources`/`loadCustomResourceValues` helpers + GameEvents subscriptions), `SCAN_Data/SCANdata.cs:1020-1047` (`shortSerialize`/`shortDeserialize` blob format), `SCAN_PartModules/SCANsat.cs:52` (`bool scanning` persistent), `SCAN_PartModules/SCANresourceScanner.cs` (inherits SCANsat, no additional persistent fields), `SCAN_PartModules/SCANexperiment.cs:94-115` (ScienceData persistence — rides vessel-proto), `SCAN_PartModules/SCANRPMStorage.cs:25-73` (RPM IVA prop state — rides vessel-proto), `SCAN_PartModules/SCANresourceDisplay.cs` (no persistent fields), `SCANquickload.cs:15-71` (DEBUG-only KSPAddon, NOT a ScenarioModule), `SCAN_UI/SCANsatRPM.cs:31` (InternalModule, NOT a ScenarioModule), `SCAN_Settings_Config.cs:194-203` (external Settings.cfg, not a scenario surface), full grep for `ContractParameter`/`ContractConfigurator` (confirmed empty).
- **Corrections applied 2026-05-18:**
  1. `ContractSystem` projection is no longer deferred — slice (j) shipped (covered in previous pass).
  2. Coverage persistence question answered from source: lives in `SCANcontroller`'s OnSave `Progress` child nodes. `SCANcontroller` is not in `AgencyScenarioProjector.CareerScenarios`, so today's behaviour is shared-coverage-across-agencies.
  3. SCANsat itself ships no CC contract definitions; the BodyContextKeys protect against an *external* CC pack's payload. Operational implication: SCANsat CC pack must be in the frozen modlist if the operator advertises SCANsat contracts.
- **Corrections applied 2026-05-19 (this pass):**
  1. `Progress` is a SINGLE container with `Body` children; original audit's wording "per-body Progress child nodes" was reading-ambiguous and consumed wrong by the spec.
  2. `Scanners → Vessel → Sensor` is 3-level nested with 1-N sensors per vessel — the audit flattened nested Sensor records into Vessel scalars, which is implementation-wrong.
  3. `SCANResources` is a third root container not enumerated by the original audit (rule: shared, not partitioned per §6).
  4. Field-name corrections: `Name` (not `BodyName`), `Map` (not `body_scan`), single `LandingTarget` string (not split lat/lon), lowercase sensor field names, lowercase `guid`/`name` on Vessel.
  5. `ClampHeight` + `LandingTarget` are conditional emits (optional per body).
  6. 30+ root-level KSPField UI scalars are persisted at the ScenarioModule root by KSP (rule: shared per §7).
  7. SCANsat ships exactly ONE ScenarioModule (`SCANcontroller`). Other candidate classes (`SCANquickload`, `SCANsatRPM`) are NOT ScenarioModules — DEBUG-only `KSPAddon` and `InternalModule` respectively. The 2026-05-18 audit row for `SCANsat/SCANsat.cs` was correct on the PartModule side; the rest of the enumeration here is supplemental.
- **Gaps still open (product calls, not research):** all resolved 2026-05-18 / 2026-05-19 — see Decisions table.

### Decisions ratified — 2026-05-18 (extended 2026-05-19)

| Question | Answer |
|----------|--------|
| §1 Coverage scoping | **Per-agency.** Each agency owns their scan progress independently. |
| §2 Layering on first connect | **Each agency starts at 0% on every body.** No baseline shared layer. |
| §3 Scanner-node filtering | **Filter to per-agency-owned vessels** (consistent with `lmpOwningAgency`). |
| §4 Modlist policy | (Resolved earlier) Mandatory + version-pinned per [README.md](README.md). |
| §6 SCANResources scope (2026-05-19 re-walk) | **Shared, NOT partitioned.** `MinMaxValues` is body-resource display range, not player-discovered amount; `MinColor`/`MaxColor`/`Transparency` are visualization config. The 2026-05-18 audit did not enumerate this container; the re-walk caught it. Pin: do not include `SCANResources` in the per-agency router/projector partition. |
| §7 Root UI scalars (2026-05-19 re-walk) | **Shared, frozen at operator seed.** The ~30 `KSPField` root-level scalars (`mainMapVisible`, `bigMapColor`, `zoomMapProjection`, etc.) are UI preferences. Under gate=on the router suppresses the shared-store write, so each client's runtime UI tweaks accumulate in their LOCAL `SCANcontroller` state but the SERVER's projection always serves the operator-seeded baseline scalars. Acceptable tradeoff: minor visual config difference between gate=on and gate=off; no operator pain. |
| §8 Body-level scope (2026-05-19 re-walk) | **All Body fields per-agency, not just `Map`.** The `Disabled` / `MinHeightRange` / `MaxHeightRange` / `ClampHeight` / palette fields are runtime-mutable per-player UI preferences (set via SCANsat's body-config UI). Partitioning the WHOLE `Body` child node — rather than carving out just `Map` — is simpler to reason about and the per-body wire-bytes overhead (~8 small strings × bodies × agencies) is trivial. |
| §9 Multi-Sensor-per-Vessel handling (2026-05-19 re-walk) | **`AgencyScannerEntry` carries `List<SensorRecord>` nested**, not flat. The original spec's flat fields (`SensorType`, `Fov`, `MinAlt`, `MaxAlt`) cannot represent a vessel running multiple sensors simultaneously — the audit-via-prespec catch that drove the re-walk. Sensor record is its own type: `{ SensorType, Fov, MinAlt, MaxAlt, BestAlt, RequireLight }`. |
| §5 Luna Compat plugin coordination | **Fork-side router takes precedence.** Operator policy: disable LunaCompat's SCANsat server plugin entry (`ModSettingsStructure.xml`) when `PerAgencyCareer` is on. No upstream coordination owed. |

Project-wide precedent: this answer also governs FFT antimatter factory ([near-future-and-far-future.md](near-future-and-far-future.md)) and DMagic asteroid science + anomaly records ([dmagic-orbital-science.md](dmagic-orbital-science.md)) — both share the same architectural shape.

Implementation slice: see [implementation-spec.md](implementation-spec.md) §SCANsat.
