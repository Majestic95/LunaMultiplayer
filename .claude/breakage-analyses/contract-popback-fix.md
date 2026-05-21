# Contract pop-back fix — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `898578e6` (fix(server,client,settings): banned-parts drift)
**Discipline:** Per [[feedback-breakage-analysis]] — mandatory before non-trivial changes.
**Motivation:** Live-soak finding. Per-agency Career players report "I cannot cancel contracts at all — when they cancel the contract, it pops right back up a second later." Reproduces under both per-agency (gate=on) and shared-agency (gate=off) modes — the bug is in client-side LMP event handling, not the per-agency router.

**Root cause.** When the user clicks Cancel on an Active contract (or Decline on an Offered contract), KSP's `Contract.Cancel()` sets state to Cancelled and fires `onCancelled`. LMP sends the state change to the server, which stores it correctly. But on the next `ContractSystem.Update` tick, KSP calls `GenerateContracts` to fill the now-empty slot. KSPCF's `ContractPreLoader` keeps a persistent contract cache that LMP populates via [`InjectServerContractsIntoPreLoader`](../../LmpClient/Systems/Scenario/ScenarioSystem.cs#L390) on every scenario load. KSPCF does not always evict the cancelled contract from that cache before the next tick — so CC's patched generator restores the just-cancelled contract from cache and KSP fires `onOffered` for it ~1 second after the click.

LMP's [`ContractOffered`](../../LmpClient/Systems/ShareContracts/ShareContractsEvents.cs#L513) handler had two branches that both preserved the re-Offer instead of withdrawing it:
1. The [`ServerOfferedContractGuids` check](../../LmpClient/Systems/ShareContracts/ShareContractsEvents.cs#L530) returned early because the GUID was still in the initial-load snapshot (that protection exists for CC's first-load re-fire path and was never invalidated when the user acts locally).
2. The lock-holder branch silently re-broadcast the re-Offer as a fresh Offered contract to the server, looping it back into the Available list.

Result: the cancelled contract reappeared in Mission Control's Available list ~1 second after the cancel click. No combination of clicks could keep it gone — every cancel triggered the same regeneration → pop-back cycle.

---

## Scope lock — IS

### 1. `LocallyActedOnContractGuids` session-scoped HashSet

NEW FIELD on `ShareContractsSystem`. `HashSet<string>` constructed with `StringComparer.OrdinalIgnoreCase`. Populated by the five per-action `ShareContractsEvents` handlers; cleared in BOTH `OnEnabled` and `OnDisabled` so a re-enable without a prior disable (per [[reference-client-system-enable-convention]]) cannot carry stale GUIDs into a new session. ~40 lines of XML doc + 2-line declaration.

### 2. `ShouldWithdrawReOffer` pure decision helper

NEW PUBLIC STATIC METHOD on `ShareContractsSystem`. Three-argument-free signature for unit testability — takes the GUID + the set as parameters:
```csharp
public static bool ShouldWithdrawReOffer(string contractGuid, ICollection<string> locallyActedOnGuids)
```
Returns true when the GUID is non-empty AND the set is non-null AND `set.Contains(guid)`. ~10 lines including XML.

### 3. Five per-action handler additions in `ShareContractsEvents.cs`

`ContractAccepted`, `ContractCancelled`, `ContractDeclined`, `ContractCompleted`, `ContractFailed` each add one line: `System.LocallyActedOnContractGuids.Add(contract.ContractGuid.ToString());` immediately before the existing `SendContractMessage` call. The set add is idempotent — re-firing the same handler in rapid succession is harmless.

### 4. `ContractOffered` guard

NEW BRANCH inserted at the top of `ContractOffered`, BEFORE the existing `ServerOfferedContractGuids` snapshot-protection check. ~15 lines including XML rationale. When `ShouldWithdrawReOffer` returns true, logs a `LunaLog.LogWarning` naming CC's preloader as the likely cause (operator triage breadcrumb) and calls the existing `WithdrawAndRemoveContract` helper.

### 5. New LmpClientTest file `ShareContractsReOfferDecisionTest.cs`

~110 lines. 7 cases pinning the helper across all branches: guid-in-set / guid-not-in-set / empty-set / null-set / null-guid / empty-guid / case-insensitive match.

---

## Scope lock — IS NOT

- **No change to `ServerOfferedContractGuids`** — that snapshot still does its original job (protecting initial-load CC re-fires). The new guard is a sibling check that runs first.
- **No change to the server-side `AgencyContractRouter`** — the router correctly stores Cancelled state per-agency. The bug is purely in the client's `ContractOffered` handler.
- **No change to KSPCF's `ContractPreLoader`** — Harmony patching CC's preloader to force cache eviction would be invasive + would couple us to KSPCF internals. The defensive withdraw is cheaper + version-stable.
- **No change to the existing `SendContractMessage` cadence** — Cancel/Decline still send to the server immediately, same as before.
- **No protocol bump** — wire shape unchanged.
- **No server binary change** — `LmpClient.dll` is the only changed artifact.
- **No persistence of `LocallyActedOnContractGuids`** — session-scoped only. A reconnect re-populates `ServerOfferedContractGuids` from the server snapshot, and contracts the user has already finalised are in the server's per-agency Cancelled list or CONTRACTS_FINISHED. No need to persist the "I cancelled it" intent across sessions.

---

## Edge cases

| Case | Behaviour |
|------|-----------|
| User cancels then re-connects (server has Cancelled state in AgencyState) | `LocallyActedOnContractGuids` cleared on disable + enable. Server's catch-up `AgencyContractMsgData` carries the Cancelled entry; `ApplyContractBatch` puts it in `ContractsFinished`. CC re-Offer on a future tick: GUID NOT in the (now-empty) `LocallyActedOnContractGuids`, GUID NOT in `ServerOfferedContractGuids` (verified via [Server/System/Agency/AgencyScenarioProjector.cs:146-150](../../Server/System/Agency/AgencyScenarioProjector.cs#L146-L150) `SharedContractStates = {Offered, Generated}` only — Cancelled goes to `CONTRACTS_FINISHED`; [LmpClient/Systems/Scenario/ScenarioMessageHandler.cs:77](../../LmpClient/Systems/Scenario/ScenarioMessageHandler.cs#L77) populates the snapshot only from `state == "Offered"`). Non-lock-holder branch → `WithdrawAndRemoveContract` ✓. **Lock-holder branch → silently re-broadcasts as fresh Offered** ([ShareContractsEvents.cs:593](../../LmpClient/Systems/ShareContracts/ShareContractsEvents.cs#L593)) — pop-back on reconnect for the LOCK HOLDER specifically. This is a known residual gap acceptable for v8.2 (narrow trigger: lock-holder + reconnect + KSPCF preloader cache retained X across `OnDisable`; same failure mode as pre-fix v8.1 so no regression). Real fix needs a server-side `AgencyContractWithdrawn`-style wire OR a per-agency `ContractPreLoader` cache eviction on terminal transitions — deferred. |
| `RestoreMissingServerOfferedContracts` re-adds a locally-acted-on contract | Low-probability corner case: the restoration path at [ShareContractsEvents.cs:203-261](../../LmpClient/Systems/ShareContracts/ShareContractsEvents.cs#L203-L261) uses `StartIgnoringEvents` (line 244), so `ContractOffered`'s `IgnoreEvents` early-return ([line 521](../../LmpClient/Systems/ShareContracts/ShareContractsEvents.cs#L521)) runs BEFORE the new guard — restoration wins for an unacted contract (intended). If a locally-acted GUID somehow ends up in `serverOfferedSnapshot`, restoration would silently re-add it. Verified that `SnapshotServerOfferedContracts` ([line 131](../../LmpClient/Systems/ShareContracts/ShareContractsEvents.cs#L131)) only snapshots contracts with `serverGuids.Contains(...)` (i.e., still in `ServerOfferedContractGuids`) AND present in `cs.Contracts` — server-side projection strips Cancelled from that set, so a Cancelled contract can't reach the snapshot under per-agency. Documented as un-pinned; no fix needed. |
| Concurrent Cancel of multiple contracts | Each handler adds to the set independently. HashSet.Add is thread-safe for single-writer-per-key insertion patterns; the events fire on the Unity main thread serially. Multiple cancellations resolve correctly. |
| User Accepts a contract, then CC re-Offers a duplicate | Accept handler adds the GUID to the set. The duplicate (new `Contract` instance, same GUID) hits the guard → withdrawn. Local Active contract (the original instance) is unaffected because `WithdrawAndRemoveContract` operates by reference and the original is a different object. |
| User Declines an Offered contract | Same as Cancel path. Set populated; CC re-Offer withdrawn. |
| User Completes a contract (legitimately) | Set populated. CC re-Offer (if any) withdrawn. Local Completed contract intact in ContractsFinished. |
| Lock-holder cancels a contract they generated mid-session | The new guard fires first (GUID in `LocallyActedOnContractGuids`) → withdraw. The lock-holder re-broadcast branch is never reached. Good — the cancel sticks. |
| Multi-player-per-agency: Player A cancels, Player B sees re-Offer | Player B's local KSP has no `LocallyActedOnContractGuids` entry for this GUID (B didn't cancel it). For B: server's per-agency state has the contract Cancelled (echo went owner-only to A only under current 1:1 design); B has no echo. If CC re-Offers for B, the guard misses and the existing `ServerOfferedContractGuids` protection applies (if originally in snapshot). This is pre-existing behaviour. Multi-player-per-agency cohort doesn't ship in v8.1. |
| CC re-Offers a `LmpUnavailableContract` stub | The existing `if (contract is LmpUnavailableContract) return;` early-return runs FIRST; the new guard never sees it. ✓ |
| Inside `ApplyContractBatch` while `IgnoreEvents` is set | The existing `if (System.IgnoreEvents) return;` check runs SECOND, before the new guard. Apply-time CC re-fires are suppressed at the LMP layer. ✓ |

---

## Failure modes considered

| Mode | Mitigation |
|------|------------|
| `LocallyActedOnContractGuids` grows unbounded over a long session | Each entry is one short GUID string (~38 chars + HashSet overhead, ~80 bytes). A player cancelling 1000 contracts in a session adds ~80 KB. Negligible. Cleared on `OnDisabled` (reconnect / quit). |
| Lock-holder cancel + reconnect → pop-back on reconnect | Documented above as a known residual gap. The reconnect path is: set cleared → server catches up the Cancelled state via `AgencyContractMsgData` → CC re-Offers from preloader (still cached) → lock-holder branch re-broadcasts. The cleanest server-side closer would be: on `AgencyContractMsgData` apply (terminal state), re-add the GUID to `LocallyActedOnContractGuids`. Deferred to a follow-up — out of scope here. |
| Race between `ContractCancelled` handler and `ContractOffered` for the same GUID | Both run on the Unity main thread serially. KSP fires events in a deterministic order: onCancelled completes before onOffered for the regen. Set is populated before the next tick's re-Offer arrives. ✓ |
| `WithdrawAndRemoveContract` throws on the new instance | The existing helper handles its own state internally. If it throws, the exception unwinds through the `ContractOffered` callback — same as pre-fix behaviour for any other Withdraw call site (e.g. the existing `RecoverAsset` / `TourismContract` withdrawals). No new failure surface introduced. |
| User Accepts then immediately Cancels (faster than the regen tick) | Each handler adds to the set. Order doesn't matter — both terminal-action lookups resolve to "withdraw on re-Offer". |
| Contract GUID uppercase vs lowercase mismatch (CC restoration round-trip) | Set uses `StringComparer.OrdinalIgnoreCase`. Helper just delegates to `Contains`. Pinned by the case-insensitive test case. |
| `LocallyActedOnContractGuids` accessed during `OnDisabled.Clear()` from another thread | KSP `OnDisabled` runs on the Unity main thread; handlers also on the main thread. No cross-thread access surface. |

---

## Multi-lens review plan

After implementation + tests pass, run two parallel `general-purpose` agents per [[feedback-review-lens-framing]]:

1. **Consumer-lens** — does the new guard break legitimate CC re-fires of UNACTED contracts on subsequent scenario loads? Does it interact correctly with `LmpUnavailableContract` stubs, `ApplyContractBatch`'s `IgnoreEvents` window, and the `RestoreMissingServerOfferedContracts` post-load restoration?
2. **Upgrade / lifecycle** — does the set's lifetime contract hold across reconnect / scene change / Sandbox vs Career? Does CC behaviour vary between KSPCF versions in a way that would shift the bug?

Expect 0 MUST-FIX (small surface, narrow change). Any SHOULD-FIX from either lens gets folded in before the joint commit.

---

## Test surface delta

| Suite | Pre | Post | Delta |
|-------|-----|------|-------|
| `LmpClientTest` | 180 | 187 | +7 (new `ShareContractsReOfferDecisionTest.cs`) |
| `ServerTest` | unchanged | unchanged | 0 (server-side untouched) |
| `MockClientTest` | unchanged | unchanged | 0 (the wire surface is the same; the bug + fix are client-internal) |
| `LmpCommonTest` | unchanged | unchanged | 0 |

Full LmpClientTest suite green: 187/187 pass.

---

## Wire / protocol impact

None. Wire shape unchanged. Protocol stays at 0.31.0. v8.1 server + v8.2 client + v8.1 client mixed cohort all interoperate.

---

## Distribution

`LmpClient.dll` only — single file, ~3 MB. Players replace `GameData/LunaMultiPlayer/Plugins/LmpClient.dll`, restart KSP, reconnect. Server operators do nothing. Per [[feedback-release-upload-autonomous]] — bundle as a v0.31.0-per-agency-private-8.2 release once the parallel kerbal-roster fix lands too.
