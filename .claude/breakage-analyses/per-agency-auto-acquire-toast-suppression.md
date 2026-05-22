# Per-agency auto-acquire toast suppression — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `56e53cf3` (contract pop-back hotfix, v0.31.0-per-agency-private-9 tip area)
**Discipline:** Per `[[feedback-breakage-analysis]]`.

**Motivation:** Cohort soak reported a green "Cannot interact with this vessel: It belongs to XYZ Agency" toast firing intermittently with no user action — "randomly but very dependably." Operator hypothesis: fires when another player switches craft. Hypothesis verified — `VesselLockEvents.LockReleased` (and three sibling passive paths) fire blanket `AcquireUpdateLock` / `AcquireUnloadedUpdateLock` for foreign-agency vessels, which the server's 5.17a guard rejects with `LockRejectReason.CrossAgency`, triggering the toast at [LmpClient/Systems/Lock/LockMessageHandler.cs:115](LmpClient/Systems/Lock/LockMessageHandler.cs#L115).

---

## Scope lock — IS

### 1. `LmpClient/Systems/Agency/AgencyMembership.cs` — new pure helper `IsAutoAcquireBlockedByAgency`

Six-branch bypass matrix mirroring `IsRecoveryBlockedByAgency` exactly (gate off / local agency-less / vessel unknown / Unassigned-sentinel / same-agency / different → BLOCK). XML documents the four KSP-driven passive paths the helper exists to gate + the deliberate scope limitation (Update/UnloadedUpdate only, not Control).

### 2. `LmpClient/Systems/Lock/LockSystem.cs` — gate `AcquireUpdateLock` + `AcquireUnloadedUpdateLock`

Inserted between existing already-owned early-out and inner `AcquireLock`. Pattern:

```csharp
if (LockQuery.UpdateLockBelongsToPlayer(vesselId, ...)) return;          // existing
if (!force && IsAutoAcquireBlockedByAgency(vesselId)) return;            // new
AcquireLock(new LockDefinition(LockType.Update, ...), force, immediate); // existing
```

Plus a new private static wrapper `IsAutoAcquireBlockedByAgency(Guid vesselId)` that resolves `AgencySystem.Singleton` + `SettingsSystem.ServerSettings.PerAgencyCareerEnabled` + `TryGetOwningAgency` and delegates to the pure helper — same resolution pattern as `VesselRemoveEvents.TryBlockCrossAgencyAction`. New `using LmpClient.Systems.Agency;`.

### 3. `LmpClientTest/AgencyMembershipDecisionTest.cs` — +6 cases pinning every branch

`IsAutoAcquireBlockedByAgency_DifferentAgency_Blocks` + `_SameAgency_Permits` + `_GateOff_PermitsEvenWhenDifferentAgency` + `_LocalEmpty_Permits` + `_VesselUnknown_Permits` + `_UnassignedSentinel_Permits`. Test rationale prose documents the motivating cross-agency-vessel-switch-fanout case + the forward-compat multi-player-per-agency contract.

### 4. `CLAUDE.md` — Stack Notes entry + LmpClientTest line update

One Stack Notes entry under the 2026-05-22 date documenting the four passive paths + the deliberate `force=true` bypass + the deliberate `AcquireControlLock` non-gating rationale. LmpClientTest line "+6 from `IsAutoAcquireBlockedByAgency`" appended to the existing `AgencyMembershipDecisionTest` entry.

---

## Scope lock — IS NOT

- `AcquireControlLock` is intentionally NOT gated. User-driven path (`OnVesselChange`, `FlightStarted`); toast on user-initiated cross-agency control attempt is meaningful UX and feeds into the spectator-switch path via `OnVesselChange`'s pre-acquire branch.
- `AcquireKerbalLock` not gated. Kerbal locks aren't vessel-scoped and the server's 5.17a guard at [Server/System/LockSystem.cs:84+](Server/System/LockSystem.cs#L84) only fires on vessel-scoped lock types.
- Server-side `LockSystem.AcquireLock` cross-agency classifier untouched — server still rejects cross-agency acquires correctly; we're just reducing the volume of useless wire chatter that would otherwise reach it.
- No wire protocol change. No `Agency*MsgData` additions. Protocol stays 0.31.0.
- No mod-compat router edits. The four passive paths and the cross-agency reject path are stock-LMP surface, no MKS/WOLF/etc. interaction.

---

## Edge cases considered

1. **`force=true` chain-fired acquires.** `VesselLockEvents.LockAcquire` Control branch fires `AcquireUpdateLock(..., force: true)` after local got Control; `VesselDecoupleEvents.DecoupleComplete` + `VesselUndockEvents.UndockComplete` fire `AcquireUnloadedUpdateLock(..., force: true)` after pre-gating on local owning parent's Update; `VesselEvaEditorEvents.VesselCreated` fires `AcquireUpdateLock(..., force: true)` on locally-created vessels (EVA construction drops + deployable science). All `force=true` callers have established local authority by an upstream check; gate would be incorrect → `!force` guard preserves the chain.

2. **Pre-handshake state.** `AgencySystem.LocalAgencyId` starts at `Guid.Empty`; `PerAgencyCareerEnabled` starts at `false` until `SettingsReplyMsgData` arrives. Both early-return-permit branches in the helper match `IsRecoveryBlockedByAgency`'s established contract.

3. **Vessel registry MISS (relay-stripped `lmpOwningAgency`).** Per `[[5.18b relay-vs-store note]]` the relay path can deliver protos that parse to `Guid.Empty` because KSP's `BackupVessel` strips the unknown top-level field. We treat MISS as "permit and let server decide" — server-side 5s debounce catches the brief race window, registry catches up via VesselSync. Same posture as the recovery guard.

4. **Unassigned-sentinel vessel.** Spec §10 Q3: pre-0.31 vessels + `/deleteagency` cascade targets carry `OwningAgencyId = Empty` and are interactable by any agency. Helper permits, matching server-side classifier at `LockSystem.cs:170+`.

5. **Gate=off cohort.** Returns `false` immediately, bit-identical to legacy LMP. Required so the entire passive lock-acquire chain still works on shared-agency servers.

6. **Multi-player-per-agency forward-compat.** The same-agency branch permits acquire — today no-op (1:1 ownership) but contract is set for any future relaxation. 5.17a server-side guard already allows same-agency acquires.

7. **Pinned-but-Unassigned vessel after `/deleteagency`.** Demoted vessels become Unassigned (Empty owner). Passive paths from any client succeed in acquiring the lock — important so the vessel doesn't become a physics-frozen orphan. Helper permits via Unassigned-sentinel branch.

8. **Pinned cross-agency vessel with no online same-agency peer is stranded immortal until the owning-agency reconnects** (integration-lens review [SHOULD FIX]). When agency X player A disconnects holding Update on V_A, BUG-010 broadcasts `VesselPinnedMsgData` and the only thing that unpins V_A is a Control / Update `LockAcquire` reaching peers via `VesselPinnedEvents.OnLockAcquire`. With the new helper, peer B (agency Y) no longer auto-acquires V_A's released Update lock; peer C (agency X) does. In a 1:1-player-per-agency cohort, or any time X has no other online member, V_A stays immortal-pinned indefinitely on every other client until an X member reconnects (or the operator runs `/deleteagency X`, which demotes V_A to Unassigned and frees the next passive event to permit the acquire). **This is a sharpening of a pre-existing bug, not a new one** — pre-fix, B's auto-acquire was *rejected* by the server's 5.17a guard, so peer B never unpinned V_A pre-fix either; the fix simply suppresses the rejection traffic + toast. Operator-mitigated by either same-agency reconnect or admin-driven `/deleteagency`. Documented here so an operator soaking after a long absence understands the "pinned forever" symptom is expected. Future server-side mitigation (e.g., a fallback unpin after N seconds of unanswered same-agency takeover) is out of scope for this commit.

---

## Test plan

### Automated

- ✅ `LmpClientTest/AgencyMembershipDecisionTest.IsAutoAcquireBlockedByAgency_*` — 6/6 pass (full bypass matrix pinned).
- ✅ `LmpClientTest` full suite — 201/201 pass (was 195; +6 new).
- ✅ `ServerTest/LockSystemAgency*` — 18/18 pass (cross-agency server-side contract unaffected).
- ✅ `LmpClient.csproj` build clean — only the 7 pre-existing noise warnings documented in CLAUDE.md.

### Manual / soak

- **Verify suppression:** Two-cohort with `PerAgencyCareer=on`. Player A (agency X) on vessel V_A. Player B (agency Y) on vessel V_B. Player B switches craft — Player A's screen should NOT see the toast for V_B.
- **Verify positive path:** Player A switches craft within agency X — same-agency peer (if multi-player-per-agency) still auto-acquires their fellow agency-mate's released Update lock.
- **Verify Unassigned-sentinel acquire works:** `/deleteagency` on agency X. Player B (agency Y) physics-loads a former-X vessel. Auto-acquire should land on the server (vessel becomes Y-simulated). No toast.
- **Verify dual-mode silence:** `PerAgencyCareer=off` cohort, all passive paths still acquire normally (no regression).

---

## Rollback / blast radius

- LmpClient-only change. No server-side edit, no wire schema, no protocol bump.
- Cherry-pick-revertible by a single `git revert`.
- Worst case if the helper somehow returns `true` incorrectly: a vessel's Update lock fails to transfer when it SHOULD have; the vessel stays at the previous holder's last-broadcast pose until either (a) the previous holder reconnects, (b) some other client tries Control, or (c) the vessel exits + re-enters physics range. No data loss, no state corruption. Worst case is recoverable by reconnect.
