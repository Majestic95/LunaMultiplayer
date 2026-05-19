# Luna Compat overlap inventory

**Purpose.** Before adding Harmony, ModuleManager definitions, part-module sync lists, or a server plugin in *this* fork, cross-check **[Luna Compat](https://github.com/TheXankriegor/LunaCompat)** ([README](https://github.com/TheXankriegor/LunaCompat/blob/main/README.md)).

**Version skew.** Luna Compat’s README pins dependencies and badge versions to specific LMP / KSP lines. After merging worktrees into this fork, **re-verify** Luna Compat’s stated LMP/KSP compatibility on each release; do not assume badges track this fork’s protocol bumps.

---

## Harmony patches (Luna Compat)

| Mod | Role | Requires Luna Compat **server plugin**? |
|-----|------|----------------------------------------|
| Extraplanetary Launchpads | Static random seed (recycling) | No |
| Infernal Robotics | Historical LMP IR fixes | No |
| KIS | Historical LMP KIS fixes | No |
| Kethane | Static random seed (distribution) | No |
| TUFX | Persist settings across disconnect | No |
| ClickThrough Blocker | Persist settings | No |
| Speed Unit Annex | Persist settings | No |
| Physics Range Extender | Terrain extender mitigation | No |
| Kerbal Konstructs | Instances, groups, map decals, facilities | **Yes** |
| Kerbal Colonies | Colonies / facilities | **Yes** |
| SCANsat | Active scanners, background scanning, progress | **Yes** |

**Implication.** New server-side logic for those “server plugin” mods should ideally land as **contributions or extensions inside Luna Compat’s plugin**, not duplicated under `Server/` here, unless there is an architectural reason core LMP must own it.

---

## Mods analysed in `docs/mod-compat/` but not Luna Compat–scoped

Those entries are tracked for **fork / per-agency** interaction; absence from Luna Compat does **not** imply incompatibility — it means patching is neither shipped nor preemptively required upstream.

| Mod | Luna Compat README | Fork doc |
|-----|--------------------|----------|
| MechJeb2 | Not listed | [MechJeb2.md](MechJeb2.md) |
| [x] Science! Continued | Not listed | [x-science-continued.md](x-science-continued.md) |
| Outer Planets Mod | Not listed | [outer-planets-mod.md](outer-planets-mod.md) |

SCANsat differs: covered by Luna Compat **and** analysed here for fork overlap ([SCANsat.md](SCANsat.md)).

---

## Part module sync list (excerpt — see upstream README for full set)

Luna Compat ships LMP **`PartModule`** sync configuration for many mods, including SCANsat.

**Rule of thumb.**

- Changing **which fields** replicate for a PartModule belongs in Luna Compat (or companion patch mod), unless the fork introduces a **new message type or server authority model** that PartModule configs cannot express.
- Changing **whose career/scenario blobs** contain a serialized field belongs here (per-agency projection, contracts, scenarios).

---

## Duplication checklist (use before implementing)

Answer these for each new compat feature:

1. Does Luna Compat already ship a Harmony patch or part sync entry for this mod?
2. If yes, does **per-agency career** invalidate any shared-state assumptions (single global RNG seed, shared scenario node, shared contract acceptance)?
3. Does the behaviour need **`ModMsgData`** relay semantics** only**, or deterministic server authority?
4. If server authority: can **`LunaCompatServerPlugin`** (or Luna Compat’s config XML) absorb it?

If (1)=yes and (2)=no → **defer to Luna Compat**.  
If (1)=yes and (2)=yes → **open a coordinated change** (Luna Compat + this fork docs + optional protocol notes), avoid silent double-patching.

---

## Tracking

Maintain a short “last reviewed” footer when you reconcile with Luna Compat releases:

| Item | Luna Compat ref (approx.) | Reviewed |
|------|---------------------------|----------|
| README inventory | Default branch README | _(fill when reviewed)_ |

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **Luna Compat upstream:** **NOT** re-fetched this pass — the rows in this doc reflect Luna Compat's README at the time the inventory was first written. Refresh against `TheXankriegor/LunaCompat` when reconciling for a release.
- **Per-mod source walks completed in this folder** (independently of Luna Compat status):
  - [SCANsat.md](SCANsat.md) — `KSPModStewards/SCANsat`, `master`. Coverage persistence located in `SCANcontroller` scenario; not currently per-agency-projected. 4 product questions open.
  - [x-science-continued.md](x-science-continued.md) — `linuxgurugamer/KSP-X-Science`, `master`. KSPAddon (no scenario state). Cosmetic onboard-science leak via stock `FlightGlobals.Vessels`. 2 product questions open.
  - [outer-planets-mod.md](outer-planets-mod.md) — `Poodmund/Outer-Planets-Mod`, `master`. Pure data, no plugin. Operator policy covers body uniformity. New follow-up: optional Final Frontier ribbon pack writes to a `FinalFrontier` `ScenarioModule` not currently per-agency-projected.
  - [MechJeb2.md](MechJeb2.md) — `MuMech/MechJeb2`, `dev`. PartModule with three-tier persistence; no career/scenario surface. **All five original product questions resolved as no-action.**
- **New cross-mod follow-up:** Final Frontier ribbon mod (referenced by OPM's `OptionalMods/`) writes to a `FinalFrontier` scenario module that is not in `AgencyScenarioProjector.CareerScenarios`. If any operator's frozen modlist includes Final Frontier, add a per-mod doc using [TEMPLATE-mod-compat.md](TEMPLATE-mod-compat.md).
