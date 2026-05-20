# Fix external-seat boarding sync — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `f1cfa419` (fix(client,sharecontracts): scrub CC body-index in wire-batch contract path)
**Discipline:** Per `[[feedback-breakage-analysis]]` — mandatory before non-trivial changes.
**Motivation:** Pre-existing bug uncovered during Stage 6 per-agency kerbal-roster audit (2026-05-20). The Harmony patches at [LmpClient/Harmony/KerbalEVA_BoardSeat.cs:33](../../LmpClient/Harmony/KerbalEVA_BoardSeat.cs#L33) and [LmpClient/Harmony/KerbalEVA_OnDeboardSeat.cs:25](../../LmpClient/Harmony/KerbalEVA_OnDeboardSeat.cs#L25) raise `ExternalSeatEvent.onExternalSeatBoard` and `onExternalSeatUnboard`, but a `grep ExternalSeatEvent` across `LmpClient/` confirms **nothing subscribes**. Result: when a kerbal sits down in an external command seat (lawn-chair on a rover), the network never learns. Peer clients see a still-empty seat. The bug has been present since commit `b7306514` (2018-11-30), when Dagger's `LmpClient/Systems/ExternalSeat/{ExternalSeatSystem.cs,ExternalSeatEvents.cs}` were deleted during a docking/decoupling refactor — collateral damage; the refactor's new `VesselCoupleSys` does NOT cover seat-boarding (it handles vessel-vessel docking, not kerbal-into-seat). No subsequent commit reinstated the surface.

---

## Scope lock — IS

### 1. Two new handler methods in `LmpClient/Systems/VesselCrewSys/VesselCrewEvents.cs`

Mirrors the modernized shape of the existing internal-boarding handler at [VesselCrewEvents.cs:16-25](../../LmpClient/Systems/VesselCrewSys/VesselCrewEvents.cs#L16-L25):

```csharp
/// <summary>
/// Event triggered when a kerbal boards an external command seat (lawn chair).
/// </summary>
public void OnExternalSeatBoard(Vessel vessel, Guid kerbalVesselId, string kerbalName)
{
    if (vessel == null) return;
    LunaLog.Log("Crew-board to an external seat detected!");

    VesselRemoveSystem.Singleton.MessageSender.SendVesselRemove(kerbalVesselId, false);
    LockSystem.Singleton.ReleaseAllVesselLocks(new[] { kerbalName }, kerbalVesselId);
    VesselRemoveSystem.Singleton.KillVessel(kerbalVesselId, true, "Killing kerbal-vessel as it boarded an external seat");

    VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, true, reason: "crew boarded external seat");
}

/// <summary>
/// Event triggered when a kerbal unboards an external command seat.
/// </summary>
public void OnExternalSeatUnboard(Vessel unboardedVessel, KerbalEVA kerbal)
{
    if (unboardedVessel == null || kerbal == null || kerbal.vessel == null) return;
    LunaLog.Log("Crew-unboard from an external seat detected!");

    VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(unboardedVessel, true, reason: "crew unboarded external seat");
    EvaReady.FireOnCrewEvaReady(kerbal);
}
```

**Why no spectating check on board.** The original Dagger handler at commit `efb0dc74` carried the comment *"Do not check if we are spectating as we are perhaps boarding a seat of a vessel controlled by another player!"* That rationale survives — if Bob's kerbal boards Alice's rover's lawn-chair while Bob is spectating, Bob still needs to push the proto. The modern internal-board `OnCrewBoard` follows the same no-spectating-check convention.

**Why the unboard handler calls `EvaReady.FireOnCrewEvaReady`.** KSP's `KerbalEVA.OnDeboardSeat()` re-points `__instance.vessel` to a fresh EVA vessel for the kerbal but does NOT reliably fire `GameEvents.onCrewOnEva` (that event is for IVA→EVA transitions out of internal seats). Without the explicit `FireOnCrewEvaReady`, the new EVA-kerbal vessel goes unreported until the next periodic resync.

### 2. Two new subscriptions in `LmpClient/Systems/VesselCrewSys/VesselCrewSystem.cs`

Append to `OnEnabled` (after [line 27](../../LmpClient/Systems/VesselCrewSys/VesselCrewSystem.cs#L27)):
```csharp
ExternalSeatEvent.onExternalSeatBoard.Add(VesselCrewEvents.OnExternalSeatBoard);
ExternalSeatEvent.onExternalSeatUnboard.Add(VesselCrewEvents.OnExternalSeatUnboard);
```

Append matching `.Remove` calls to `OnDisabled` (after [line 36](../../LmpClient/Systems/VesselCrewSys/VesselCrewSystem.cs#L36)).

Reusing `VesselCrewSystem` rather than reviving Dagger's separate `ExternalSeatSystem` keeps the surface unified — kerbal/crew lifecycle handlers belong together. The old separation was a 2018 design artefact.

### 3. ForkBuildInfo entry

Append `external-seat-sync` to [Server/ForkBuildInfo.cs:ActiveFixes](../../Server/ForkBuildInfo.cs) — operators grepping `[fix:` get a marker (though this fix doesn't add a per-event log tag; the existing `LunaLog.Log` is enough).

### 4. No test surface change

- The handlers dispatch to static singletons (`LockSystem.Singleton`, `VesselRemoveSystem.Singleton`, `VesselProtoSystem.Singleton`) that depend on the Unity/KSP runtime. There is no clean unit-isolation path.
- Precedent: the existing `OnCrewBoard` (internal-board, the template I'm mirroring) has no `LmpClientTest` case. Neither do any of the 11 Harmony patches under `LmpClient/Harmony/`. Soak testing is the validation path for this class of code.
- LmpClientTest stays at 91 tests.

### 5. CLAUDE.md update

- **Stack Notes & Patterns Learned**: new entry dated 2026-05-20 documenting that external-seat events were silently un-subscribed since 2018 and the modern fix lives in `VesselCrewSystem` rather than a separate system.
- **Known Limitations & Future Work**: remove the (implicit) external-seat gap mention if it was anywhere. It isn't currently listed — this is a pre-existing latent gap, not a documented limitation.

---

## Scope lock — IS NOT

- **No new Harmony patches** — the two existing patches at `KerbalEVA_BoardSeat.cs` and `KerbalEVA_OnDeboardSeat.cs` already fire the events; the gap is purely on the subscriber side.
- **No revival of `LmpClient/Systems/ExternalSeat/`** — adding handler methods to the existing `VesselCrewEvents` is simpler than re-creating a separate system + csproj entries + base-class plumbing.
- **No protocol bump** — wire shape is unchanged. `VesselProtoMsgData` + `VesselRemoveMsgData` already exist with the right semantics.
- **No server-side change** — the server already handles `VesselProtoMsgData` for the seat-owner vessel and `VesselRemoveMsgData` for the EVA kerbal-vessel correctly. The pre-existing v4 cross-agency write guard at `Server/Message/VesselMsgReader.cs:HandleVesselProto` will gate the proto write under PerAgencyCareer=true — Bob can't board Alice's lawn-chair via cross-agency-write because his proto for HER vessel would be rejected. (Under shared-roster mode the proto sails through as today.)
- **No per-agency-aware logic** — the fix predates Stage 6 in spirit; it's purely a stability/regression fix for the seat-boarding flow. Gate=on and gate=off both benefit.
- **No spectator gate** — by design, mirroring the existing `OnCrewBoard` precedent.

---

## Edge cases

| Case | Behaviour |
|------|-----------|
| Kerbal boards rover's lawn-chair on a vessel they own (shared-roster) | Old: silent; remote players see empty seat. New: `OnExternalSeatBoard` fires → EVA-kerbal vessel removed + seat-owner vessel proto rebroadcast → all peers see the kerbal in the seat. |
| Kerbal boards lawn-chair on a peer's vessel (shared-roster) | Old: silent. New: same as above; no spectating-check gate. Server forwards to peers per normal vessel-proto relay. |
| Kerbal boards lawn-chair on Alice's rover, gate=on, Bob's kerbal (cross-agency) | New handler fires unconditionally on Bob's client; server-side `HandleVesselProto` cross-agency-write guard at [Server/Message/VesselMsgReader.cs:318](../../Server/Message/VesselMsgReader.cs#L318) (session 39 v4 fix) rejects Bob's proto because Alice owns the rover. Bob's local change is cosmetic-only on his client; the seat-empty state persists on Alice's authoritative copy + her client + other peers. Same anti-grief outcome as the lock guards. |
| Kerbal unboards from lawn-chair, gate=off | `OnExternalSeatUnboard` fires → seat-owner vessel proto rebroadcast (kerbal removed from `protoCrew`) + `FireOnCrewEvaReady` for the freshly-spawned EVA kerbal vessel → new EVA vessel is broadcast via the existing `CrewEvaReady` chain. |
| Kerbal unboards from lawn-chair, gate=on, cross-agency | Bob's unboard from Alice's lawn-chair: same cross-agency rejection on the proto write. Stage 6 spec §Q-CrossAgency already requires kerbals can only interact with their own agency's vessels in normal play — this matches. |
| Lawn-chair sits on a vessel that no longer exists (race) | `vessel == null` short-circuits the board handler at line 1 of the new method. Defensive. |
| Multiple kerbals board different seats on the same rover in quick succession | Each Harmony postfix fires independently → each invokes the new handler → each sends an EVA-vessel remove + a fresh seat-owner vessel proto. The seat-owner vessel proto carries the cumulative crew list (KSP's `ProtoVessel.Save` snapshots current state), so the final state on the server is consistent. Intermediate states are eventually-consistent which matches the existing internal-board behavior. |
| Kerbal in lawn-chair when the rover vessel switches off / unloads | Existing physics-range unload logic handles this — the seat-state is preserved in the rover's proto on disk. The new handlers don't change this path. |
| Lock contention — kerbal had a kerbal-lock that's still held | `LockSystem.Singleton.ReleaseAllVesselLocks(new[] { kerbalName }, kerbalVesselId)` releases both the kerbal-lock and any vessel-lock for the EVA vessel. Same call as `OnCrewBoard`. |
| Harmony patch order vs subscriber registration | `VesselCrewSystem.OnEnabled` runs during `MainSystem.NetworkState = Running`; the Harmony patches are static class members loaded at assembly-load time and fire whenever the patched method is invoked. Pre-`Running` boarding events would have no subscriber. Same constraint as every other VesselCrewSystem subscription — no regression. |

---

## Failure modes considered

| Mode | Mitigation |
|------|------------|
| `EvaReady.FireOnCrewEvaReady` is the wrong entry point post-2018 | Verified by grep: still in use by [VesselCrewEvents.cs:32](../../LmpClient/Systems/VesselCrewSys/VesselCrewEvents.cs#L32). Same signature, same semantics. |
| `SendVesselMessage` no longer accepts `(vessel, true, reason: "...")` overload | Verified by the matching call on line 24 of the same file. Same signature. |
| Subscribing twice (board fires both my new handler AND something else nobody noticed) | Confirmed by grep across `LmpClient/` — `onExternalSeatBoard.Add` and `onExternalSeatUnboard.Add` appear in zero locations today. Adding them in `VesselCrewSystem.OnEnabled` creates a single subscription. |
| Subscriber doesn't unsubscribe on disconnect | `OnDisabled` removes the subscriptions. `VesselCrewSystem` is per-`MainSystem` lifecycle — on disconnect the system disables and unsubscribes. Confirmed by precedent at `VesselCrewSystem.OnDisabled` lines 33-36. |
| Kerbal-vessel race with `KillVessel(addToKilledList: true)` | The kill-list prevents the vessel from re-spawning during a relay window. Same shape as `OnCrewBoard` — proven by 5+ years of internal-board operation. |
| Reverting kerbal back to EVA after a partial-boarding failure | KSP's `KerbalEVA.BoardSeat` postfix only fires if `__result == true` (board succeeded). Failed-board attempts don't trigger the event; no cleanup needed. |
| Future Harmony breakage if KSP changes `KerbalEVA.BoardSeat` signature | Not in scope for this fix. If KSP renames the method, the Harmony patch itself would stop matching — same fragility as the other 30+ Harmony patches in the project. |

---

## Multi-lens review plan

After implementation, run **two parallel lenses** per `[[feedback-review-lens-framing]]`:

1. **client-harmony-review** — the natural domain agent. Confirm: handler shapes match internal-board precedent; no Unity main-thread violations; `SendVesselMessage` reason-strings are reasonable; subscriptions cleanly remove on disable; no race vs `OnCrewBoard` if both fire on a single vessel.
2. **server-systems-review** — confirm the cross-agency interaction story: server-side `HandleVesselProto` (`VesselMsgReader.cs`) already rejects cross-agency writes per session-39 v4 fix; the new client-side broadcast is harmless under gate=on because the server gates the write. Verify no Server change needed.

Expect 0 MUST-FIX (small surface, established precedent). Any [SHOULD CONSIDER] gets folded into the same commit before review-receipt.

---

## Test surface delta

| Suite | Pre | Post | Delta |
|-------|-----|------|-------|
| LmpClientTest | 91 | 91 | 0 (no pure-helper extraction; handlers depend on static singletons) |
| ServerTest | 670 | 670 | 0 (no server change) |
| MockClientTest | ~100 | ~100 | 0 (no wire change; existing tests cover the underlying VesselProto + VesselRemove paths) |
| LmpCommonTest | 14 | 14 | 0 |

Soak validation: operator hops into a rover, sends a kerbal to sit in an external command seat, confirms peer sees the kerbal in the seat. Same for unboard.

---

## Commit metadata

- **Branch**: `feature/per-agency`
- **Commit subject**: `fix(client,vessel): resubscribe external-seat board/unboard handlers`
- **Scope token**: `client,vessel` (per CLAUDE.md allowed scopes)
- **No AI attribution** (silent partner rule)
- **Review receipt**: `.claude/review-receipts/{sha1}.txt` required by `require-bug-review.sh` PreToolUse hook
