# Kerbal Engineer Redux (KER) — compat layer analysis

**Identification.** Kerbal Engineer Redux ([upstream](https://github.com/jrbudda/KerbalEngineer)) is a client-side flight / editor / tracking-station stat display (delta-V, TWR, orbit data, burn time). The Flight side requires an in-vessel **engineer chip** part (or a Kerbal with the Engineer trait), but every value KER shows is computed from vessel/orbit state already known to KSP.

---

## Luna Compat status

| In Luna Compat Harmony list? | In Part Sync list? | Server plugin flag? |
|------------------------------|---------------------|---------------------|
| No | No | No |

KER is **not** listed in [Luna Compat](https://github.com/TheXankriegor/LunaCompat) ([overlap inventory](lunacompat-inventory.md)). No upstream coordination implied.

---

## State ownership

| Where state lives | Shape |
|-------------------|-------|
| `KerbalEngineer/Flight/FlightEngineerModule.cs` (`: PartModule`) | Vessel-side `PartModule`. **No `[KSPField(isPersistant)]` fields.** The part's presence is a marker that the vessel has a flight computer; nothing about the readouts persists on the part. |
| `KerbalEngineer/Settings/SettingHandler.cs` + sibling XML files | Per-install XML files at `GameData/KerbalEngineer/Settings/*.xml`. Window positions, default readout sections, display preferences. **Never crosses the wire; never enters the save game.** |
| `KerbalEngineer/VesselSimulator/` | In-memory simulation of vessel stats (delta-V across stages, TWR, etc.). Pure read of vessel/parts/atmosphere; no persistence target. |
| `KerbalEngineer/Flight/Sections/`, `Flight/Readouts/`, `Flight/Presets/` | Section / readout / preset definitions. Configurable layouts ride the XML settings above. |

**No ScenarioModule.** Full inventory of `KerbalEngineer/` confirmed: no `*Scenario*.cs`, no `*Controller*.cs` that inherits `ScenarioModule`. Verified against the file tree.

**No contracts, no science definitions, no career surface.**

---

## This fork touchpoints

- Scenario: **none.** KER does not read `ResearchAndDevelopment.GetSubjects()`, `ContractSystem`, `Funding.Instance`, `Reputation.Instance`, or `ProgressTracking`. Its readouts compute from vessel state directly.
- Vessel: standard proto sync carries the `FlightEngineerModule` PartModule along with the vessel. Vessel ownership (`lmpOwningAgency`) applies automatically.
- Custom relay: not used.

---

## Interaction with PerAgencyCareer

**None.** KER does not touch `AgencyState.Funds`, `Science`, `Reputation`, `TechNodes`, `ScienceSubjects`, `Contracts`, `Strategies`, `Achievements`, `FacilityLevels`, or `PurchasedParts`. The `AgencyScenarioProjector` is irrelevant to KER.

Behaviour under gate=on or gate=off is identical: each player sees stats for whatever vessel they're locally viewing, computed from the local KSP's state.

---

## Failure modes (multiplayer)

1. **VesselSimulator computes wrong delta-V for a peer-owned vessel under heavy lag** — proto sync delivers a stale snapshot of the vessel's parts/fuel; KER computes against that snapshot. Cosmetic; resolves on next proto update. Same shape as any other vessel-displayed stat under Luna MP.
2. **Per-install XML divergence** — two players have different readout preferences. Per-install by design; not a multiplayer concern.
3. **Engineer-chip part absence on one client** — disallowed under the modlist policy ([README.md](README.md)). Sanitisation path (stock part-validation) would strip the part from incoming proto; vessel would still arrive but lose the chip. Mandatory-modlist policy prevents this.

---

## Proposed layering

| Layer | Owner repo | Deliverable |
|-------|-------------|--------------|
| Core LMP fork | no | No code owed. |
| Luna Compat (Harmony/MM) | no | No code owed. |
| Luna Compat server plugin | no | No code owed. |
| Operational | yes | Modlist policy already enforces KER + version uniformity. |

**Net:** KER is functionally identical between single-player and multiplayer — orthogonal to per-agency, orthogonal to LMP scenario sync, orthogonal to Luna Compat. The simplest result of the audit batch so far.

---

## Tests

1. Two clients in the same flight scene, both with KER installed: each sees their own delta-V readout for their controlled vessel; the readout matches single-player calculation.
2. Peer-owned vessel becomes the focus: KER's flight readouts display computed values from the proto snapshot the local KSP holds. No regressions when proto updates arrive.
3. Modlist mismatch (one client missing KER): policy violation per [README.md](README.md); not a supported state.

---

## Tracking

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **KER upstream:** `jrbudda/KerbalEngineer`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** none needed — KER has no per-agency or scenario interaction.
- **KER files inspected:** top-level `KerbalEngineer/` tree (confirmed no ScenarioModule), `Flight/FlightEngineerModule.cs` (PartModule, no isPersistant), `Settings/SettingHandler.cs` (XML files at `GameData/KerbalEngineer/Settings/`, per-install, never wire-visible).
- **Net verdict:** no work owed at any layer.
- **Gaps still open:** none.
