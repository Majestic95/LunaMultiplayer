# Near Future Technologies + Far Future Technologies — compat layer analysis

**Identification.** A family of part packs by Nertea / `post-kerbin-mining-corporation`:

- **Near Future Electrical** ([repo](https://github.com/post-kerbin-mining-corporation/NearFutureElectrical)) — fission reactors, capacitors, radioisotope generators.
- **Near Future Propulsion** ([repo](https://github.com/post-kerbin-mining-corporation/NearFuturePropulsion)) — ion / VASIMR / electric thrusters.
- **Near Future Launch Vehicles** ([repo](https://github.com/post-kerbin-mining-corporation/NearFutureLaunchVehicles)) — large lifter parts.
- **Far Future Technologies** ([repo](https://github.com/post-kerbin-mining-corporation/FarFutureTechnologies)) — fusion engines, antimatter engines, mining rigs, **antimatter factories**.
- (Adjacent: Near Future Construction, Solar, Spacecraft, Exploration, Aeronautics — same architectural pattern, not in this audit batch but inherit the same verdict shape unless they ship a ScenarioModule.)

Most of the suite is **part packs with PartModule extensions** (Fission/Fusion/Ion behaviour, antimatter containment). **Far Future Technologies is the outlier** — it ships a `ScenarioModule` for global antimatter factory state.

---

## Luna Compat status

| Mod | In Luna Compat Harmony list? | In Part Sync list? | Server plugin flag? |
|-----|------------------------------|---------------------|---------------------|
| All Near Future mods | No | No (confirm against upstream) | No |
| Far Future Technologies | No | No | No |

None of the suite is listed in [Luna Compat](https://github.com/TheXankriegor/LunaCompat) ([overlap inventory](lunacompat-inventory.md)).

---

## State ownership

### Near Future Electrical (representative; other NF mods follow same pattern)

`Source/NearFutureElectrical/`:

| File | Inherits | Notes |
|------|----------|-------|
| `FissionReactor.cs`, `FissionEngine.cs`, `FissionGenerator.cs`, `FissionConsumer.cs`, `FissionFlowRadiator.cs` | `PartModule` (mostly via stock `ModuleResourceConverter` derivatives) | Reactor thermal + power simulation. Persistent state lives in stock-resource fields and PartModule `[KSPField(isPersistant)]`. |
| `DischargeCapacitor.cs`, `RadioisotopeGenerator.cs` | `PartModule` | Capacitor charge state + RTG depletion. |
| `RadioactiveStorageContainer.cs` | `PartModule` | Spent-fuel container; pure vessel-side resource state. |
| `ModuleCoreHeatNoCatchup.cs`, `ModuleMultiJettison.cs`, `ModuleUpdateOverride.cs` | `PartModule` | Stock-module-extension helpers. |

**No `ScenarioModule`** in NF Electrical. Confirmed against the file inventory.

Other NF mods follow the same pattern: PartModule packs + ModuleManager configs + textures. No save-game-level scenario state.

### Far Future Technologies

> **Re-walked 2026-05-19 against local clone at `F:/tmp/mks-external/FarFutureTechnologies` SHA `ad59fbb5` (current master tip, released 2024-10-18). Major finding:** the 2026-05-18 audit row marking `FarFutureTechnologyPersistence.cs` as a load-bearing ScenarioModule was wrong on a structural level. The file exists in the repo but is NOT in the `<Compile Include="..."/>` list of `Source/FarFutureTechnologies/FarFutureTechnologies.csproj` — it is an orphan dead source file. The compiled FFT.dll that operators ship contains NO ScenarioModule. The orphan also references two symbols that don't exist in the source tree (`FarFutureTechnologySettings.amFactoryConfigNodeName` and the entire `AntimatterFactory` class), so even reviving it would require operator hand-patching with hypothetical missing classes. See "Re-walked 2026-05-19" subsection below for verbatim verification. **Verdict overturn: FFT shipped today is in the "no work owed" category, joining KER / OPM / Trajectories / DPAI / TweakScale.**

`Source/FarFutureTechnologies/`:

| File | Inherits | Notes |
|------|----------|-------|
| ~~`FarFutureTechnologyPersistence.cs`~~ | ~~`ScenarioModule`~~ | **ORPHAN (re-walk 2026-05-19).** Not in csproj Compile list; not in shipped DLL. References `FarFutureTechnologySettings.amFactoryConfigNodeName` (undefined) + `AntimatterFactory` class (does not exist in source tree). The 2026-05-18 audit's central premise was that this file ships and persists global antimatter factory state — verified false. |
| `Modules/ModuleAntimatterTank.cs` | `PartModule` | Three `[KSPField(isPersistant)]`: `bool ContainmentEnabled`, `bool DetonationOccuring`, `float ContainmentCostCurrent`. Per-vessel antimatter containment. |
| `Modules/ModuleFusionEngine.cs`, `Modules/FusionReactor.cs`, `Modules/FusionReactorMode.cs` | `PartModule` | Fusion engine + reactor behaviour; persistent state via standard PartModule fields. |
| `Modules/ModuleChargeableEngine.cs` | `PartModule` | Charge-then-fire engine cycle. |
| `Modules/PulseEngine/` (subfolder) | `PartModule` | Pulse-engine variants (Daedalus / Project Orion style). |
| `FarFutureTechnologySettings.cs`, `FarFutureTechnologyGameSettings.cs` | KSP Game Settings nodes | Mod-wide configuration. Per-install. |

---

## This fork touchpoints

### Per-mod summary

| Mod | Touchpoint |
|-----|-------------|
| NF Electrical, Propulsion, Launch Vehicles | Vessel proto sync only. PartModule fields ride standard replication. **No fork code needed.** |
| FFT vessel-side modules (tanks, engines) | Vessel proto sync only. Same as NF. |
| **FFT `FarFutureTechnologyPersistence`** | **Save-game `ScenarioModule`** with player-progress-shaped state. **NOT in `AgencyScenarioProjector.CareerScenarios`** today. |

---

## Interaction with PerAgencyCareer

### Vessel-side modules (all NF mods + FFT PartModules)

**No career interaction.** Reactor fuel, capacitor charge, antimatter containment — all vessel-side, all per-agency-correct via `lmpOwningAgency` on the parent vessel.

### `FarFutureTechnologyPersistence` (the load-bearing concern)

The scenario module persists `AntimatterFactory.Instance` — a singleton tracking the player's antimatter production yield and stockpile. **This is conceptually agency-shaped state**: each agency should presumably own their own antimatter inventory, not share a global pool.

Under today's fork, this scenario module passes through `ProjectForClient` unchanged (it's not in `CareerScenarios`). Result: **antimatter factory inventory is shared across all agencies**. Antimatter produced by agency A is available to agency B. Identical leak pattern to SCANsat planet coverage.

---

## Failure modes (multiplayer)

1. **Cross-agency antimatter pool sharing** — see above. Likely the most visible per-agency leak in the Nertea suite.
2. **Concurrent factory upgrades** — if two clients both upgrade `AntimatterFactory.Instance` simultaneously (each thinks they're upgrading their own), the scenario blob arrives at the server twice with conflicting state. `ScenarioDataUpdater.RawConfigNodeInsertOrUpdate` last-writer-wins. Operator-visible if both clients are mid-upgrade.
3. **Fusion reactor per-vessel state** — clean. Vessel proto sync covers it.
4. **Antimatter tank containment loss** — per-vessel; if a remote vessel loses power, its antimatter tank detonates locally on whichever client has the vessel loaded. Standard vessel state propagation; no special handling.
5. **Reactor thermal simulation under physics rollback** — bounded by stock KSP behaviour; not Nertea-suite-specific.

---

## Proposed layering

| Surface | Owner verdict |
|---------|----------------|
| All Near Future PartModule packs | **No action.** Standard vessel proto sync covers. |
| FFT vessel-side modules | **No action.** Same as above. |
| **FFT `FarFutureTechnologyPersistence`** (global antimatter factory) | **Open product decision.** Two options below. |

### Implementation sketch — if FFT antimatter factory needs per-agency isolation

Pattern follows the existing 5.17e-6 `StrategySystem` / `ProgressTracking` projector entries.

1. **New `AgencyState` field**:
   ```csharp
   /// <summary>
   /// Per-agency state for third-party Far Future Technologies antimatter
   /// factory persistence (mod-namespaced). Owned by AgencyFarFutureRouter;
   /// projected back via AgencyScenarioProjector under the
   /// "FarFutureTechnologyPersistence" scenario name.
   /// </summary>
   public AgencyFarFutureFactoryEntry FarFutureFactory { get; set; }
   ```
   `AgencyFarFutureFactoryEntry` mirrors FFT's persisted shape: `int FactoryLevel`, `double AntimatterInventory`, `double DeferredConsumption`, `bool FirstLoad`.

2. **New router**: `Server/System/Agency/AgencyFarFutureRouter.cs`. Intercepts inbound scenario writes to `FarFutureTechnologyPersistence` (in `ScenarioDataUpdater.RawConfigNodeInsertOrUpdate`'s caller surface, or via a dedicated wire path if FFT exposes mutation events). Same shape as `AgencyContractRouter.TryRoute`.

3. **New projector entry**: add `"FarFutureTechnologyPersistence"` to `AgencyScenarioProjector.CareerScenarios`, route to `SpliceFarFutureFactoryIntoScenario(serializedText, targetAgency)`. The splice reads the per-agency entry and re-emits the FFT-expected node shape under `AMFactoryConfigNodeName`.

4. **Dual-mode silence**: under `PerAgencyEnabled=false`, the new code paths early-return; FFT operates exactly as in stock Luna MP today (shared global factory).

5. **Test**:
   - Two agencies on a Career server with `PerAgencyCareer=on` and FFT installed.
   - Agency A produces antimatter; agency B's inventory remains untouched.
   - Agency A's factory upgrade is invisible to agency B; agency B can independently upgrade their own.
   - Gate=off: behaviour matches stock Luna MP — single shared factory.

Estimated scope: similar to the `StrategySystem` / `ProgressTracking` projector entries (Stage 5.17e-6) — one router (~80 lines), one projector splice (~60 lines), one `AgencyState` field, one `ServerTest` pinning the splice.

### Alternative — defer until FFT in real play

If the project doesn't have an active FFT playtest planned, the leak is documentation-only and the implementation sketch above stays a backlog entry. Operator-visible behaviour is "antimatter factory is a shared/global feature" which is consistent with how the unmodded Luna MP treats it.

---

## Tests

1. NF Electrical: agency-A builds a fission reactor. Agency-B sees the reactor's power output on agency-A's vessel via standard proto sync. No cross-agency leak.
2. FFT antimatter tank: per-vessel containment state survives reconnect on the owning agency's client.
3. **FFT antimatter factory cross-agency**: with PerAgencyCareer=on, document whether agency A's antimatter production is visible/usable by agency B. **Today: yes (shared).** Product decision required.
4. NF Propulsion ion engine fuel consumption: per-vessel resource state, no per-agency surface.

---

## Tracking

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **Mod upstream:** `post-kerbin-mining-corporation/NearFutureElectrical` (`master`) and `post-kerbin-mining-corporation/FarFutureTechnologies` (`master`), WebFetch pass on 2026-05-18.
- **Fork files re-read:** `Server/System/Agency/AgencyScenarioProjector.cs` (`CareerScenarios` set + the Stage 5.17e-6 splice patterns referenced in the implementation sketch above).
- **Mod files inspected:**
  - NF Electrical: `Source/NearFutureElectrical/` file inventory (PartModule-only — no ScenarioModule).
  - FFT: `Source/FarFutureTechnologies/` file inventory; `FarFutureTechnologyPersistence.cs` (ScenarioModule attribute + persisted fields); `Modules/ModuleAntimatterTank.cs` (PartModule, three persistent fields).
- **Findings this pass:**
  1. The Near Future suite is uniformly PartModule packs with no save-game scenario state. Vessel proto sync handles everything.
  2. **Far Future Technologies ships a `ScenarioModule` for global antimatter factory state** — the only career-state-shaped surface in the suite. Identical leak pattern to SCANsat planet coverage.
  3. Full implementation sketch provided for per-agency antimatter factory isolation; estimated parity with the existing Stage 5.17e-6 splices.
- **Gaps still open (product calls):**
  - If the wider Near Future suite is expanded later to include System Heat or DynamicBatteryStorage as cross-vessel sims, those need separate audits.

### Decisions ratified — 2026-05-18 (S3 OVERTURNED 2026-05-19 re-walk)

| Question | Answer |
|----------|--------|
| ~~Should FFT `FarFutureTechnologyPersistence` (antimatter factory) be per-agency isolated?~~ | **OVERTURNED 2026-05-19**: the premise was structurally invalid. `FarFutureTechnologyPersistence.cs` is an orphan source file not compiled into FFT's DLL, and the supporting `AntimatterFactory` class + `amFactoryConfigNodeName` constant the orphan depends on don't exist in the source tree. Stock FFT therefore has no global antimatter factory state to partition. **Revised verdict: NO WORK OWED.** All real FFT state is per-PartModule (antimatter tank, fusion reactor, charge-up engine) which already rides vessel-proto sync correctly under `lmpOwningAgency`. |
| Near Future suite (non-FFT) per-agency interaction? | **No work owed.** Confirmed by the audit — NF mods are PartModule-only and ride vessel proto sync correctly. |

~~Implementation slice: see [implementation-spec.md](implementation-spec.md) §FFT antimatter factory.~~ — **No implementation slice owed; S3 retired.** See [implementation-spec.md](implementation-spec.md) §S3 for the retirement record.

### Re-walked 2026-05-19 — verified findings

Walked branch `master` HEAD `ad59fbb52a04db26732309ecf3941041f89001d0` (latest tagged release on FFT's public master, dated 2024-10-18). Local shallow clone at `F:/tmp/mks-external/FarFutureTechnologies` per the [[reference-mks-external-clones]] precedent.

**Confirmed by direct grep of `Source/FarFutureTechnologies/FarFutureTechnologies.csproj`:**

```xml
<Compile Include="FarFutureTechnologySettings.cs" />
<Compile Include="FarFutureTechnologyGameSettings.cs" />
<Compile Include="Modules\FusionReactorMode.cs" />
<Compile Include="Modules\FusionReactor.cs" />
<Compile Include="Modules\ModuleAntimatterTank.cs" />
<Compile Include="Modules\ModuleColorAnimator.cs" />
<Compile Include="Modules\ModuleEngineExhaustDamage.cs" />
<Compile Include="Modules\ModuleFusionEngine.cs" />
<Compile Include="Modules\ModuleChargeableEngine.cs" />
<Compile Include="Modules\PulseEngine\EnginePulseEffects.cs" />
<Compile Include="Modules\PulseEngine\ModulePulseEngineAnimator.cs" />
<Compile Include="Properties\AssemblyInfo.cs" />
<Compile Include="UI\AntimatterManager.cs" />
<Compile Include="UI\AntimatterWindow.cs" />
<Compile Include="UI\UILoader.cs" />
<Compile Include="Utils.cs" />
```

16 files compile into FFT.dll. `FarFutureTechnologyPersistence.cs` is absent from this list. Grepping the 16 compiled files for `: ScenarioModule` returns zero matches.

**The orphan would not compile if added back to the list.** Two dangling references:

1. `FarFutureTechnologySettings.amFactoryConfigNodeName` — the `FarFutureTechnologySettings` class (`FarFutureTechnologySettings.cs:21`) declares exactly five static fields (`DebugModules`, `MinimumExhaustBuildingDamagePerTick`, `MinimumPulseBuildingDamage`, `MinimumPulsePartsHeat`, `MinimumPulsePartsForce`). No `amFactoryConfigNodeName`. Not a `partial class`. Single definition in the entire repo.
2. `AntimatterFactory` — grep for `class AntimatterFactory` returns zero matches across the entire repo. The class does not exist.

Without these, even reviving the orphan into the csproj would produce build errors `CS0117: 'FarFutureTechnologySettings' does not contain a definition for 'amFactoryConfigNodeName'` + `CS0246: The type or namespace name 'AntimatterFactory' could not be found`.

**What the audit missed.** The 2026-05-18 audit was WebFetch-based and saw the orphan's `[KSPScenario]` attribute + the OnSave/OnLoad shape, then ratified "yes, partition this" without inspecting the build manifest. The audit-via-prespec discipline applied to the S3 implementation pre-spec (this re-walk) caught it before any code shipped.

**What FFT actually persists, by category:**

| Surface | Compiled? | Persistence shape | Per-agency status |
|---------|-----------|---------------------|---------------------|
| `ModuleAntimatterTank` PartModule | ✅ | 3 `[KSPField(isPersistant)]`: `ContainmentEnabled`, `DetonationOccuring`, `ContainmentCostCurrent` | Per-vessel — already partitioned via `lmpOwningAgency` |
| `FusionReactor` PartModule | ✅ | 6 `[KSPField(isPersistant)]`: `Enabled`, `CurrentModeID`, `Charged`, `Charging`, `CurrentCharge`, `ChargeRate` | Per-vessel — already partitioned |
| `ModuleFusionEngine` (inherits FusionReactor) | ✅ | (inherits) | Per-vessel — already partitioned |
| `ModuleChargeableEngine` PartModule | ✅ | 7 `[KSPField(isPersistant)]` (charge state, throttle bounds) | Per-vessel — already partitioned |
| `AntimatterManager` UI singleton | ✅ | Runtime-only; no OnSave/OnLoad/ConfigNode/GameEvents-onGameStateSaved | Not persisted — no concern |
| `FarFutureTechnologySettings` static class | ✅ | Loads from `GameData/FFT/FFTSETTINGS` config; not save-game state | Global config — not per-agency |
| `FarFutureTechnologiesSettings_*` `GameParameters.CustomParameterNode` (4 classes) | ✅ | Difficulty / balance settings, persist via KSP's built-in GameParameters | Per-game preferences — not per-agency |
| `FarFutureTechnologyPersistence` orphan + `AntimatterFactory` non-existent class | ❌ | (dead code) | N/A |

**Side-channel persistence search:**
- `File.WriteAllText` / `File.WriteAllBytes` / `StreamWriter` / `FileStream` across the compiled source: zero matches.
- `GameEvents.onGameStateSaved` subscriptions: zero matches.
- `GameEvents.OnVesselRollout` subscription in `ModuleAntimatterTank.cs:151` — transient UI handoff, no persistence.

**Operator caveat (low-probability):** if a custom patch or fork of FFT writes the missing `AntimatterFactory` class + the `amFactoryConfigNodeName` constant and re-adds the orphan to the csproj, that variant would persist global antimatter factory state and would leak across agencies. No such patch is documented anywhere I could find; the orphan has been broken in FFT's master at least through 2024-10-18. If an operator reports cross-agency antimatter visibility, revisit and consider implementing S3 against the patch variant — but that would be operator-cohort-specific code.
