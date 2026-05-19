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

**Open design question (now answered — see mod-source walk below).** Coverage lives in `SCANcontroller`'s OnSave/OnLoad as per-body `Progress` child nodes containing integer-serialised bitmaps. `SCANcontroller` is NOT in `AgencyScenarioProjector.CareerScenarios`, so today every agency receives the unioned coverage of all players. This is consistent behaviour, not a bug — but it is a product call worth surfacing explicitly before playtest.

Document your measured behaviour in the field under **both** gates:

- `PerAgencyCareer` off — baseline LMP shared career.
- `PerAgencyCareer` on — each agency isolation expectations.

---

## Mod-source walk (SCANsat repo `KSPModStewards/SCANsat`)

Walked branch `master` via WebFetch on 2026-05-18. Source file references below are paths into that repo, not into this fork.

### State inventory

| Class / file | Role | Persistence shape |
|--------------|------|--------------------|
| `SCANsat/SCANcontroller.cs` (`SCANcontroller : ScenarioModule`) | Single global scenario, `[KSPScenario(AddToAllGames \| AddToExistingGames, FLIGHT, SPACECENTER, TRACKSTATION)]` | OnSave writes: per-body `Progress` child nodes (`body_scan.integerSerialize()` coverage bitmap, terrain config, landing target lat/lon); a `Scanners` child node keyed by vessel GUID (sensor type, FOV, altitude range); a `SCANResources` child node with per-body min/max per resource; ~25 UI/preference scalars (colors, map size/projection, toolbar flags). Loaded back in OnLoad symmetrically. |
| `SCANsat/SCANsat.cs` (`SCANsat : PartModule, IScienceDataContainer`) | Scanner sensor PartModule | One `[KSPField(isPersistant = true)] bool scanning`. PartModule-side `OnLoad/OnSave` persist a `List<ScienceData> storedData` (held science before transmit/recover) plus RPM IVA prop config. |
| `SCANsat/SCANresourceScanner.cs` (`ModuleSCANresourceScanner : SCANsat, IAnimatedModule`) | Resource-flavor sensor part | No additional `isPersistant` fields beyond what `SCANsat` adds — inherits the `scanning` bool + science container behaviour. |
| `SCANsat/SCAN_Data/SCANdata.cs` | In-memory coverage / data class | Holds the live `Int32[]` bitmap that gets `integerSerialize()`'d on save. Not directly persisted — round-trips through `SCANcontroller`. |
| `SCANsat/SCAN_Data/{SCANanomaly, SCANresourceBody, SCANterrainConfig, SCANwaypoint, SCANtype, SCANresourceGlobal, SCANresourceType}.cs` | Per-body / per-resource / per-anomaly support types | All consumed through `SCANcontroller`'s scenario blob — no independent save targets. |
| `SCANsat/SCAN_UI/*` | GUI windows | Purely local; preferences fold back into `SCANcontroller`'s UI scalars. |

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
| (No field) | The per-body coverage bitmap. **There is no per-agency representation today.** A `CoverageBitmaps` (or equivalent) field would be the natural home if per-agency coverage is desired. |

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
| Shared vs per-agency planet coverage bitmap | **Open product decision** — see below. If per-agency: new `AgencyState.CoverageBitmaps` + projector splice for `SCANcontroller`. If shared (status quo): document explicitly so operators don't expect it. |
| GUID-keyed `Scanners` node filtering by agency | **Open product decision, downstream of the bitmap decision.** Filtering scanners without filtering coverage is asymmetric and confusing; keep these aligned. |

### Design questions raised by the source walk (for product call)

1. **Coverage scoping.** Should each agency see their own scanned bitmap of every body, or stay shared (current)? Shared makes "exploration is collective" and avoids divergence; per-agency makes science / CC contracts truly isolated. **No design code change recommended until this is answered.**
2. **If per-agency**: should reconnecting clients see *only their agency's* coverage, or a per-agency layer over a baseline shared layer? (The second has no current precedent in the fork; the first matches the strip-and-splice pattern of every other Stage 5.17e-6 surface.)
3. **Scanner-node filtering**: if per-agency coverage is adopted, should the `Scanners` node project per-agency-owned vessels only, or stay shared? Suggested: project per-agency-owned (matches `lmpOwningAgency` already on vessels).
4. ~~**External SCANsat CC pack as mandatory modlist entry**~~ — **resolved 2026-05-18.** Project-wide operator policy: all mods (including the external SCANsat CC pack) are mandatory and version-pinned for every joiner. See README.md "Operator policy — modlist uniformity" for the global rule. The fork's `BodyContextKeys` sanitisation remains in place as defence-in-depth, not as a supported operating state.
5. **Luna Compat plugin coordination**: if we adopt per-agency coverage, does the Luna Compat server plugin need an agency-aware mode (additional payload on its existing wire), or do we route SCANsat scenario projection entirely through this fork and disable that part of the Luna Compat plugin? Coordination with Luna Compat upstream required either way.

---

## Recommended split of responsibilities

| Concern | Owner |
|---------|--------|
| Sync scanning parts, timers, orbit modules, RPC-style progress between clients | Luna Compat (Harmony + cfg + server plugin) |
| Malformed SCANsat CC contract nodes on ingest | Luna MP fork client (`ScenarioSystem` validation path) |
| Which agency owns an accepted SCANSat contract lifecycle | Luna MP fork server (`AgencyContractRouter` + future client mirror completeness) |
| Shared vs per-agency “world map uncovered %” semantics | Design decision — possibly **explicit new persistence** if product requires divergence |

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

- **Fork commit:** `c36d6f97` (2026-05-18)
- **SCANsat upstream:** `KSPModStewards/SCANsat`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** `LmpClient/Systems/Scenario/ScenarioSystem.cs` (BodyIndexKeys + BodyContextKeys + `FindMissingBodyReference`), `Server/System/Agency/AgencyContractRouter.cs` (SharedScenarioStates = {Offered, Generated}), `Server/System/Agency/AgencyScenarioProjector.cs` (CareerScenarios set + `SpliceAgencyContractsIntoScenario`), `Server/System/ScenarioSystem.cs` (`SendScenarioModules`), `LmpCommon/Message/Data/ModMsgData.cs` (relay shape).
- **SCANsat files inspected:** `SCANsat/SCANcontroller.cs` (KSPScenario attribute + OnLoad/OnSave shape), `SCANsat/SCANsat.cs` (PartModule + persistent fields), `SCANsat/SCANresourceScanner.cs` (extends SCANsat with no extra isPersistant), top-level `SCANsat/SCAN_Data/` file inventory.
- **Corrections applied this pass:**
  1. `ContractSystem` projection is no longer deferred — slice (j) shipped (covered in previous pass).
  2. Coverage persistence question answered from source: lives in `SCANcontroller`'s OnSave `Progress` child nodes, `body_scan.integerSerialize()`. `SCANcontroller` is not in `AgencyScenarioProjector.CareerScenarios`, so today's behaviour is shared-coverage-across-agencies.
  3. SCANsat itself ships no CC contract definitions; the BodyContextKeys protect against an *external* CC pack's payload. Operational implication: SCANsat CC pack must be in the frozen modlist if the operator advertises SCANsat contracts.
- **Gaps still open (product calls, not research):** all resolved 2026-05-18 — see below.

### Decisions ratified — 2026-05-18

| Question | Answer |
|----------|--------|
| §1 Coverage scoping | **Per-agency.** Each agency owns their scan progress independently. |
| §2 Layering on first connect | **Each agency starts at 0% on every body.** No baseline shared layer. |
| §3 Scanner-node filtering | **Filter to per-agency-owned vessels** (consistent with `lmpOwningAgency`). |
| §4 Modlist policy | (Resolved earlier) Mandatory + version-pinned per [README.md](README.md). |
| §5 Luna Compat plugin coordination | **Fork-side router takes precedence.** Operator policy: disable LunaCompat's SCANsat server plugin entry (`ModSettingsStructure.xml`) when `PerAgencyCareer` is on. No upstream coordination owed. |

Project-wide precedent: this answer also governs FFT antimatter factory ([near-future-and-far-future.md](near-future-and-far-future.md)) and DMagic asteroid science + anomaly records ([dmagic-orbital-science.md](dmagic-orbital-science.md)) — both share the same architectural shape.

Implementation slice: see [implementation-spec.md](implementation-spec.md) §SCANsat.
