# Stage 5.18g — Untrusted-cohort hardening — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `3e9ea269` (S7 docs ship)
**Discipline:** Per [[feedback-breakage-analysis]] — mandatory before non-trivial changes.
**Motivation:** The 2026-05-19 full-analysis pass (this session) identified two BLOCKING grief vectors against an untrusted-cohort multiplayer test: (1) `AgencyCurrencyRouter` writes `agency.Funds = msg.Funds` with no NaN/Infinity guard, and (2) `AgencyContractRouter` lacks a per-`ContractGuid` claim lock, so two agencies receiving the same Offered contract via the v3 live relay (`042d2cb5`) can both Accept it inside one tick and both get the per-agency Active record. Plus two minor UX/doc fixes surfaced by the same audit.

**Policy choices (operator-confirmed this session):**
- Currency: reject NaN/Infinity only; no upper cap. Silent server log + drop. No new wire message, no client UI.
- Contract: per-`ContractGuid` `ConcurrentDictionary` TryAdd. First Accept wins; collisions silent-drop with operator-visible Warning log. No new wire message, no client refund/toast.

---

## Scope lock — IS

### 1. Currency NaN/Infinity guard

- Edit [Server/System/Agency/AgencyCurrencyRouter.cs](../../Server/System/Agency/AgencyCurrencyRouter.cs) `TryRouteFunds` / `TryRouteScience` / `TryRouteReputation`.
- Add a `RejectIfNonFinite(double value, string field, ClientStructure client)` helper that returns `true` if the value is `double.IsNaN || double.IsInfinity`; logs at `Warning` with `[fix:per-agency-career]` tag + player name + field + value; returns from the router with `true` (handled → caller skips legacy path; mutation dropped).
- Call sites: line 88 (Funds), line 111 (Science), line 129 (Reputation), each after `TryResolveAgency` returns true.
- **Why return true (not false):** returning false would fall through to the legacy shared-agency path, which would write the same NaN to `Funding.Instance`. Return true to signal "handled — but mutation rejected."
- **Rejection echo:** `AgencySystemSender.SendStateTo(client, agency)` already runs from the success path; on rejection we still want the sender's local KSP `Funding.Instance` to converge with the (unchanged) server state. Send the unchanged state back so the cheat-attempt client's local mutation snaps back to authoritative. No new message — reuses `AgencyStateMsgData`.

### 2. Contract claim TryAdd

- Edit [Server/System/Agency/AgencyContractRouter.cs](../../Server/System/Agency/AgencyContractRouter.cs).
- New `private static readonly ConcurrentDictionary<Guid, Guid> _claimedContracts = new()` — key `ContractGuid`, value the claiming `AgencyId`.
- Modify `ApplyPerAgencyBatch` (around line 143-186):
  - For each contract in `entries`, attempt `_claimedContracts.TryAdd(contract.ContractGuid, agencyId)`.
  - If TryAdd returns false AND the existing value != agencyId → already claimed by a different agency. Skip Upsert + skip echo + log Warning. Drop from `echoEntries`.
  - If TryAdd returns true (first claim wins) OR existing == agencyId (idempotent re-Accept by same owner) → proceed with Upsert + echo.
- **Claim lifetime:** entries remain in `_claimedContracts` for the server process lifetime. Memory cost: ~32 bytes per Accepted contract guid; bounded by the number of Accept-route events. For a long-running server, consider eviction on `Completed`/`Failed`/`Cancelled` to bound growth — **deferred** (the BUG-025 v2 tech-purchase claim has the same lifetime issue and is acceptable for v1; revisit if profiling shows growth >100K entries).
- **Persistence:** `_claimedContracts` is in-memory only. On server restart, the claim set rebuilds from `LoadExistingAgencies`'s `AgencyState.Contracts` scan — add a one-time boot pass in `AgencySystem.LoadExistingAgencies` that walks each agency's `Contracts` and pre-seeds `_claimedContracts`. This closes the post-restart double-claim window.
- **Reset hook:** add `ResetClaimedContracts()` to support `MockClientTest` test isolation (matches the existing `ScenarioStoreSystem.CurrentScenarios.Clear()` precedent in `ServerHarness.ResetPerTestState`).

### 3. StatusDrawer button gate

- Edit [LmpClient/Windows/Status/StatusDrawer.cs:73](../../LmpClient/Windows/Status/StatusDrawer.cs#L73).
- Add `&& AgencySystem.Singleton.LocalAgencyId != System.Guid.Empty` to the toggle-render predicate.
- Closes the v1 soak Finding 1 dead-button race between `SettingsReply` (ch 2) and `AgencyHandshake` (ch 22). Button now hidden until handshake completes; reappears with full window-open behavior.

### 4. Status-doc fix

- Edit [docs/research/05c-per-agency-completeness-status.md:345](../../docs/research/05c-per-agency-completeness-status.md#L345).
- Change `SCANcontroller (SCANsat)` row from "Incomplete / unsafe" to "Per-agency complete" with router `AgencyScanRouter` / projector `SpliceAgencyScansatIntoScenario` / tests `AgencyScanRouterTest`, `AgencyScanProjectorTest`, `AgencyStateSCANsatRoundTripTest`, `AgencyTransferAgencySCANsatMigrationTest`, `AgencyScanRoutingTest`. Match the row format of the kolony/planetary/orbital rows above.

---

## Scope lock — IS NOT

- **NOT** a configurable cap on currency values. Operator-confirmed minimum scope.
- **NOT** a client-side currency refund + toast. No new `AgencyMutationRejectedMsgData`. Currency rejection is silent to the cheating client — local mutation snaps back via the unchanged `AgencyStateMsgData` echo.
- **NOT** a client-side contract refund + toast. Losing client sees the contract drop from their Offered list on next scenario sync; no toast. No new `AgencyContractRejectedMsgData`.
- **NOT** a fix for AgencySystem.cs / AgencyScenarioProjector.cs file-size violations (>900 line hard cap). Documented as nice-to-have in the open-items list; not in this slice.
- **NOT** any change to the shared-pool relay in `ApplySharedBatch` (Offered/Generated states route to peers unchanged). The race only exists on the per-agency Accept path.
- **NOT** WOLF Phase 4. WOLF pre-spec is the next slice after this lands and a v4 release is cut.
- **NOT** any wire-protocol change. Protocol stays at 0.31.0.

---

## Edge cases enumerated

### Currency

1. **NaN sent on Funds** — `RejectIfNonFinite` returns true; Warning logged with player name + "Funds=NaN"; mutation dropped; `AgencyStateMsgData` echo with unchanged server state snaps the cheating client back. Pinned by new ServerTest `Currency_NaN_Funds_Rejected`.
2. **Infinity sent on Science** — same path. Pinned by `Currency_Infinity_Science_Rejected`.
3. **-Infinity sent on Reputation** — `double.IsInfinity` returns true for both +∞ and -∞. Pinned by `Currency_NegativeInfinity_Reputation_Rejected`.
4. **double.MaxValue** — legal IEEE-754 number, `IsNaN` + `IsInfinity` both false. **Passes through**. Operator-acceptable per minimum-scope policy. Documented in the router XML as known limitation.
5. **Legitimate negative Funds** (KSP allows brief negatives, e.g., contract failures) — finite, not rejected. Pinned by `Currency_NegativeFundsFinite_Accepted` (regression guard).
6. **Gate off** — early return via `TryResolveAgency`; no rejection check runs. Pinned by `Currency_GateOff_AnyValuePassesThrough`.

### Contract

7. **Same contract Accepted twice by same agency** — idempotent. TryAdd returns false with existing == agencyId; proceed with Upsert + echo. Pinned by `Contract_SameAgencyReAccept_StillUpserted`.
8. **Two agencies Accept same Offered contract** — first wins via TryAdd; second sees TryAdd false + existing != agencyId; skip Upsert; log Warning; drop from `echoEntries`. Pinned by `Contract_TwoAgenciesSimultaneousAccept_OnlyFirstWins`.
9. **Server restart between Accept and next handshake** — `LoadExistingAgencies` pre-seeds `_claimedContracts` from disk; second-agency Accept after restart still loses. Pinned by `Contract_PreSeedFromDisk_PostRestartClaimRejected`.
10. **`_claimedContracts` reset between tests** — `ServerHarness.ResetPerTestState` calls `AgencyContractRouter.ResetClaimedContracts()`; tests stay isolated.
11. **Per-contract exception isolation** — TryAdd cannot throw; existing per-contract try/catch around Upsert preserves the batch-isolation contract.

### StatusDrawer

12. **Handshake completes mid-render** — `LocalAgencyId` flips from Empty to non-Empty between frames; toggle appears the next frame. No flicker (button is hidden, not greyed).
13. **Disconnect mid-session** — `LocalAgencyId` reset to Empty on disconnect; toggle disappears. Matches existing PerAgencyCareerEnabled disappearance.

---

## Test plan

### New ServerTest cases (target: ~8 new)

- `AgencyCurrencyRouterValidationTest` (new file):
  - `Funds_NaN_Rejected_StateUnchanged`
  - `Funds_PositiveInfinity_Rejected_StateUnchanged`
  - `Science_NegativeInfinity_Rejected_StateUnchanged`
  - `Reputation_NaN_Rejected_StateUnchanged`
  - `Funds_NegativeFinite_Accepted` (regression — KSP allows brief negatives)
  - `Funds_DoubleMaxValue_Accepted_KnownLimitation` (documents minimum-scope policy)

- `AgencyContractRouterClaimTest` (new file):
  - `Contract_FirstAccept_ClaimRecorded`
  - `Contract_SameAgencyReAccept_Idempotent`
  - `Contract_DifferentAgencySecondAccept_DroppedWithWarning`
  - `LoadExistingAgencies_PreSeedsClaimsFromDisk` (boot-time pre-seed)
  - `ResetClaimedContracts_ClearsAllClaims` (harness reset)

### Existing tests must stay green

- `AgencyCurrencyRouterTest` (current happy-path coverage) — no behaviour change for finite values.
- `AgencyContractRoutingTest` (e2e MockClientTest) — unchanged for single-agency Accept paths.
- All other Stage 5 / MKS / mod-compat tests — unaffected by these changes.

### No new MockClientTest (intentional)

The TryAdd race is a synchronous server-side decision; pinning it at the unit level in `ServerTest` is sufficient. Adding a multi-client e2e for the race would require harness-level scheduling control (two `MockNetClient` Accepts in the same server tick) that the current harness doesn't provide. Documented as a soak-attention item — operators should manually verify two-client Accept-on-same-contract in the v4 multiplayer test.

---

## File inventory (expected diff)

| File | Change | Lines |
|------|--------|-------|
| `Server/System/Agency/AgencyCurrencyRouter.cs` | +`RejectIfNonFinite` helper + 3 call sites | ~+25 |
| `Server/System/Agency/AgencyContractRouter.cs` | +`_claimedContracts` dict + TryAdd in `ApplyPerAgencyBatch` + `ResetClaimedContracts` + `PreSeedClaimsFromAgencyState` | ~+40 |
| `Server/System/Agency/AgencySystem.cs` | +1 line in `LoadExistingAgencies` to call `AgencyContractRouter.PreSeedClaimsFromAgencyState` | ~+3 |
| `LmpClient/Windows/Status/StatusDrawer.cs` | +1 conjunction in toggle-render predicate | ~+1 |
| `docs/research/05c-per-agency-completeness-status.md` | SCANsat row updated | ~+1/-1 |
| `Server/ForkBuildInfo.cs::ActiveFixes` | +`"5.18g-untrusted-cohort-hardening"` entry | +1 |
| `MockClientTest/Util/ServerHarness.cs` | +1 line in `ResetPerTestState` to call `AgencyContractRouter.ResetClaimedContracts` | ~+1 |
| `ServerTest/AgencyCurrencyRouterValidationTest.cs` | new file, 6 cases | ~+150 |
| `ServerTest/AgencyContractRouterClaimTest.cs` | new file, 5 cases | ~+130 |
| `CLAUDE.md` | test count bump + Stage 5.18g entry + Stack Notes entry for the claim lifetime/persistence contract | ~+15 |

Total: ~+370 lines / ~-1 line across 10 files. Below the soft-cap thresholds for affected files.

---

## Lens-framing for reviewers (before commit)

Per [[feedback-review-lens-framing]] + [[feedback-integration-logic-review]]:

1. **General correctness** — TryAdd race correctness, NaN/Infinity coverage of IEEE-754 edge cases, lock interleaving with existing `AgencySystem.GetAgencyLock`.
2. **Consumer (5.19 hardening author / future feature author)** — does the `_claimedContracts` lifetime contract degrade gracefully under eviction (when we add it)? Is the silent-rejection-on-cheat-currency observable enough for operators to debug a misbehaving client?
3. **Upgrade (operator restarting a populated v3 universe)** — does `LoadExistingAgencies` correctly pre-seed claims from existing per-agency `Contracts`? Are there any race windows during boot where a connecting client could Accept a not-yet-pre-seeded contract?
4. **Integration-logic flow trace** — full request path for "Agency A and Agency B both have Offered contract X; both click Accept within 10ms; what reaches disk + what reaches each client?" + "Server restarts; Agency A had contract X Accepted; Agency B connects first and tries to Accept X; what happens?"

---

## Receipt requirement

Per `.claude/hooks/require-bug-review.sh`, this commit requires a matching review receipt under `.claude/review-receipts/<sha1>.txt` documenting the multi-lens review pass. Receipts authored AFTER applied fixes; review must be from a fresh-context independent agent (not self-pass per [[feedback-independent-review]]).
