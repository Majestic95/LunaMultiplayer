# TweakScale — compat layer analysis

**Identification.** [TweakScale](https://github.com/TweakScale/TweakScale) lets a player resize a part in VAB/SPH; cost, mass, resource capacity, and engine thrust all scale automatically. Originally pellinor's; current maintenance: `TweakScale/TweakScale` (Lisias' fork is the active line, aka "TweakScale /L").

---

## Luna Compat status

| In Luna Compat Harmony list? | In Part Sync list? | Server plugin flag? |
|------------------------------|---------------------|---------------------|
| No | No (confirm against upstream) | No |

Not listed in [Luna Compat](https://github.com/TheXankriegor/LunaCompat) ([overlap inventory](lunacompat-inventory.md)).

---

## State ownership

| Where state lives | Shape |
|-------------------|-------|
| `Source/Scale/Scale.cs` (`TweakScale : PartModule, IPartCostModifier, IPartMassModifier`) | Eight `[KSPField(isPersistant = true)]`: `string type`, `bool active`, `bool available`, `float currentScale`, `float defaultScale`, `Vector3 defaultTransformScale`, `float DryCost`, `int OriginalCrewCapacity`. Plus `IPartCostModifier` and `IPartMassModifier` interface impls that adjust VAB cost / vessel mass live. |
| `Source/Scale/ScaleType.cs`, `ScaleExponents.cs` | Scale type definitions + scaling exponents. Static config data, not state. |
| `Source/Scale/Startup.cs` (`[KSPAddon(Startup.Instantly, true)] : MonoBehaviour`) | Init-time dependency check. No persistence. |
| `Source/Scale/MainMenu.cs` (`[KSPAddon(Startup.MainMenu, true)] : MonoBehaviour`) | Companion-mod-warning dialog at main menu. No persistence. |
| `Source/Scale/PrefabDryCostWriter.cs`, `Globals.cs`, `Tools.cs`, etc. | Helpers. No save-game persistence. |

**No `ScenarioModule`.** Verified against the `Source/Scale/` file listing. **No career, contract, or science surface.**

---

## This fork touchpoints

- Scenario: **none.**
- Vessel: TweakScale's eight persistent fields ride standard PartModule sync. A part scaled in agency-A's VAB ships its `currentScale` + `DryCost` + `OriginalCrewCapacity` along with the vessel proto.
- Vessel cost: `IPartCostModifier` is called by KSP when computing the editor build cost. Under per-agency Career, that cost is debited from `AgencyState.Funds` via the existing funds-routing path. **TweakScale's cost modification flows through the per-agency funds path automatically** — no new code.
- Vessel mass: `IPartMassModifier` is called by KSP physics — pure local computation, no wire surface.
- Custom relay: not used.

---

## Interaction with PerAgencyCareer

**Zero direct career interaction.** No `AgencyState` field. The indirect cost-via-`Funds` interaction is exactly what we want: a player scaling parts up pays the higher cost out of their agency's funds, scaled-down pays less. Already correct under the existing fork.

---

## Failure modes (multiplayer)

1. **`currentScale` divergence across clients** — disallowed by standard PartModule sync invariants. If a relay drops the field, the part snaps to default scale on the receiving end. Standard vessel-sync regression test catches this.
2. **`DryCost` calculated locally** — `DryCost` is persisted, but TweakScale also recomputes it at part instantiation via `PrefabDryCostWriter`. If the prefab DB differs between clients (modlist mismatch), recomputation would diverge. Disallowed by modlist policy.
3. **Scaled engines + ScaleExponents config files mismatched** — same shape as (2); modlist policy covers.
4. **Cost-cheat via tiny scaling** — inherent to TweakScale, not a per-agency concern. Players who reduce scale to reduce cost still get a tiny (low-utility) part.

---

## Proposed layering

| Layer | Owner repo | Deliverable |
|-------|-------------|--------------|
| Core LMP fork | no | None owed. |
| Luna Compat (Harmony/MM) | no | None owed. |
| Luna Compat server plugin | no | None owed. |
| Operational | yes | Modlist policy enforces TweakScale + companion packs + version uniformity. |

---

## Tests

1. Single-agency: scale a part in VAB, build, launch, reconnect. Confirm `currentScale` persists and the part renders at the chosen size on reload.
2. Cross-agency: agency-A builds a scaled-up tank. Agency-B sees the scaled mesh on agency-A's vessel via standard proto sync. Confirm scale matches on both clients.
3. Per-agency funds: confirm the scaled-up part's build cost is debited from agency-A's `AgencyState.Funds` (existing funds path; this is a regression check, not new logic).
4. Modlist mismatch (one client missing a TweakScaleCompanion patch for a third-party mod): disallowed by [README.md](README.md) policy.

---

## Tracking

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **TweakScale upstream:** `TweakScale/TweakScale`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** none — TweakScale has no per-agency surface beyond the existing funds path.
- **TweakScale files inspected:** `Source/Scale/Scale.cs` (PartModule with 8 isPersistant fields, IPartCostModifier + IPartMassModifier), `Source/Scale/Startup.cs` (KSPAddon for init only), `Source/Scale/` file inventory.
- **Findings this pass:**
  1. TweakScale is PartModule-only — no ScenarioModule, no career interaction surface.
  2. The 8 persistent fields cover scale state; ride standard PartModule sync.
  3. Editor cost modification rides the existing per-agency funds path automatically.
- **Net verdict:** no work owed at any layer.
- **Gaps still open:** none.
