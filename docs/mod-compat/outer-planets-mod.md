# Outer Planets Mod (OPM) — compat layer analysis

**Identification.** Outer Planets Mod is a **celestial body pack** beyond stock — new worlds, orbital relationships, biome maps, science definitions, textures. Contracts and science subject IDs routinely reference outer bodies (`celestialBody` name + biome IDs).

[Luna MP upstream wiki Mod support](https://github.com/LunaMultiplayer/LunaMultiplayer/wiki/Mod-support) already stresses **matching GameData trees** server vs clients — planet packs are the canonical severity-up case.

[Luna Compat](https://github.com/TheXankriegor/LunaCompat): **no dedicated OPM entry** at overlap-review time (`lunacompat-inventory.md`). Compat burden is mostly **universe homogeneity + contract/scenario sanitisation**, not OPM-exclusive Harmony shipped with Luna Compat.

---

## Core multiplayer rule

**Everyone must load the same solar system.**

If any participant’s `FlightGlobals.Bodies` list/order or body names differs from others:

- Contracts referencing missing or reordered bodies explode or strip.
- Subject IDs keyed by biome/body can point at mismatched interpretations.
- Vessel celestial references desync outright.

That rule is invariant for LMP generally; **`PerAgencyCareer` does not relax it**.

---

## What this Luna MP fork already does (relevant hooks)

### Contract / CC payload sanitisation (client)

`LmpClient/Systems/Scenario/ScenarioSystem.cs` walks contracts for **integer body indices** against `FlightGlobals.Bodies` and rejects/strips payloads that reference missing bodies — mitigates mismatched installs or partially corrupted shared scenario text.

Science-related CC **`PARAM`** keys (`experiment`, `biome`, `situation`, … under `BodyContextKeys`) participate in malformed-node detection beside SCANsat-ish keys — important when OPM-heavy contract packs enumerate outer worlds.

Design implication: a client **without OPM** joining an OPM roster may **lose chunks of shared contract/offered content** deliberately rather than hard-crashing — predictable but operationally undesirable. **Operational fix:** enforce identical mod roster (OPM mandatory for that universe).

---

## Interaction with PerAgencyCareer

Per-agency state still lives in subject IDs rooted in celestial/biome names from the player’s runtime:

- **`AgencyState.ScienceSubjects`** keys resemble `experimentId@@BodySituation`-style identifiers (whatever KSP encodes — see live agency files during tests). New OPM worlds add **legal** IDs that vary only along the universal body list agreement.

Projection re-splices those subjects through `AgencyScenarioProjector`; no extra server split is inherently required once body universes agree.

Concrete concerns:

| Concern | Mitigation |
|---------|-------------|
| One agency researches OPM-exclusive nodes first | Intended — tech tree splice is per-agency in gate-on mode (`AgencyTechRouter` pattern). Ensure **tech tree defs** identical across installs. |
| Contracts mentioning OPM bodies | Offered/Generated stay shared router side — still require **matching bodies** everywhere. |

### Do we need a dedicated compat layer?

**Default:** Policy + tooling layer, not Luna MP fork code:

- **Mandatory mod manifest** baked into session rules (MM zip list or CKAN metapackage).
- Optional future **fork enhancement** — **server rejects handshake** unless advertised body count/name hash matches (speculative; touches protocol/handshake UX).

Reserve **Harmony/MM patches** under Luna Compat (or adjunct) **only if** measured issues appear (examples: ScenarioModule persists cross-body indices as integers that break projection between slightly different Kopernicus-derived loads — hypothetical until reproduced).

---

## Kopernicus / load-order warnings (operational)

OPM stacks often include **Kopernicus**. Follow KSP norms:

- Compatible Kopernicus + OPM pairing per KSP version.
- Stable load order ModuleManager audits when updating either.

These are orthogonal to Luna MP but dominate real-world breakage.

---

## Recommended operator checklist

Before advertising an OPM + per-agency server:

1. Freeze an **exported GameData snapshot** / CKAN metapackage (includes OPM, Kopernicus, any rescales).
2. Smoke test **two Career agencies** researching different outer bodies — observe no stray cross-agency subject leakage.
3. Join with a **thin client intentionally missing one OPM DLL** — document what strips/breaks (`ScenarioSystem` sanitisation surfaces are your reference).
4. Record whether **Breaking Ground deployed science** is in play — overlaps `DeployedScience` scenario gating in `ScenarioSystem`; test that path with OPM biomes enabled.

---

## Test matrix extensions (science + contracts focus)

Beyond generic “matching installs” validation:

| Step | PASS criterion |
|------|----------------|
| A accepts CC contract referencing OPM moon | Accepted state visible only inside A’s routed contract persistence (existing agency contract semantics). |
| B without same contract defs | Disallowed by operator policy ([README.md](README.md)). The handshake check `B without OPM` is documented as a non-supported state — kept here as the failure shape, not a pass criterion. |

---

## Mod-source walk (OPM repo `Poodmund/Outer-Planets-Mod`)

Walked branch `master` via WebFetch on 2026-05-18.

### Repo composition

OPM is a **pure-data Kopernicus mod**. The repository tree is:

```
.github/ISSUE_TEMPLATE/
CKAN/                                — CKAN metadata
GameData/CTTP/                       — community texture pack (dependency)
GameData/OPM/
  KopernicusConfigs/                 — body definitions (Sarnus, Urlum, Neidon, Plock + moons)
  Localization/                      — string tables
  OPM_Textures/PluginData/           — textures
  Patches/                           — ModuleManager patches (see below)
  Resources/                         — resource distribution configs
  OPM_KSPedia_core.ksp               — KSPedia
  OuterPlanetsMod.version            — version manifest
OptionalMods/
  OPM_FinalFrontier/                 — optional Final Frontier ribbon definitions
```

**No `.cs` / `.csproj` / `.dll` in the repo.** OPM ships no plugin code; it relies on Kopernicus, ModuleManager, and CTTP.

### Patches folder

`GameData/OPM/Patches/` contains:

| File | Role |
|------|------|
| `OPM_CommNet.cfg` | CommNet ground stations on outer bodies |
| `OPM_DistantObjectEnhancement.cfg` | Visual mod integration |
| `OPM_KSPTextureLoader.cfg`, `OPM_KopernicusAsteroids.cfg`, `OPM_PlanetShine.cfg`, `OPM_RemoteTech.cfg`, `OPM_ResearchBodies.cfg` | Compatibility with other mods (visual, comms, research-bodies fog) |
| `OPM_Resources.cfg` | Resource distribution definitions for outer bodies |
| `OPM_ScienceDefs.cfg` | New `ScienceSubject` definitions per body × situation × experiment |

**No `Contracts.cfg` or contract pack ships in OPM itself.** CC contract packs targeting OPM bodies (e.g. the historic "Career Evolution" or "Tourism Plus" contract packs) are separate mods governed by the project-wide modlist policy.

### Per-agency cross-walk

| OPM-introduced surface | Path through Luna MP fork | Per-agency verdict |
|-------------------------|----------------------------|---------------------|
| New entries in `FlightGlobals.Bodies` (Sarnus, Urlum, Neidon, Plock + moons) | Loaded at KSP startup before any LMP code runs. Vessel celestial references, contract `body` indices, science subject IDs all assume this list shape. | **Already correct under the mandatory-modlist policy.** Body uniformity is a precondition, not a runtime check. `ScenarioSystem.FindInvalidBodyIndex` remains the defence-in-depth guard for misconfigured installs. |
| New `ScienceSubject` IDs from `OPM_ScienceDefs.cfg` (e.g. `crewReport@SarnusInSpaceLow`) | Flow through stock KSP's `ResearchAndDevelopment.GetSubjects()`; share-progress relay is `ShareProgressScienceSubjectMsgData`. | **Already routed per-agency** by `AgencyResearchRouter.TryRouteScienceSubject`; projector splices them back via R&D `Science` child nodes. No OPM-specific change. |
| New resource distribution from `OPM_Resources.cfg` (read by ORS / Karbonite / SCANsat resource overlay) | The configs themselves are global static data; per-body min/max state lives in `SCANcontroller.SCANResources` (see `SCANsat.md`). | **Same shared-coverage caveat as SCANsat** — resource overlay knowledge is unioned across agencies today. Open product decision covered in [SCANsat.md](SCANsat.md). |
| Optional Final Frontier ribbons (`OptionalMods/OPM_FinalFrontier/`) | Final Frontier writes its own `ScenarioModule` (`FinalFrontier`), not stock `ProgressTracking`. **Not currently in `AgencyScenarioProjector.CareerScenarios`.** | **Cosmetic leak today** — every agency sees every other agency's ribbons. Final Frontier is itself a separate mod and would need its own per-mod doc + projector entry if isolation is desired. Out of scope for the OPM doc; tracked as a follow-up. |
| CommNet / RemoteTech / ResearchBodies compat patches | Local KSP configuration only. | No per-agency surface. |

### Failure modes

1. **Body-list mismatch** — disallowed by operator policy; sanitiser catches stragglers. Already documented above.
2. **Cross-agency resource overlay sharing on outer bodies** — same shape as SCANsat coverage. Defer to SCANsat product call.
3. **Final Frontier ribbon leak (if optional pack installed)** — out of scope for OPM doc; new follow-up.
4. **Kopernicus / CTTP version skew** — operational; mandatory modlist policy enforces.

### Compat layer recommendation

| Surface | Owner verdict |
|---------|----------------|
| Body uniformity | **Operator policy** (mandatory modlist). Fork sanitiser is defence-in-depth. |
| OPM science subjects | **Already handled** by `AgencyResearchRouter`. No OPM-specific code. |
| OPM body resource overlay isolation | **Follows SCANsat product call.** No standalone decision required for OPM. |
| Final Frontier ribbons (optional) | **Out of scope for OPM** — tracked as a separate per-mod follow-up if the optional pack is in any operator's frozen modlist. |

### Design questions raised by the source walk

None unique to OPM. The two related open questions (resource overlay scoping, Final Frontier ribbons) are tracked elsewhere — SCANsat coverage thread, and a hypothetical future Final-Frontier.md respectively.

---

## Tracking

| Item | Notes |
|------|--------|
| Code anchors | `LmpClient/Systems/Scenario/ScenarioSystem.cs` (`FindInvalidBodyIndex`, CC body-context validation) |
| Related doc | [`SCANsat.md`](SCANsat.md) CC parameter discipline (same sanitisation subsystem) |

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **OPM upstream:** `Poodmund/Outer-Planets-Mod`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** `LmpClient/Systems/Scenario/ScenarioSystem.cs::FindInvalidBodyIndex` + `BodyIndexKeys` (defence-in-depth body-range guard).
- **OPM files inspected:** `GameData/OPM/` tree, `GameData/OPM/Patches/` cfg listing, `OptionalMods/` listing. Confirmed: no `.cs`, no `.dll`, no plugin code shipped.
- **Findings this pass:**
  1. OPM is a pure-data Kopernicus mod — no plugin surface. All per-agency interaction routes through stock KSP state (Bodies, ScienceSubject IDs, resource configs).
  2. Body uniformity moved from "test matrix concern" to "operator policy precondition" ([README.md](README.md)).
  3. Science subjects defined by `OPM_ScienceDefs.cfg` ride existing `AgencyResearchRouter` plumbing.
  4. Resource overlay isolation tracks the SCANsat product decision (same `SCANcontroller.SCANResources` blob).
  5. **New follow-up surfaced:** optional Final Frontier ribbons (`OptionalMods/OPM_FinalFrontier/`) write to a `FinalFrontier` `ScenarioModule` not currently projected per-agency. If any operator's frozen modlist includes Final Frontier, a separate per-mod analysis is warranted.
- **Gaps still open (product calls):**
  - Resource-overlay isolation — defer to SCANsat thread.
  - Final Frontier ribbon isolation — new per-mod doc only if Final Frontier is in scope for the project.
