# BUG-005 / BUG-006 — Cross-subspace `UnloadedUpdate` lock + vessel disappear/duplicate

**Implementation order:** **6th (capstone)** in the Option C sequence. Heaviest change. Protocol break.

**Status:** Validated against `master` at commit `48df64bd` (2026-05-16). Diagnoses from [03-time-sync-fix-brainstorm.md](../03-time-sync-fix-brainstorm.md#bug-3--unloadedupdate-lock-across-subspaces) verified by direct code read.

**Inventory entries:**
- BUG-006 (#292 "Player often takes lock UnloadedUpdate on vessels in different subspace") — closed 2019 with the wrong fix (Option C "disable the broadcasts" — see commit `fbc7a8c`).
- BUG-005 (#421 / #483 / #506 / #481 "Vessels disappear or duplicate seemingly at random") — open, four years of reports. Downstream symptom of the same lock-keying gap.

One fix addresses both. Bundled together because they share root cause and the protocol break should happen exactly once.

---

## Symptom

A player in subspace A can claim authority over a vessel that physically lives in subspace B's timeline. Manifests as:
- Ship rewinds after logout (#400)
- Random craft deletion (#483)
- Despawning ships (#506)
- Random duplication / disappearance (#421)

## Code locations (validated)

- [LmpCommon/Locks/LockDefinition.cs:8-29](../../../LmpCommon/Locks/LockDefinition.cs#L8-L29) — `LockDefinition` fields: `PlayerName`, `KerbalName`, `VesselId`, `Type`. **No subspace dimension.**
- [LmpCommon/Locks/LockDefinition.cs:115-122](../../../LmpCommon/Locks/LockDefinition.cs#L115-L122) — `Equals` keys on `(PlayerName, VesselId, Type, KerbalName)`. Confirms registry shape.
- [Server/System/LockSystem.cs:7-49](../../../Server/System/LockSystem.cs#L7-L49) — `LockStore` is a flat dictionary; `AcquireLock` does NOT consult requester's subspace vs vessel's subspace. Force/exists check on `LockDefinition` alone.
- [LmpClient/Systems/Lock/LockSystem.cs](../../../LmpClient/Systems/Lock/LockSystem.cs) — client-side `AcquireUnloadedUpdateLock` has no subspace check before requesting.
- [Server/System/WarpSystem.cs:34-51](../../../Server/System/WarpSystem.cs#L34-L51) — `RemoveSubspace` refuses removal if any client occupies the subspace, but **ignores vessels.** Relevant interaction noted under "Pruning" below.
- Commit `fbc7a8c` (upstream) — the "fix" that shipped for #292; commented out `SendUnloadedSecondaryVesselPositionUpdates` + `SendUnloadedSecondaryVesselUpdates`. Hides symptom; root cause remains.

## Diagnosed root cause (validated)

Lock registry is keyed only by `(LockType, VesselId, PlayerName, KerbalName)`. DMP has the same omission (`Dictionary<string, string> serverLocks`, no subspace dimension); LMP inherited it. progfz's #292 write-up identified the problem and proposed three fixes:

- **Option A** (recommended) — Don't grant lock if requester is in a different subspace from the vessel
- **Option B** — Priority lock for last-controller; only direct control or Update lock overrides
- **Option C** — Disable `UnloadedUpdate` broadcasting entirely

gavazquez merged **Option C** as `fbc7a8c`. This silenced the most visible symptoms but the root cause remains: locks are still acquired across subspaces, so the underlying authority confusion still drives the downstream BUG-005 symptoms (#400, #483, #506, #421).

## Recommended fix (Option A — protocol bump)

**Approach:**
1. Server tracks each vessel's `AuthoritativeSubspaceId` = whichever subspace last sent a `VesselProtoUpdate` for it.
2. Lock key changes from `(LockType, VesselId, PlayerName, KerbalName)` to **`(LockType, VesselId, AuthoritativeSubspaceId, PlayerName, KerbalName)`**.
3. Server rejects `ACQUIRE` when requester's subspace is **in the past** relative to the vessel's `AuthoritativeSubspaceId`. Other types (Asteroid, Contract, Spectator, Kerbal) are unaffected — they have no vessel-subspace dimension.
4. Restore the routines that `fbc7a8c` commented out (now safe because locks are properly partitioned).
5. `RemoveSubspace` ([Server/System/WarpSystem.cs:34](../../../Server/System/WarpSystem.cs#L34)) must also refuse removal when **any vessel** has that subspace as its `AuthoritativeSubspaceId`. O(n_vessels) per disconnect — acceptable for typical scales but worth measuring.

**Migration:** Clean break, no shim. Bump `LMP_PROTOCOL_VERSION`. Critic explicitly flagged: any compatibility shim has to fabricate the missing subspace dimension from old clients, and every fabrication choice recreates the bug:
- Use `requester.CurrentSubspace` → original bug.
- Use `LatestSubspace` → punishes past-subspace players.

Pre-bump clients are rejected at handshake with a "protocol bumped, please upgrade" message.

**Fork strategy implication:** This is a hard protocol break and we are on fork master. Anyone running our fork's server needs our fork's client from this point forward. Anyone wanting to keep using vanilla LMP 0.29.x can stop pulling from our fork before this commit. Communicate this in the commit message and in CLAUDE.md "Stack Notes" once it ships.

## Out of scope / rejected alternatives

- **Option B (priority lock for last-controller)** — smaller scope but doesn't fix cross-subspace authority confusion. Two players in different subspaces could still fight over a vessel. Reject.
- **Option C (re-disable broadcasts)** — already shipped upstream as `fbc7a8c`; that's the wrong fix. We restore the broadcasts as part of Option A.
- **Authoritative-subspace per *part* instead of per vessel** — overly granular; KSP doesn't model partial vessel authority. Reject.

## Test plan

**Server tests (extend `LockSystemTest`):**
- Vessel V is owned by subspace A (last proto-update came from a player in A).
- Player P1 in subspace A acquires `UnloadedUpdate` lock on V → grant.
- Player P2 in subspace B (where B < A in time order) acquires same lock → reject.
- Player P2 in subspace B (where B > A in time order) acquires same lock → grant; V's `AuthoritativeSubspaceId` flips to B on next proto-update.
- Concurrent acquire with same subspace → exactly one grant (existing CreateSubspaceLock pattern).

**Vessel ownership tests (new `VesselAuthorityTest`):**
- First proto-update from any client sets `AuthoritativeSubspaceId`.
- Subsequent proto-updates from a same-or-future subspace update it.
- Proto-update from a strictly-past subspace is rejected (vessel state cannot rewind).

**`RemoveSubspace` test:**
- Subspace S has 0 clients but is the authoritative subspace for vessel V → `RemoveSubspace(S)` returns false.

**Client tests (Stage 4 mock harness):**
- Repro #421 / #483 / #506 / #481 scenario (vessel disappears/duplicates after subspace mismatch) → no longer reproduces.

## Dependencies

- **Hard dependency:** none of the other five bugs. Bug 3 is self-contained.
- **Soft dependency (sequencing only):** Bug 5a server dedup (BUG-051a) lands FIRST so that the protocol bump introduced here doesn't have to also introduce request-dedup semantics simultaneously.
- **Vessel ownership invariant must be written down BEFORE coding the migration.** Open questions below.

## Risks

- **Highest blast radius of any of the six fixes.** A bug in the new lock-keying logic could turn cross-subspace deny-list into a deny-everything case, which is "vessel control mysteriously broken for all players" — worse than the bug we are fixing.
- **AdmiralRadish coordination** — per the fork-master strategy memo, no coordination up front. But docking/coupling work intersects `LockType.UnloadedUpdate` handoff during dock-to-undock transitions. **`AuthoritativeSubspaceId` must reassign on docking (two-becomes-one) and undocking (one-becomes-two).** Audit AdmiralRadish's recent vessel-coupling commits (#660 Fierce-Cat draft, #687 merged) before designing the dock/undock handoff. Decide adopt/edit/replace per the strategy memo.
- **Backup race** ([BUG-033](../01-bug-inventory.md), open) — if `BackupSubspaces` reads `WarpContext.Subspaces` while a vessel's `AuthoritativeSubspaceId` reassignment is in flight, we could serialize an inconsistent state. Audit during design.
- **Test coverage on existing `LockSystem`** — the server-side flat dictionary has minimal tests today. Add tests as part of this fix, not as a separate cleanup pass.

## Open questions

- **Initial `AuthoritativeSubspaceId`?** Options: (a) on first proto-update from any client, (b) on first ACQUIRE, (c) inherited from previous owner on dock/undock. Brainstorm critic asked the same — needs a written invariant.
- **When a docked pair undocks, which becomes the parent vessel's `AuthoritativeSubspaceId`?** Probably "the player who initiated undock decides." Verify against `VesselCoupleSystem`.
- **Vessel persistence (`Vessels/` directory):** does `AuthoritativeSubspaceId` go in the vessel proto on disk, or in a separate registry file? If in the proto, schema change — minor.
- **Past-subspace player joining a future-authoritative vessel:** brainstorm says reject ACQUIRE. UX needs to message this clearly — "this vessel exists in a later subspace; sync forward to take control."
