# BUG-014 — `VesselPositionSys/ExtensionMethods/` rb-set audit (continue PR #628's pattern)

**Implementation order:** **5th** in the Option C sequence. Can be interleaved earlier — units are small and isolated.

**Status:** **PRE-VALIDATION.** Diagnoses from [03-time-sync-fix-brainstorm.md](../03-time-sync-fix-brainstorm.md#bug-4--warp-at-distance--big-vessel-jitter) — the brainstorm itself flagged that the actual call-site inventory was not done. This Phase-2 doc is a placeholder for the inventory step.

**Inventory entry:** BUG-014 in `01-bug-inventory.md` is listed as "likely fixed in master" via upstream PR [#628](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/628) (merged 2026-04-17, commit `1b5fc45b`). This Phase-2 doc covers only the *remaining* sites PR #628 didn't reach.

---

## Symptom (remaining variant)

Large vessels (space stations) and physics-range-shared craft still jitter visibly in some scenarios after PR #628. The interpolation rotation/rb fix landed for unpacked rigidbodies, but extension methods that set `transform.position` / `transform.rotation` without a matching `part.rb.position` / `part.rb.rotation` create the same snap-back pattern PR #628 described.

## Code locations (preliminary — to be inventoried)

- [LmpClient/Systems/VesselPositionSys/ExtensionMethods/](../../../LmpClient/Systems/VesselPositionSys/ExtensionMethods/) — directory; needs full file-by-file walk.
- [upstream PR #628](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/628) — already-merged fix. Commit message reproduced verbatim from the brainstorm:
  > For unpacked (off-rails) parts with rigidbodies, only transform.position and transform.rotation were being set. Unity's physics engine treats this as an external teleport and snaps the rigidbody back toward its last solved position on the next FixedUpdate, causing visible vibration and shaking.

## First action: inventory

Before any code change, run the following audit (track output in a follow-up commit, replace this section with findings):

```
git log --oneline upstream/master -- LmpClient/Systems/VesselPositionSys/ExtensionMethods/
# Identify what PR #628 touched.

grep -rn "transform\.position\|transform\.rotation" LmpClient/Systems/VesselPositionSys/ExtensionMethods/
# Catalog every setter site.

# For each site:
#  - Is the target a packed (on-rails) part? If yes -> transform.* is the correct path; do not change.
#  - Is the target unpacked with a rigidbody? If yes -> needs matching rb.position / rb.rotation set.
#  - Did PR #628 already cover this site? Diff against upstream master.
```

Output target: `docs/research/02-analysis/bug-014-rb-audit-inventory.md` (sibling doc) listing each candidate site with disposition (covered / needs fix / not applicable).

## Recommended fix shape (post-inventory)

For each remaining unpacked-rigidbody site:

```csharp
// Before:
part.transform.position = newPos;
part.transform.rotation = newRot;

// After (matching PR #628's pattern):
part.transform.position = newPos;
part.transform.rotation = newRot;
if (part.rb != null)
{
    part.rb.position = newPos;
    part.rb.rotation = newRot;
}
```

Each fix is a small atomic commit. Recommend one PR per file or per logical group.

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
