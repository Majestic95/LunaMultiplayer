# BUG-014 — `VesselPositionSys/ExtensionMethods/` rb-set audit (continue PR #628's pattern)

**Implementation order:** **5th** in the Option C sequence. Can be interleaved earlier — units are small and isolated.

**Status:** **CLOSED — audit complete, no remaining sites.** Inventory walk against `master` at `25303e7d` (2026-05-16) confirms PR #628's pattern is fully applied across the directory. No fix needed in this fork.

**Inventory entry:** BUG-014 in `01-bug-inventory.md` is listed as "likely fixed in master" via upstream PR [#628](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/628) (merged 2026-04-17, commit `1b5fc45b`). This Phase-2 doc covers only the *remaining* sites PR #628 didn't reach.

---

## Symptom (remaining variant)

Large vessels (space stations) and physics-range-shared craft still jitter visibly in some scenarios after PR #628. The interpolation rotation/rb fix landed for unpacked rigidbodies, but extension methods that set `transform.position` / `transform.rotation` without a matching `part.rb.position` / `part.rb.rotation` create the same snap-back pattern PR #628 described.

## Code locations (validated)

- [LmpClient/Systems/VesselPositionSys/ExtensionMethods/VesselPositioner.cs:71-108](../../../LmpClient/Systems/VesselPositionSys/ExtensionMethods/VesselPositioner.cs#L71-L108) — `SetVesselPositionAndRotation`. The only file in the directory that touches Unity transforms; `VesselProtoUpdater.cs` only mutates `protoVessel.*` fields.
- [upstream PR #628](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/628) — already-merged fix, present in our master since `541fbcfa`'s upstream merge chain. Commit message reproduced verbatim from the brainstorm:
  > For unpacked (off-rails) parts with rigidbodies, only transform.position and transform.rotation were being set. Unity's physics engine treats this as an external teleport and snaps the rigidbody back toward its last solved position on the next FixedUpdate, causing visible vibration and shaking.

## Inventory findings (1 file, 4 setter sites, all correctly paired)

| Site | Setter | Path | rb pairing |
|------|--------|------|------------|
| `VesselPositioner.cs:75` | `vessel.vesselTransform.position` | `!vessel.loaded` | N/A — vessel is unloaded, no Unity physics to fight; vesselTransform is the serialization target. |
| `VesselPositioner.cs:76` | `vessel.vesselTransform.rotation` | `!vessel.loaded` | N/A — same as above. |
| `VesselPositioner.cs:84` | `part.partTransform.rotation` | loaded path, every part | Paired at `:99` with `part.rb.rotation = partRotation` under `if (!vessel.packed && part.rb)`. |
| `VesselPositioner.cs:91` | `part.partTransform.position` | loaded + (`vessel.packed` or `physicalSignificance.FULL`) | Paired at `:101` with `part.rb.position = part.partTransform.position` under `if (!vessel.packed && part.rb && physicalSignificance.FULL)`. |

The conditionals are internally consistent: for unpacked NON-FULL parts (`physicalSignificance.NONE` / `.COMPOUND`), neither `partTransform.position` nor `part.rb.position` is set — these parts follow their parent, so manual placement is correctly avoided. The pairing at `:99-:101` exactly mirrors AdmiralRadish's PR #628 pattern.

`VesselProtoUpdater.cs` is out of scope (no Unity transform mutations; pure `protoVessel.*` field assignments).

## Disposition

**No code change needed.** PR #628 fully covers the directory. Closing BUG-014 as fixed-by-upstream.

## Out of scope / deferred

- **Distance-gated subspace merge** (brainstorm Option B for Bug 4) — when two players are within 2.5km in different subspaces, propose a subspace merge to the trailing player. **Defer as RFC.** Critic flagged this is not a small fix: KSP's physics-load range is engine-enforced, merge has to be opt-in, server must not load the other vessel into physics until acceptance, plus burn/SAS-hold edge cases. File as a separate design doc, not in this bug-fix line.

## Test plan

- **No automated test path** until Stage 4 mock-client harness. Until then: visual + soak (two clients sharing physics range with a large vessel).
- After Stage 4: regression test pinning the no-jitter behavior with a vessel in two players' physics range.

## Dependencies

- **None on our other bugs.** This is an audit pass over an isolated extension-methods directory.
- **Cross-fork: PR #628 is upstream and already merged into our master** (we are 0 behind upstream, 5 ahead — see [git status]). Any new audit should diff from `upstream/master` to avoid re-doing his work.

## Risks

- **Touching code AdmiralRadish recently rewrote.** Highest collision area among our six bugs. Strategy: read his commit (`1b5fc45b`) closely, treat any remaining sites we change as additive extensions of his pattern, not replacements. Cite his commit hash in our commit messages.

## Open questions

- **How many sites remain?** Unknown until inventory completes. Could be 0 (PR #628 covered everything) or 10+. The inventory itself takes <30 minutes.
- **Are there packed-vessel sites that should ALSO touch `rb.*`?** Probably not — packed parts are kinematic. Verify in inventory.
