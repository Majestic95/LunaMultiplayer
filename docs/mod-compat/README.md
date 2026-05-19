# Mod compatibility layers (design notes)

This folder holds **fork-local design and analysis** for running third-party mods on a Luna MP server whose **per-agency career** extensions (Stage 5 / `PerAgencyCareer`) materially change scenario, contract, and career payload routing compared to upstream LMP.

## Goals

- Document **interaction points** in this repo (scenario projection, contracts, sanitisation).
- Decide what belongs **in core Luna MP**, in a **sidecar mod** (e.g. Luna Compat style), or in a **server plugin**.
- Avoid re-implementing work already covered by **[Luna Compat](https://github.com/TheXankriegor/LunaCompat)** unless the fork changes assumptions that patch needs to revisit.

## Index

| Doc | Topic |
|-----|--------|
| [lunacompat-inventory.md](lunacompat-inventory.md) | What Luna Compat already solves; duplication checklist |
| [SCANsat.md](SCANsat.md) | Contracts, scenarios, Luna Compat sync vs per-agency |
| [MechJeb2.md](MechJeb2.md) | Autopilot, vessel control, warp — greenfield compat notes |
| [x-science-continued.md](x-science-continued.md) | [x] Science! UI vs per-agency R&amp;D projection |
| [outer-planets-mod.md](outer-planets-mod.md) | Planet pack uniformity, contracts, subjects + per-agency |
| [kerbal-engineer-redux.md](kerbal-engineer-redux.md) | Stat display mod; no per-agency / wire surface |
| [kerbal-attachment-system.md](kerbal-attachment-system.md) | Vessel coupling / pipes / winches; cross-agency couple ownership question |
| [kerbal-inventory-system.md](kerbal-inventory-system.md) | EVA / container inventories; cross-agency PART-snapshot stamp question |
| [trajectories.md](trajectories.md) | Atmospheric trajectory predictor; no per-agency surface |
| [docking-port-alignment-indicator.md](docking-port-alignment-indicator.md) | Docking HUD + port-renaming PartModule; no per-agency surface |
| [tweakscale.md](tweakscale.md) | Part scaling; cost flows through existing per-agency funds path |
| [near-future-and-far-future.md](near-future-and-far-future.md) | Nertea part packs; FFT antimatter factory is a shared-global leak |
| [dmagic-orbital-science.md](dmagic-orbital-science.md) | DMagic science parts + contracts; asteroid science / anomaly records shared-global leak |
| [implementation-spec.md](implementation-spec.md) | Consolidated implementation roadmap — slices ratified from per-mod audits |
| [asymmetric-visual-mods.md](asymmetric-visual-mods.md) | Carve-out from modlist-uniformity for rule-bound visual-only mods (draft) |
| [TEMPLATE-mod-compat.md](TEMPLATE-mod-compat.md) | Copy for each additional mod |

## Operator policy — modlist uniformity (project-wide)

All mods enabled on a server are **mandatory and version-pinned for every joiner**. A client whose GameData tree (mod set + versions) does not match the server's published manifest cannot join. This is the baseline assumption every per-mod doc in this folder is written against.

Implication: per-mod "what happens if a client is missing this mod?" failure modes documented downstream describe the **breakage that should not happen** under correct ops. They are kept as defence-in-depth context for the fork's sanitisation paths (e.g. `LmpClient/Systems/Scenario/ScenarioSystem.cs::FindMissingBodyReference`), not as supported operating states.

**Carve-out — asymmetric visual mods (draft 2026-05-18).** A narrow, rule-bound set of pure-render mods may be installed asymmetrically across clients via `OptionalPlugins` in `LMPModControl.xml`, exempting them from the version-pin and presence-required rules above. The carve-out is rule-bound, not category-bound — "visual mod" alone does not qualify (e.g. Parallax replaces terrain meshes and stays mandatory). See [asymmetric-visual-mods.md](asymmetric-visual-mods.md) for inclusion rules and the candidate shortlist (currently 10 candidates, 0 verified — until a mod's audit lands, it is still subject to the uniformity rule).

## Project-wide design policy — per-agency ScenarioModule isolation (ratified 2026-05-18)

When a third-party mod ships a `ScenarioModule` that persists player-progress-shaped state (planet coverage, factory inventory, asteroid science, anomaly records, etc.) and `PerAgencyCareer` is on:

**Each agency owns their copy of that state, fully isolated.** Each agency starts at zero on a fresh server — no shared baseline. The fork's `AgencyScenarioProjector` strips shared content and splices the requesting agency's state on send, in the same shape as the existing Stage 5.17e-6 splices (`StrategySystem`, `ProgressTracking`, `ContractSystem`, etc.).

Three mods identified in the audits as falling under this policy:
- **SCANsat** — per-body coverage bitmaps + per-agency `Scanners` filter ([SCANsat.md](SCANsat.md)).
- **Far Future Technologies** — global antimatter factory state ([near-future-and-far-future.md](near-future-and-far-future.md)).
- **DMagic Orbital Science** — asteroid science diminishing-returns + anomaly records ([dmagic-orbital-science.md](dmagic-orbital-science.md)).

Where a third-party mod is already covered by a Luna Compat server plugin (SCANsat), the fork-side router takes precedence under `PerAgencyCareer=on` and operator policy disables the LunaCompat entry for that mod.

Consolidated implementation roadmap: [implementation-spec.md](implementation-spec.md).

## Fork touchpoints (per-agency)

When evaluating any mod, ask where its state lives:

1. **Vanilla scenarios** reshaped by `AgencyScenarioProjector` — see `Server/System/Agency/AgencyScenarioProjector.cs` and `Server/System/ScenarioSystem.cs` (`SendScenarioModules`).
2. **Contracts** — `AgencyContractRouter` splits shared Offered/Generated vs per-agency contract persistence; Contract Configurator `ContractPreLoader` remains on the shared scenario path (see router XML docs).
3. **Sanitisation before load** — `LmpClient/Systems/Scenario/ScenarioSystem.cs` ( malformed CC params, body indices including SCANsat-style keys).
4. **Custom relay bytes** — `ModMsgData` / `ModCliMsg` for mod-specific payloads (opaque to core LMP unless you formalise schemas).

## External references

- [Luna Compat README](https://github.com/TheXankriegor/LunaCompat/blob/main/README.md) (Harmony patches, part syncs, server plugin)
- Luna MP wiki — [Mod support](https://github.com/LunaMultiplayer/LunaMultiplayer/wiki/Mod-support) (upstream conventions)
