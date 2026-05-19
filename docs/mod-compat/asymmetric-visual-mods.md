# Asymmetric visual mods — carve-out from modlist-uniformity policy

**Status.** Draft 2026-05-18. Inclusion rules ratified; per-mod audits pending. Until a mod's row is marked *Verified*, the project-wide modlist-uniformity rule in [README.md](README.md) still applies.

**Scope.** This doc carves out a narrow exception to the "all mods are mandatory and version-pinned" policy: a small, audited set of mods that may be installed asymmetrically across clients without affecting simulation, persistence, or wire protocol. Operators ship them via `OptionalPlugins` in `LMPModControl.xml` so the join check passes regardless of presence or version.

**Non-goal.** This is NOT an open-ended "visual mods are fine" relaxation. The label "visual" is misleading — several widely-described "visual mods" cross the line (Parallax replaces terrain meshes; RealPlume modifies thrust via PartModule). The carve-out is rule-bound, not category-bound.

---

## Why this is feasible

The LMP modcontrol layer already supports the asymmetric model. From [LmpCommon/ModFile/Structure/ModControlStructure.cs](../../LmpCommon/ModFile/Structure/ModControlStructure.cs):

- `MandatoryPlugins` — must be present (optional SHA pin)
- `OptionalPlugins` — allowed, **no SHA check**, presence not required
- `ForbiddenPlugins` — banned
- `AllowNonListedPlugins` — bool: tolerate plugins not in any list

The join check at [LmpClient/Systems/Mod/ModFileHandler.cs:88-127](../../LmpClient/Systems/Mod/ModFileHandler.cs#L88-L127) only fails when a file is forbidden OR (`AllowNonListedPlugins=false` AND missing from mandatory/optional). A mod listed under `OptionalPlugins` passes whether the client has it or not, at any version.

So the carve-out is a **0-line code change**. The work is in (a) defining the inclusion rule precisely, (b) auditing each candidate mod against it, and (c) publishing the operator config snippet.

---

## Inclusion rule

A mod qualifies for the asymmetric-visual carve-out **only if all six conditions hold**. Each row in the candidate list below must cite which check was run.

| # | Condition | Test |
|---|-----------|------|
| 1 | No `ScenarioModule` subclass anywhere in the mod's assembly. | grep for `: ScenarioModule` in source / decompile. Any hit disqualifies. |
| 2 | No `PartModule` subclass that mutates any of: resources, mass, thrust, ISP, drag, heat, lift, or part position/rotation. | Read each `PartModule.OnFixedUpdate`, `OnUpdate`, `OnLoad`, `OnSave`. Modules that only emit visual effects (light, particle, mesh swap on engine running state) pass. |
| 3 | No terrain mesh, terrain collider, or PQS modifications. | grep for `PQS`, `PQSMod`, `MeshCollider`, `terrain` in source. Any hit disqualifies. |
| 4 | No celestial body parameter modifications (atmosphere thickness, density, temperature, gravity, rotation). | Inspect ModuleManager configs shipped with the mod. Patches against `@Body[*]`, `@PLANET[*]`, or `@Kopernicus` disqualify the bundled config (the engine-only portion may still qualify if shipped separately). |
| 5 | No new `RESOURCE_DEFINITION` or `PartResourceDefinition`. | grep for `RESOURCE_DEFINITION { name = ...` in configs. Any hit disqualifies. |
| 6 | No Harmony patches on physics, contract, science, funding, reputation, vessel-loading, vessel-saving, or save-game serialisation. | grep for `[HarmonyPatch` and inspect targets. Patches limited to UI/render/camera/input pass. |

**A mod that ships an engine + bundled configs is treated as two artifacts.** The engine DLL may qualify while a specific config pack disqualifies under rule 4. List the engine and the audited config pack as separate `OptionalPlugins` entries.

**Re-audit triggers.** Any new major version of an included mod must be re-audited before the operator updates the `OptionalPlugins` recommendation. Configs are particularly prone to rule-4 drift between versions.

---

## Candidate shortlist (pending audit)

Each row is a candidate. None are *Verified* yet. Verification is per-mod and follows the [TEMPLATE-mod-compat.md](TEMPLATE-mod-compat.md) shape — produce a one-page audit doc per mod that walks the six checks, then update Status.

| # | Mod | Type | Status | Notes |
|---|-----|------|--------|-------|
| 1 | **EVE Redux** (Environmental Visual Enhancements) | Clouds/aurora/city-lights engine | Candidate | Engine DLL only. Bundled cloud configs ship separately (see rows 8-10). Likely passes 1-6 but needs grep pass. |
| 2 | **Scatterer** | Atmospheric scattering + ocean shader | Candidate | Has been reported to ship atmosphere tweaks in some forks. Audit rule 4 carefully. |
| 3 | **TUFX** | Post-processing (color grading, bloom, AA) | Candidate | Profiles save per-scene to local config, never to save game. Likely cleanest pass. |
| 4 | **PlanetShine** | Ambient light from celestial bodies onto vessels | Candidate | Pure light source code. Likely passes. |
| 5 | **Distant Object Enhancement** | Distant vessel/body visibility flares | Candidate | Reads vessel positions, renders flares. Read-only access — likely passes. |
| 6 | **Engine Lighting Relit** | Engines emit light when active | Candidate | Adds PartModule. Rule 2 requires confirming the module is FX-only (no thrust/ISP touch). |
| 7 | **Waterfall** | Shader-based engine plumes | Candidate (caveat) | Adds `ModuleWaterfallFX` PartModule. PartModule rides ProtoVessel wire. KSP preserves unknown ConfigNodes through round-trip, so wire-safe. Rule 2 requires confirming the module touches no sim state. |
| 8 | **Stock Visual Enhancements (SVE)** | EVE+Scatterer config pack | Candidate (config-only) | Audit only rule 4. Listed separately from row 1. |
| 9 | **Astronomer's Visual Pack (AVP)** | EVE+Scatterer config pack | Candidate (config-only) | Same as SVE. |
| 10 | **Spectra** | EVE+Scatterer config pack | Candidate (config-only) | Same as SVE. |

### Excluded (with rationale)

| Mod | Excluded by | Notes |
|-----|-------------|-------|
| **Parallax** | Rule 3 | Replaces terrain meshes and adds scatter colliders. Two clients on different Parallax versions disagree on rover wheel contact, lander altitude, surface scatter collisions. Sim divergence, not cosmetic. Stays mandatory. |
| **RealPlume** | Rule 2 | Modifies engine thrust curves and ISP through PartModule patches, not just FX. Stays mandatory. |
| **KSPRC** | Rule 4 (bundled configs) | Visual pack but historically bundled atmosphere parameter tweaks. If a stripped-config variant exists, audit that separately. |
| **HullCam VDS / Camera Tools** | Rule 2 (case-by-case) | Adds camera-selection PartModules. Some variants are FX-only; some touch part rotation. Audit before inclusion. |

---

## Operator config recipe

Ship the carve-out as `OptionalPlugins` entries in the server's `LMPModControl.xml` (the file generated by [Server/System/ModFileSystem.cs](../../Server/System/ModFileSystem.cs)). Omit the `<Sha>` element so version drift is allowed.

Shape (substitute real `FilePath` values from the operator's GameData tree):

```xml
<OptionalPlugins>
  <DllFile>
    <Text>EVE Redux (engine only)</Text>
    <Link>https://github.com/.../EnvironmentalVisualEnhancements</Link>
    <FilePath>environmentalvisualenhancements/eve-redux.dll</FilePath>
  </DllFile>
  <DllFile>
    <Text>Scatterer</Text>
    <Link>https://github.com/LGhassen/Scatterer</Link>
    <FilePath>scatterer/scatterer.dll</FilePath>
  </DllFile>
  <DllFile>
    <Text>TUFX</Text>
    <Link>...</Link>
    <FilePath>tufx/tufx.dll</FilePath>
  </DllFile>
  <!-- repeat per verified mod -->
</OptionalPlugins>
```

Important: `FilePath` is normalised to lowercase by [ModFileHandler.SetAllPathsToLowercase](../../LmpClient/Systems/Mod/ModFileHandler.cs#L44-L49). Match that convention in the snippet.

`AllowNonListedPlugins` should stay at its current operator-chosen value — this carve-out works equally with it set true or false. The `OptionalPlugins` entries are what document the operator's intent ("I expect to see these on some clients, not others") so the carve-out is auditable from the manifest alone.

---

## Interaction with PerAgencyCareer

None. Asymmetric visual mods touch zero per-agency surfaces by construction (rules 1, 5, 6 close those paths). The gate's on/off state is irrelevant to whether a visual mod is present on a given client.

---

## Failure modes (what *can* go wrong, even with the rule in place)

1. **Cross-client screenshot mismatch.** Two players take screenshots of the same vessel at the same time. One has Scatterer, the other doesn't. Outputs look different. Expected and acceptable — the carve-out's whole point.
2. **Performance asymmetry.** A heavyweight visual stack on one client causes that client to lag in physics frames, which can show up as wobblier orbital sync via the existing Luna MP lag-tolerance paths. Operator concern, not a wire issue.
3. **A "visual" mod silently violates a rule after a version bump.** Mitigated by the re-audit trigger above. The whitelist must be dated against the audited version.
4. **Waterfall PartModule on a client without Waterfall.** KSP preserves the unknown `MODULE { name = ModuleWaterfallFX ... }` ConfigNode through ProtoVessel round-trip (well-known KSP modding behaviour: stock loader keeps unrecognised module nodes intact). The plume is invisible for that client; nothing else changes. Worst case is a warning in the log.

---

## Open work

- Per-mod audit docs for each candidate (10 files, one per row). Each runs the six checks against the actual upstream source at a pinned version. See [TEMPLATE-mod-compat.md](TEMPLATE-mod-compat.md).
- Update each candidate row from *Candidate* → *Verified at version X.Y* once its audit lands.
- After at least three audits land, revisit whether the rule needs tightening based on what we see in practice.

---

## Tracking

| Item | Status |
|------|--------|
| Inclusion rule ratified | Draft 2026-05-18 |
| Code change required | None (LMP modcontrol already supports it) |
| Candidate shortlist | 10 entries, all unverified |
| Per-mod audits | 0 / 10 |
| README carve-out pointer | Pending |
