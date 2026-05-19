# v4 — VesselProto cross-agency write guard — scoping note

**Status:** PRE-IMPLEMENTATION. Drafted 2026-05-19 (session 39) after the Phase 4 pre-spec authoring caught the underlying proto-write hole that was wrongly assumed closed by 5.17a-write-counterpart.

**Branch:** `feature/per-agency` (the unified per-agency branch), tip `0d99a81a` (5.18g shipped). The fix lands as a single small commit BEFORE v4 release cut. WOLF Phase 4 ([mks-lmp-compatibility-phase-4-prespec.md](mks-lmp-compatibility-phase-4-prespec.md)) is deferred to v5.

**Scope:** ONE file edit + ~50 lines of new test code + breakage analysis. Closes the broad cross-agency vessel-state-write exploit that affects ALL VesselProto traffic (not just WOLF). The fix is structurally equivalent to the 5.17a write-path counterpart pattern but applied to the previously-uncovered `HandleVesselProto` dispatch case.

**Source pins:**

- LMP fork at `f:\luna-multiplayer\`, `feature/per-agency` tip `0d99a81a`.
- `Server/Message/VesselMsgReader.cs` (the file the fix edits).
- `Server/System/Vessel/VesselDataUpdater.cs` (the downstream consumer; unchanged but referenced for trace context).

---

## 1. The bug — verified end-to-end trace

### 1.a The hole

`Server/Message/VesselMsgReader.HandleMessage` at [VesselMsgReader.cs:23-108](../../Server/Message/VesselMsgReader.cs#L23-L108) dispatches `VesselMessageType.Proto` to `HandleVesselProto` at line 33. `HandleVesselProto` at [VesselMsgReader.cs:262-318](../../Server/Message/VesselMsgReader.cs#L262-L318) performs:

1. RemovedVessels check (line 266) — drops if vessel is in the kill list.
2. Zero-bytes check (line 268-272) — drops if payload is empty.
3. **Past-subspace check** (line 277-283) — drops if sender is strictly past the vessel's `AuthoritativeSubspaceId`.
4. Sender agency resolution (line 297-302).
5. **`VesselDataUpdater.RawConfigNodeInsertOrUpdate`** (line 304) — writes the proto bytes to the server's vessel store.
6. **Relay to all clients** (line 317).

**`RejectIfCrossAgencyWrite` is NOT called.** The Stage 5.17a write-path counterpart at [VesselMsgReader.cs:208-228](../../Server/Message/VesselMsgReader.cs#L208-L228) gates 11 relayed message types (Position / Flightstate / Update / Resource / 3x PartSync / ActionGroup / Fairing / Decouple / Undock) AND 2 destructive handlers (Remove at line 244 + Couple at line 410) — but NOT Proto. The omission was intentional per the [[5.18b relay-vs-store note]] in CLAUDE.md: "Proto is intentionally excluded — it has its own ownership-stamping logic and the established contract that relayed bytes are advisory."

**The intent of that exclusion was about the RELAY surface.** The relayed proto bytes carry the original sender's wire payload, which clients should treat as advisory (re-deriving ownership from `VesselSync` replies or `AgencyVisibilityMsgData`). But the SERVER's stored copy IS authoritative for the vessel's state — and that store gets overwritten by whatever bytes the sender supplied.

### 1.b The attack

Verified trace under per-agency mode (`PerAgencyCareer=true`):

1. Alice (Agency A) owns vessel `V_A` with `OwningAgencyId == AgencyA`. The vessel hosts kerbals Jeb + Bill via its ConfigNode `crew = Jeb Kerman` / `crew = Bill Kerman` entries.
2. Bob (Agency B) crafts a `VesselProtoMsgData` with `VesselId = V_A` and a payload that removes Jeb from the crew list (or modifies any other field — resources, parts, position).
3. Bob sends the crafted proto to the server.
4. Server-side `HandleVesselProto`:
   - RemovedVessels check passes.
   - Zero-bytes check passes.
   - Past-subspace check passes (Bob is current-subspace).
   - Sender agency resolved as Bob's AgencyB.
   - `RawConfigNodeInsertOrUpdate(V_A, Bob's bytes, Bob's subspace, AgencyB)` runs.
5. Inside `RawConfigNodeInsertOrUpdate` ([VesselDataUpdater.cs:73-169](../../Server/System/Vessel/VesselDataUpdater.cs#L73-L169)):
   - `vessel = new Classes.Vessel(Bob's bytes)` — parses Bob's payload.
   - 5.16b stamp logic ([VesselDataUpdater.cs:146-160](../../Server/System/Vessel/VesselDataUpdater.cs#L146-L160)) preserves `existing.OwningAgencyId` (Alice's AgencyA) on the new vessel object.
   - `VesselStoreSystem.CurrentVessels.AddOrUpdate(V_A, vessel, ...)` — **replaces** Alice's vessel object with Bob's parsed payload (carrying Alice's preserved agency stamp).
6. Disk store: `BackupSystem` flushes Bob's bytes to `Universe/Vessels/V_A.txt` on next backup cycle.
7. Relay (line 317): Bob's original bytes are broadcast to all clients including Alice.
8. Alice's KSP applies the relayed proto. Alice's local vessel state now reflects Bob's mutations.

**Net effect:** Bob arbitrarily mutated Alice's vessel state on the server's authoritative store. The `OwningAgencyId` is the ONLY field preserved across the write. Crew list, parts, resources, position — all overwritten.

### 1.c The exploit surface

Any client that can craft a proto and knows a target vessel's Guid can mutate that vessel's state. The vessel-id discovery surface is broad: every connected client receives `VesselSync` replies + relayed protos for every vessel in the universe, so Bob already knows Alice's vessel-ids by construction. The only "modification" required is a custom LMP client that sends `VesselProtoMsgData` with a target vessel-id not in the modifier's own roster.

**Severity:** high. The `OwningAgencyId` preservation gives a false sense of safety — under per-agency mode operators assume "Alice's vessel is safe from Bob" because the ownership stamp doesn't change. But every other field of the vessel is unprotected.

### 1.d Specific exploit paths this closes

Without claiming exhaustive coverage:

- **Kerbal seizure via vessel-write** — Bob removes Jeb from Alice's crew list; Jeb becomes Missing/unassigned on Alice's vessel; Jeb can then be hired/used by Bob's agency. (This is the WOLF Phase 4 §1.b path via a non-WOLF mechanism.)
- **Vessel state vandalism** — Bob moves Alice's vessel into deep space / underground / through Kerbin / sets resources to zero / fills tanks with infinite resources.
- **Part removal** — Bob crafts a proto with Alice's parts list shortened; KSP applies on Alice's next sync; physical parts vanish from Alice's vessel.
- **Resource theft via state mutation** — Bob writes Alice's vessel resources to zero; Bob has effectively drained Alice's tank without doing any actual work. (Bob doesn't GET the resources — they just vanish; but this is a griefing vector.)
- **Cross-agency lock circumvention** — Bob can't acquire a lock on Alice's vessel (5.17a closes that), but Bob can craft a proto that sets the vessel into a state where Alice's own locks become useless (e.g. set the vessel to dead / removed-from-physics).

The 5.18g hardening (s38, commit `0d99a81a`) closed two BLOCKING grief vectors (NaN/Infinity currency + per-Contract claim race). This proto-write hole is BROADER than either of those — it affects all vessel-state surfaces.

---

## 2. The fix

### 2.a The change

One line in `Server/Message/VesselMsgReader.cs` — add `RejectIfCrossAgencyWrite` to `HandleVesselProto` between the past-subspace check and the sender-agency resolution:

```csharp
private static void HandleVesselProto(ClientStructure client, VesselBaseMsgData message)
{
    var msgData = (VesselProtoMsgData)message;

    if (VesselContext.RemovedVessels.ContainsKey(msgData.VesselId)) return;

    if (msgData.NumBytes == 0)
    {
        LunaLog.Warning($"Received a vessel with 0 bytes ({msgData.VesselId}) from {client.PlayerName}.");
        return;
    }

    // BUG-005/006 past-subspace check (existing — unchanged)
    if (VesselStoreSystem.CurrentVessels.TryGetValue(msgData.VesselId, out var existing)
        && WarpSystem.IsStrictlyPast(client.Subspace, existing.AuthoritativeSubspaceId))
    {
        LunaLog.Debug($"[fix:BUG-005/006] rejecting proto-update for {msgData.VesselId} from {client.PlayerName} ...");
        return;
    }

    // **NEW: v4 cross-agency proto-write guard.** The 5.17a write-path counterpart
    // covered 11 relayed message types + Remove + Couple but not Proto. Without this
    // guard a modified client can mutate any other agency's vessel state by crafting
    // a proto with a foreign vessel-id — the OwningAgencyId is preserved (existing-
    // wins per 5.16b) but the rest of the proto bytes overwrite the authoritative
    // store. See docs/research/v4-vessel-proto-cross-agency-write-guard.md for the
    // full trace + threat model.
    if (RejectIfCrossAgencyWrite(client.PlayerName, msgData)) return;

    // ... rest unchanged (line 285+) ...
}
```

### 2.b Why this works

The existing `RejectIfCrossAgencyWrite` helper at [VesselMsgReader.cs:208-228](../../Server/Message/VesselMsgReader.cs#L208-L228) implements exactly the right semantics for the proto path:

- **Gate off** → return false (bypass). Under shared-agency mode, cross-agency is undefined; legacy behavior preserved.
- **Vessel not in store** → return false (bypass). First-time-seen vessel ids fall through. This is the CORRECT default for proto — proto is the entry point for new vessels.
- **Unassigned-sentinel (`OwningAgencyId == Guid.Empty`)** → return false (bypass). Spec §10 Q3: any agency may interact with pre-0.31 vessels.
- **Sender has no agency mapping** → return false (defensive bypass).
- **Same-agency** → return false (bypass).
- **Cross-agency** → return true + Warning log. **Closes the attack.**

The existing helper's bypass-cases all map to legitimate proto-traffic shapes; the only path it newly rejects is the cross-agency attack.

### 2.c Order matters — guard BEFORE Task.Run

`RawConfigNodeInsertOrUpdate` ([VesselDataUpdater.cs:73-169](../../Server/System/Vessel/VesselDataUpdater.cs#L73-L169)) wraps its work in `Task.Run` for fire-and-forget ingest. The cross-agency guard MUST execute on the synchronous receive thread BEFORE the Task.Run hand-off — otherwise a fast attacker could race multiple protos into the Task.Run queue before the first one's ingest completes. The early-return shape at line 304 placement is correct.

The relay at line 317 fires AFTER `RawConfigNodeInsertOrUpdate` returns (the Task.Run is fire-and-forget — the synchronous flow continues immediately to the relay). With the guard placed BEFORE both, both the store update AND the relay are suppressed on rejection. Same semantics as the past-subspace check.

---

## 3. Defense layers + edge cases

### 3.a The race-window edge case (documented limitation)

**Scenario:** Bob attempts to pre-create a vessel-id BEFORE Alice's first proto for that id reaches the server.

1. Alice's KSP creates vessel `V_A` locally. Alice's first proto for `V_A` is in-flight (~2.5s KSP-side broadcast cadence).
2. Bob crafts a proto with `VesselId = V_A`. Bob sends it FIRST.
3. Server-side: `V_A` is not in store. Cross-agency guard bypasses ("vessel not in store" branch). Bob's proto lands. `RawConfigNodeInsertOrUpdate` stamps `V_A.OwningAgencyId = AgencyB` (sender's agency, since no existing entry to preserve).
4. Alice's legitimate proto for `V_A` arrives. Cross-agency guard fires (Alice = AgencyA, stored `V_A` = AgencyB). Alice's proto is REJECTED.
5. **Alice's own vessel is locked out by Bob's race-craft.**

**Why this race is narrow in practice:**

- Bob would need to know Alice's vessel-id BEFORE Alice broadcasts it. KSP-side vessel-ids are persistent Guids (`Vessel.id`) generated at vessel-creation time. They're not predictable.
- The race window is ~2.5s between Alice's KSP creating the vessel and Alice's first `BackupVessel` proto reaching the server.
- An adversary would need either side-channel knowledge of the freshly-minted Guid (chat? screen-share? observation of Alice's local logs?) or brute-force attempts at random Guids (computationally infeasible).
- The attack is irreversible without operator intervention: once Bob has stamped `V_A`, Alice can't reclaim it without admin `transferagency` (Stage 5.18d).

**Why the fix accepts this:**

- The race attack requires extraordinary effort and side-channel knowledge.
- Closing it requires per-vessel-creation pre-registration (e.g. a `VesselReserveMsgData` that lets a client claim a Guid before its first proto). That's a larger design change.
- Stage 6 / future work can layer a proper "vessel-creation attestation" surface on top if needed.
- The fix CLOSES the broad attack (modifying existing vessels) which is the realistic exploit. The race-craft-pre-create variant is documented as a known limitation.

**Mitigation operators can apply today:** keep server logs at Warning level; monitor `[fix:per-agency-career] refusing relay` log lines. A burst of cross-agency rejections targeting a specific vessel-id is a signal of an in-progress race attack. Operator can `deleteagency` the attacker.

### 3.b setvesselagency interaction

`setvesselagency` (Stage 5.18d slice (e) + Phase 3 Slice E-2) mutates `vessel.OwningAgencyId` from agency A to agency B under `VesselDataUpdater.GetVesselLock` per its M1 review. After the mutation:

- New owner B's first proto for the vessel: cross-agency guard reads the updated `vessel.OwningAgencyId == AgencyB`, sender is B, same-agency, passes.
- Old owner A's first proto AFTER setvesselagency: cross-agency guard reads `vessel.OwningAgencyId == AgencyB`, sender is A, **different** — rejected with Warning.

The expected behavior: after a setvesselagency, the OLD owner's KSP client may still hold the vessel locally and try to send protos. These get rejected at the new cross-agency guard. This is correct — A no longer owns the vessel; A's mutations should not propagate. KSP-side, A's client will eventually receive the next `VesselSync` reply or `AgencyVisibilityMsgData` and stop sending. **Transient warning-flood window during the handoff is acceptable.**

The Stage 5.18d slice (e) caller releases A's stale locks via `LockSystem.ReleasePlayerLocks` after the stamp mutation (per `[[project-mks-smoke-backlog]]` item 6). Combined with the new proto-write guard, A's vessel-relevant traffic is fully scrubbed.

### 3.c deleteagency interaction

`deleteagency` walks the agency's vessels and demotes each to Unassigned-sentinel (`vessel.OwningAgencyId = Guid.Empty`) per `Server/Command/Command/DeleteAgencyCommand.cs:241-269 ReleaseOldOwnerLocksOnDemotedVessels`. After demotion:

- Any agency's proto for the demoted vessel: cross-agency guard reads `vessel.OwningAgencyId == Guid.Empty`, bypass branch fires, passes.

This is correct per spec §10 Q3 — Unassigned-sentinel vessels accept any agency's writes.

### 3.d VesselSync interaction

`HandleVesselsSync` ([VesselMsgReader.cs:320-383](../../Server/Message/VesselMsgReader.cs#L320-L383)) is server-→-client only (the server sends proto replies in response to a sync request). The guard is on the inbound C→S path; sync replies don't go through `HandleVesselProto`. No interaction.

### 3.e Mod-compat S1 (AgencyVesselCoupleReconciler) interaction

`AgencyVesselCoupleReconciler.Reconcile` at [Server/System/Agency/AgencyVesselCoupleReconciler.cs](../../Server/System/Agency/AgencyVesselCoupleReconciler.cs) reconciles `lmpOwningAgency` on the surviving vessel after a Part.Couple. Runs from `HandleVesselCouple` at line 434. The couple handler's existing `RejectIfCrossAgencyWrite` at line 410 already gates on the dominant vessel's agency. The reconciler runs only on accepted couples. No interaction.

### 3.f Mod-compat S2 / S4 interactions

`AgencyScanRouter` (SCANsat) and `AgencyDMagicRouter` (DMagic) run from the `ScenarioBaseDataUpdater` path, NOT the vessel-proto path. No interaction.

### 3.g BUG-010 vessel pinning interaction

`VesselPinnedSystem` ([Server/Client/ClientConnectionHandler.cs](../../Server/Client/ClientConnectionHandler.cs)) broadcasts `VesselPinned` for the disconnected player's lock-held vessels. The pin is a client-side immortality flag on the receiving clients; it doesn't affect server-side proto routing. After the leaving player disconnects, the cross-agency guard continues to reject any other agency's protos for the pinned vessel. Pin survives correctly until the original pilot returns or another player of the same agency takes the lock.

---

## 4. Test plan

### 4.a ServerTest unit cases

**`ServerTest/VesselMsgReaderProtoCrossAgencyTest.cs`** (~7 cases):

1. `GateOff_AnyProto_AllowedThrough` — `PerAgencyCareer=false`; cross-agency proto for an existing vessel passes; store updated.
2. `GateOn_SameAgencyProto_AllowedThrough` — Alice's proto for Alice's vessel passes; store updated.
3. `GateOn_CrossAgencyProto_Rejected` — Bob's proto for Alice's existing vessel rejected; store unchanged; Warning logged.
4. `GateOn_UnassignedSentinelVessel_AllowedThrough` — Any agency's proto for an `OwningAgencyId == Guid.Empty` vessel passes; store updated. (Spec §10 Q3.)
5. `GateOn_NewVesselId_AllowedThrough_StampsSenderAgency` — Vessel not in store; cross-agency check bypasses; new vessel stamps to sender's agency.
6. `GateOn_SenderHasNoAgencyMapping_AllowedThrough` — Defensive bypass when `AgencyByPlayerName.TryGetValue` misses.
7. `GateOn_CrossAgencyProto_DoesNotRelay` — Verifies the relay is suppressed alongside the store update (no peer-broadcast of the rejected payload).

### 4.b MockClientTest end-to-end

**`MockClientTest/ProtoCrossAgencyRejectionTest.cs`** (~3 cases):

1. `BobCrossAgency_Proto_DroppedAtServer_AliceVesselUnchanged` — End-to-end: Alice creates vessel; Bob crafts proto with Alice's vessel-id + modified crew list; server rejects; Alice's vessel state on disk is unchanged; Bob's proto bytes are NOT relayed to other clients.
2. `BobCrossAgency_Proto_LegitimateProtoStillWorks` — Positive control: after Bob's cross-agency proto is rejected, Alice's NEXT legitimate proto for the same vessel passes normally.
3. `BobCrossAgency_Proto_LogsWarning` — Verifies the Warning-level log line emits with the expected `[fix:per-agency-career]` prefix for operator grep visibility.

### 4.c No new wire types

The fix uses the existing `RejectIfCrossAgencyWrite` helper. No new MsgData, no new enum slot, no protocol bump. **v4 protocol stays at 0.31.0.**

---

## 5. Out-of-scope (deferred)

| Surface | Why not in v4 | Where it lives |
|---------|---------------|----------------|
| Race-craft-pre-create attack (§3.a) | Narrow exploit; closing requires pre-registration design. | Stage 6 / future. |
| Per-field proto validation (e.g. "Bob can't change the parts list but can update position") | Phase 4-style + would require per-field cross-agency policy. | Out of scope. |
| WOLF privacy partition (cross-agency depot/route/etc. visibility) | The actual Phase 4 work — wire surface + routers + projector. | v5 (Phase 4 pre-spec). |
| Cross-agency kerbal authority on stock KSP crew transfer | Stock crew transfer goes through the relay-path message types already covered by 5.17a-write-counterpart. No new gap. | Verified covered. |
| Proto-write hardening for VesselSync replies (server-→-client direction) | One-way server-authoritative; no exploit surface. | N/A. |
| `VesselReserveMsgData` pre-registration for new vessel ids | Closes the race-window. Larger design. | Stage 6 candidate. |

---

## 6. Risks + mitigations

**Risk 1 — False positives.** A legitimate KSP-side flow that produces a cross-agency proto in normal operation would be silently broken.

*Mitigation:* The legitimate flows that produce protos for OTHER agencies' vessels are: (a) tracking-station "Fly" of another player's vessel, which is the EXPLOIT we're closing (under per-agency mode, you can't legitimately fly another agency's vessel); (b) setvesselagency / deleteagency, which mutates ownership BEFORE the new owner's proto arrives — guard reads the post-mutation agency, no rejection. No known legitimate cross-agency-proto-write path under gate=on.

*Verification:* test 4.b.2 covers the post-setvesselagency case implicitly (Alice's "next legitimate proto" works); add an explicit `MockClientTest/ProtoSetVesselAgencyHandoffTest.cs` case if soak surfaces a regression.

**Risk 2 — Performance.** Adding one more synchronous check per proto.

*Mitigation:* `RejectIfCrossAgencyWrite` is ~5 dict lookups + Guid compares per call. Negligible at expected proto cadence (~2.5s per vessel per active client). The existing 11 relayed-message-type call sites already pay this cost.

**Risk 3 — Race-craft-pre-create attack (§3.a).**

*Mitigation:* Documented as known limitation. Operator monitoring via Warning log lines. Stage 6 work for proper closure.

**Risk 4 — Stage 5.18b force-full-sync interaction.**

*Mitigation:* `HasReceivedInitialVesselsSync` (line 332-348) ensures a reconnecting client receives all vessels from the authoritative store via server-→-client proto replies (which don't go through `HandleVesselProto` and aren't gated). The client's subsequent C→S protos for its OWN vessels are same-agency and pass. No interaction.

---

## 7. Implementation slice

Single commit:

- `Server/Message/VesselMsgReader.cs:283-284` — add the guard line + comment.
- `ServerTest/VesselMsgReaderProtoCrossAgencyTest.cs` — new test file with 7 cases.
- `MockClientTest/ProtoCrossAgencyRejectionTest.cs` — new test file with 3 cases.
- `Server/ForkBuildInfo.cs` — append `"v4-proto-write-guard"` to `ActiveFixes[]`.
- `CLAUDE.md` — Stack Notes entry documenting the proto-path guard + the race-window limitation.
- `.claude/breakage-analyses/proto-cross-agency-write-guard.md` — breakage analysis per fork convention.

Estimated effort: **~1 session (3-4 hours)** including the multi-lens review per `[[feedback-review-lens-framing]]` + `[[feedback-integration-logic-review]]`.

---

## 8. Multi-lens review prompts (for impl session)

Before commit:

1. **General lens** — verify line placement; cross-check helper bypass cases against the proto context; verify no regression in past-subspace + zero-bytes ordering.
2. **Consumer lens** — verify the test coverage matches the 7 + 3 cases here; check that `MockClientTest` harness has the helper hooks to craft Bob's modified proto.
3. **Upgrade lens** — verify behavior under setvesselagency / deleteagency / 5.18b reconnect; confirm no soak regression vs the existing 5.17a-write surface.
4. **Integration-logic lens** — trace end-to-end:
   - Scenario: Alice creates vessel + Bob attempts cross-agency proto + Alice's legit proto continues working.
   - Scenario: setvesselagency A→B happens mid-session + A's stale protos rejected post-handoff + B's protos work.
   - Scenario: deleteagency demotes vessels to Unassigned + any agency's protos work on those vessels post-demotion.

---

## 9. Cross-links

- [mks-lmp-compatibility-phase-4-prespec.md](mks-lmp-compatibility-phase-4-prespec.md) §8.e — corrected to reference this scoping doc.
- [Server/Message/VesselMsgReader.cs:208-228](../../Server/Message/VesselMsgReader.cs#L208-L228) — `RejectIfCrossAgencyWrite` (existing helper, reused).
- [Server/Message/VesselMsgReader.cs:262-318](../../Server/Message/VesselMsgReader.cs#L262-L318) — `HandleVesselProto` (the edit site).
- [Server/System/Vessel/VesselDataUpdater.cs:73-169](../../Server/System/Vessel/VesselDataUpdater.cs#L73-L169) — `RawConfigNodeInsertOrUpdate` (downstream consumer; behavior preserved).
- [[5.18b relay-vs-store note]] — established contract that relayed proto bytes are advisory. The new guard does NOT change that; it adds the missing server-store-write protection that the prior note unintentionally implied was already in place.
- [[project-per-agency-pickup]] — Stage 5 tracker; this fix joins the per-agency hardening lineage (5.17a + 5.17a-write-counterpart + 5.18g + this).
- [[feedback-review-lens-framing]] — 4-lens review discipline.

---

_End of v4 VesselProto cross-agency write guard scoping note. Next: implementation commit (1 file edit + 2 test files + ForkBuildInfo + CLAUDE.md + breakage analysis), targeting inclusion in v4 release._
