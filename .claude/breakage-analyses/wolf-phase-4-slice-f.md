# WOLF Phase 4 Slice F — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `d726dc46` (Slice E — CrewRoutes router + cross-agency kerbal authority gate)
**Discipline:** Per [[feedback-breakage-analysis]] — mandatory before non-trivial changes.
**Motivation:** Close out Phase 4 by wiring the migration helpers that the prior 5 slices punted on. The load-bearing piece is the `/deleteagency` cascade: WOLF CrewRoutes hold passengers in `RosterStatus.Missing` + `SetTimeForRespawn(double.MaxValue)` while in flight; if the agency owning the route is wholesale-deleted before the route completes, those kerbals stay Missing forever with no in-band recovery path. Slice F's cascade restores in-flight kerbals to the AC pool before TryDeleteAgency removes the agency. The companion admin commands (`transferagency` owner-rename, `setvesselagency`) need only documentation — WOLF entities are body+biome or Guid keyed, not vessel-keyed, so no per-router migration is required.

---

## Scope lock — IS

### 1. `AgencyWolfMigration.CascadeOnDelete` (new helper, single static class)

NEW FILE: `Server/System/Agency/AgencyWolfMigration.cs` (~150 lines).

**Single public entry point:** `AgencyWolfMigration.CascadeOnDelete(AgencyState agency) → CascadeResult`.

**Two-phase execution:**

1. **Snapshot phase** (under `lock (AgencySystem.GetAgencyLock(agency.AgencyId))`):
   - Walk `agency.WolfCrewRoutes.Values`.
   - For each route with `FlightStatus == "Enroute"` OR `FlightStatus == "Arrived"`, enumerate `route.Passengers`.
   - Collect distinct passenger `Name` values into a `HashSet<string>`.
   - Lock released immediately after snapshot (disk I/O does NOT run under the per-agency lock — FileHandler's own per-path lock handles kerbal-file concurrency).

2. **Restoration phase** (no agency lock held):
   - For each unique passenger name, call `TryRestoreKerbalToAcPool(name)`.
   - Helper opens `Universe/Kerbals/{name}.txt` via `FileHandler.ReadFileText`, rewrites two lines:
     - `state = <anything>` → `state = Available`
     - `ToD = <anything>` → `ToD = 0`
   - Writes back via `FileHandler.WriteToFile(path, string)`.
   - Per-kerbal try/catch isolation: a malformed or missing file logs a Warning and contributes to a `FailedKerbalNames` list but does NOT abort sibling kerbals.

**Restoration scope rationale.** Per source walk (`F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Modules/WOLF_CrewTransferScenario.cs:586-590` + `:155`), the `rosterStatus = Missing` + `SetTimeForRespawn(double.MaxValue)` mutations happen only at Launch (Boarding→Enroute transition). Boarding-only passengers are still on their source vessel as normal crew; KSP's stock vessel-demote path handles them via the existing `TryDeleteAgency` vessel-Empty demote loop. Arrived passengers stay Missing until operator clicks Disembark (`CheckArrived` only mutates `FlightStatus`, not `rosterStatus` — verified `CrewRoute.cs:105-121`). So {Enroute, Arrived} is the correct scope — restoring Boarding would gratuitously stomp Assigned→Available for kerbals that don't need it.

**No dict.Clear() on the 5 WOLF dicts.** TryDeleteAgency's existing precedent: it removes the AgencyState from `Agencies` + `AgencyByPlayerName`, deletes the on-disk file, GCs the agency lock, and evicts contract claims. It does NOT call `.Clear()` on `KolonyEntries` / `OrbitalTransfers` / `Contracts` / `TechNodes` / etc. — those dicts vanish with the unreachable AgencyState reference. The 5 WOLF dicts get the same treatment. Adding explicit `.Clear()` calls would be operationally pointless (AgencyState is removed from `Agencies` and projector / router lookups all go through `Agencies.TryGetValue` — they can't see a deleted-but-not-cleared AgencyState). The pickup memo's "clear 5 dicts" framing was incorrect; ground-truth precedent supersedes.

**CascadeResult shape:**
```csharp
internal sealed class CascadeResult
{
    public int InFlightRoutesScanned;
    public int RestoredKerbalCount;
    public List<string> RestoredKerbalNames;
    public List<string> FailedKerbalNames;
}
```

The `InFlightRoutesScanned` count + per-name lists feed the operator-visible audit log in `DeleteAgencyCommand`.

### 2. `DeleteAgencyCommand` cascade slot

EDIT: `Server/Command/Command/DeleteAgencyCommand.cs`.

- Insert `AgencyWolfMigration.CascadeOnDelete(source)` call BETWEEN the snapshot-identity block (currently ending at line 135 with `oldOwnerClient`) and the `TryDeleteAgency` call at line 137.
- Wrap in `try`/`catch` mirroring the existing `BackupSystem.RunBackup` catch at line 178-191: any exception in cascade logs an Error and proceeds — the agency-file deletion still happens, otherwise we'd leak an orphan agency file with no in-memory record.
- After `TryDeleteAgency` succeeds, emit operator-visible log lines for the cascade summary + per-restored-kerbal lines (grammar matches existing per-vessel demote lines at L209-213).
- Cascade result is emitted under the new `[fix:WOLF-R4]` tag (matches the existing WOLF-R4 tag at Slice E `[fix:WOLF-R4] crew-route entry skipped` lines).

**Race window — narrow, accepted.** Between cascade-lock-release and TryDeleteAgency's lock-acquire (microseconds), a new `AgencyWolfCrewRouter.TryRoute` postfix from the demoted agency's owner could accept a new Launch, adding fresh passengers in `Missing` state via a subsequent `KerbalSystem.HandleKerbalProto`. Those new passengers would be orphaned post-delete. Mitigation: existing operator workflow advice at `DeleteAgencyCommand.cs:226-236` already directs operators to `/kick` the prior owner — kicked players cannot initiate Launches. The race is operationally closed by following the documented workflow. Mirrors the same race posture as the existing transferagency stale-handle quirk.

### 3. `TransferAgencyCommand` WOLF NO-OP doc

EDIT: `Server/Command/Command/TransferAgencyCommand.cs`.

- Add a single comment block in the class XML (after the existing "Renamed prior owner reconnects" paragraph) explicitly noting that WOLF state (all 5 dicts) is preserved unchanged across `/transferagency`. Rationale: AgencyId is stable across owner-rename per Phase 3 Slice E precedent; the 5 WOLF dicts are body+biome / Guid keyed, not OwningPlayerName keyed. NO code change.
- The comment block is structured to prevent a Slice G+ maintainer from accidentally adding a redundant WOLF migration walk.

### 4. `SetVesselAgencyCommand` WOLF NO-OP doc

EDIT: `Server/Command/Command/SetVesselAgencyCommand.cs`.

- Add a comment block in the class XML (after the "Connected-source-owner handling" paragraph) noting:
  - The 4 body+biome / Guid-keyed WOLF dicts (Depots / Routes / Hoppers / Terminals) don't carry vessel references — vessel reassignment never moves WOLF entries.
  - The CrewRoute case is non-obvious: `setvesselagency` on a kerbal currently in an Enroute CrewRoute is also a NO-OP because the CrewRoute's passenger list is fixed at `CreateCrewRoute` time per WOLF source contract. The kerbal's enrolment doesn't follow the vessel-reassign.
  - NO code change.

### 5. `ForkBuildInfo` umbrella WOLF-R4 update

EDIT: `Server/ForkBuildInfo.cs:47`.

- REPLACE the existing single-paragraph WOLF-R4 entry text (currently documents Slice B only) with a comprehensive multi-paragraph comment covering Slices A through F.
- Stylistic choice ratified vs MKS-R2 precedent (`Server/ForkBuildInfo.cs:45`).
- Boot banner emits one `[fix:WOLF-R4]` line as before; the entire Phase 4 narrative lives in the source comment for code-reading operators.

### 6. New ServerTest — `DeleteAgencyCommandWolfCascadeTest.cs`

NEW FILE: `ServerTest/DeleteAgencyCommandWolfCascadeTest.cs` (~7 cases). Tests the cascade helper directly via `AgencyWolfMigration.CascadeOnDelete` — pinning the new logic without bringing the full `DeleteAgencyCommand` command parser path up.

| # | Case | Asserts |
|---|------|---------|
| 1 | `Cascade_NoCrewRoutes_RestoresZero` | Empty `WolfCrewRoutes` returns CascadeResult with InFlightRoutesScanned=0, RestoredKerbalCount=0 |
| 2 | `Cascade_BoardingOnly_RestoresZero` | Boarding routes have passengers but they aren't yet in Missing (per WOLF source); cascade skips them |
| 3 | `Cascade_EnrouteWithPassengers_RestoresAll` | Single Enroute route with 3 passengers; all 3 kerbal files have `state = Missing` and `ToD = MaxValue` rewritten to `state = Available` and `ToD = 0` |
| 4 | `Cascade_ArrivedWithPassengers_RestoresAll` | Arrived route (passengers still in Missing per CheckArrived contract) gets restored too |
| 5 | `Cascade_MultiplePassengersAcrossRoutes_DedupesByName` | Same kerbal name in two routes (defensive — should not happen but per WOLF invariant) restores file once |
| 6 | `Cascade_MissingKerbalFile_IsolatedAsFailure` | Passenger references kerbal file that doesn't exist; cascade logs Failed name but continues siblings |
| 7 | `Cascade_MalformedKerbalFile_IsolatedAsFailure` | Kerbal file present but missing the `state =` field; cascade logs Failed and continues |

Setup writes kerbal files to a temp `KerbalsPath` (use `ServerHarness`'s existing `[AssemblyInitialize]` Universe setup; per-test reset via `AgencySystem.Reset`).

### 7. CLAUDE.md updates

EDIT: `f:/luna-multiplayer/CLAUDE.md`.

- **Stack Notes & Patterns Learned** — add a Phase 4 entry summarising the 5-slice arc + the cross-agency kerbal authority gate as the distinctive Phase 4 surface (vessel-proxy authority via K1-pattern scan) + the EnsureDepotKeySet extraction lesson + the Slice E integration-logic-lens catch (CheckArrived auto-transition) + Slice F's kerbal-restoration design (snapshot-then-restore, narrow-race acceptance).
- **Server System Inventory table** — update the `AgencySystem` row to list the 5 WOLF routers + the new `AgencyWolfMigration` helper.
- **Stage Roadmap** — close out the WOLF Phase 4 block; flag v4 cut as the next gate.

---

## Scope lock — IS NOT

- **NOT shipping wire broadcast for the cascade.** The demoted agency's client (if connected) is about to receive an `AgencyVisibilityMsgData` demoting their vessels + the existing `/kick` recommendation. Adding a per-family `AgencyWolfXxxStateMsgData` with `RemovedKeys=...` adds wire surface for an audience that's expected to disconnect. The upgrade-lens explicitly signs off on skipping.
- **NOT touching `WOLF_CrewTransferScenario.Launch` client-side prefix to suppress cross-agency-kerbal embark UX flap** — deferred per pre-spec §8.f to operator-demand-driven future slice. Modified-client desync is structurally acceptable; legitimate-client UX is the in-game prefix that doesn't exist yet.
- **NOT touching chunking on `SendWolfCrewRouteCatchupTo`** — inherited gap from Slice B through E per [[feedback-wire-msgdata-chunking-caps]]; separate workstream that closes for all 5 WOLF families together.
- **NOT clearing the 5 WOLF dicts** on the demoted AgencyState — see Scope §1 rationale (matches TryDeleteAgency precedent of not clearing other per-agency dicts).
- **NOT extending TryDeleteAgency to accept a cascade callback** — the cascade runs OUTSIDE TryDeleteAgency's lock window (disk I/O on kerbal files belongs in FileHandler's domain, not the agency lock). Refactoring TryDeleteAgency to invoke a callback under its lock would extend the agency-lock window unnecessarily.
- **NOT touching `LmpClient/` or `LmpCommon/`** — Slice F is server-only. Client-side AgencyMessageHandler stubs from Slice E remain untouched.
- **NOT bumping the protocol version** — Slice F adds no wire messages.

---

## Edge cases enumerated

1. **Demoted agency has zero WOLF CrewRoutes.** Cascade returns CascadeResult with zero counts; DeleteAgencyCommand logs `[fix:WOLF-R4] deleteagency {id} cascade in-flight=0 restored=0` once. No-op for the happy case.
2. **Demoted agency has Boarding-only routes.** Snapshot phase skips Boarding routes (rosterStatus is Assigned, not Missing); restoration phase has empty work set. Operator log shows in-flight=0.
3. **In-flight route's passenger has been hand-deleted from disk by an operator.** `FileHandler.ReadFileText` throws or returns empty; cascade catches per-kerbal, logs Warning, marks Failed, continues.
4. **Kerbal file exists but `state =` line is missing.** Defensive: the regex finds no match; cascade logs Warning ("kerbal file at {path} missing 'state =' field — skipping restoration"), marks Failed, continues.
5. **Concurrent /deleteagency on a different agency.** The two cascades use different per-agency lock anchors; no contention. Restoration disk I/O serializes per kerbal file via FileHandler's per-path lock.
6. **Concurrent Launch on the demoted agency during the race window.** Mitigated by operator workflow (kick first); the new passengers' kerbals would stay Missing. Documented in cascade XML.
7. **Demoted agency has Arrived routes with passengers.** Per CheckArrived contract, these passengers ARE in Missing. Restored normally. (This is the case the s42 pickup memo missed — Boarding-vs-Arrived asymmetry.)
8. **Same kerbal name in two in-flight routes (data corruption).** HashSet dedup; single restoration. Logged Warning if detected: `kerbal '{name}' appears in 2 in-flight CrewRoutes — defensive dedup`.
9. **Cascade exception (e.g., disk-full mid-restoration).** Outer try/catch in DeleteAgencyCommand logs Error and proceeds with TryDeleteAgency. Some kerbals end up restored, others not. Operator-visible log lists which.
10. **Gate=off (`PerAgencyCareer=false`) at command time.** Existing gate-refuse path at `DeleteAgencyCommand.cs:72-78` returns false before cascade runs. Cascade never invoked under gate=off.

---

## Lock ordering

- Cascade snapshot: `AgencySystem.GetAgencyLock(agency.AgencyId)` only.
- Restoration: no agency lock; per-kerbal-file lock via FileHandler internal.
- TryDeleteAgency: `PlayerNameLocks[owner]` then `AgencyLocks[agencyId]`.
- No new lock ordering introduced. Cascade lock acquire-release happens entirely before TryDeleteAgency's acquire — no nesting.

---

## Tests to add

- `ServerTest/DeleteAgencyCommandWolfCascadeTest.cs` — 7 cases enumerated above.
- Existing `MockClientTest.AgencyWolfCrewRouteRoutingTest` (if present from Slice E) untouched.
- Projected counts: ServerTest 656 → 663. LmpClientTest / LmpCommonTest / MockClientTest unchanged.

---

## Files to touch

| File | Change |
|------|--------|
| `Server/System/Agency/AgencyWolfMigration.cs` | NEW. `AgencyWolfMigration` static class + `CascadeResult` + `CascadeOnDelete` + `TryRestoreKerbalToAcPool` |
| `Server/Command/Command/DeleteAgencyCommand.cs` | Insert cascade call between snapshot identity + TryDeleteAgency; add audit log emission |
| `Server/Command/Command/TransferAgencyCommand.cs` | Class XML comment block — WOLF NO-OP documentation. No code change |
| `Server/Command/Command/SetVesselAgencyCommand.cs` | Class XML comment block — WOLF NO-OP + Enroute-kerbal corollary. No code change |
| `Server/ForkBuildInfo.cs` | Replace WOLF-R4 entry comment with comprehensive Phase 4 umbrella |
| `ServerTest/DeleteAgencyCommandWolfCascadeTest.cs` | NEW. 7 cases |
| `CLAUDE.md` | Stack Notes Phase 4 entry + AgencySystem row update + Stage Roadmap close-out |

---

## After this slice ships

- Receipt at `.claude/review-receipts/<commit-sha>.txt`.
- Multi-lens review per [[feedback-review-lens-framing]] + [[feedback-integration-logic-review]].
- Branch tip moves from `d726dc46` to the Slice F commit.
- Memory updates: `[[project-wolf-phase-4-pickup]]` closes out; `[[project-per-agency-pickup]]` notes Phase 4 complete.
- v4 release cut is the next gate per `[[project-wolf-phase-4-pickup]]` release plan section.
