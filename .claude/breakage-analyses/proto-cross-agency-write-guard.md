# v4 VesselProto cross-agency write guard — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `0d99a81a` (Stage 5.18g — untrusted-cohort hardening)
**Discipline:** Per [[feedback-breakage-analysis]] — mandatory before non-trivial changes.
**Motivation:** The 2026-05-19 session-39 WOLF Phase 4 pre-spec multi-lens review uncovered that `Server/Message/VesselMsgReader.HandleVesselProto` is NOT gated by `RejectIfCrossAgencyWrite`. The Stage 5.17a write-path counterpart from session 19 gated 11 relayed message types + `HandleVesselRemove` + `HandleVesselCouple` but omitted the proto handler. The omission was rationalised at the time via the "relayed proto bytes are advisory" framing in the [[5.18b relay-vs-store note]] — but that framing was about peer-client *interpretation* of relayed bytes, NOT the server's authoritative store, which DOES get overwritten by the proto bytes (the 5.16b stamp-preservation logic at `VesselDataUpdater.cs:146-160` preserves only `OwningAgencyId`; crew list / parts / resources / position / lmpAuthSubspace are unprotected). A modified client can craft a `VesselProtoMsgData` for any other agency's vessel-id and the server persists those bytes + broadcasts them to every connected peer.

**Policy choice (operator-confirmed session 39):** Path B — ship the proto-guard in v4 as a focused 1-line fix before WOLF Phase 4 (v5) begins. Closes the broad cross-agency vessel-state-write exploit class. The WOLF-specific kerbal-seizure path is one symptom; closing the underlying proto-write hole is broader and more valuable than Phase 4's WOLF UX gate alone.

---

## Scope lock — IS

### 1. Proto handler cross-agency gate

- Edit [Server/Message/VesselMsgReader.cs:262-318](../../Server/Message/VesselMsgReader.cs#L262-L318) `HandleVesselProto`.
- Add `if (RejectIfCrossAgencyWrite(client.PlayerName, msgData)) return;` between the past-subspace check (line 277-283) and the sender-agency resolution (line 297-302).
- **No new code in `RejectIfCrossAgencyWrite`** — the existing helper is type-agnostic (reads only `VesselId`) and its bypass cases (gate off / vessel not in store / Empty-sentinel / sender no agency) are exactly what the proto path needs.
- The `return` on rejection short-circuits BOTH `RawConfigNodeInsertOrUpdate` (store update via `Task.Run`) AND `MessageQueuer.RelayMessage` (peer broadcast). Same semantics as the past-subspace check.

### 2. ServerTest unit cases — `ServerTest/VesselMsgReaderProtoCrossAgencyTest.cs` (new file, 7 cases)

Mirror the existing `VesselMsgReaderCrossAgencyTest.cs` test shape but with `VesselProtoMsgData`. The helper is type-agnostic so behavior is identical to the 11 relayed types — a dedicated test file aids discoverability + documents the proto-specific intent + future-proofs against divergence:

1. `GateOff_AnyProto_AllowedThrough` — `PerAgencyCareer=false` bypass.
2. `GateOnButSandboxMode_AnyProto_AllowedThrough` — non-Career game mode bypass.
3. `GateOn_SameAgencyProto_AllowedThrough` — happy path.
4. `GateOn_CrossAgencyProto_Rejected` — the closed exploit.
5. `GateOn_UnassignedSentinelVessel_AllowedThrough` — spec §10 Q3 bypass.
6. `GateOn_NewVesselId_AllowedThrough` — vessel-not-in-store fallthrough (correct default for proto path; per-agency stamp routes new vessels to sender's own agency).
7. `GateOn_SenderHasNoAgencyMapping_AllowedThrough` — defensive bypass.

### 3. MockClientTest e2e cases — `MockClientTest/ProtoCrossAgencyRejectionTest.cs` (new file, 3 cases)

Mirror the existing `CrossAgencyVesselRelayTest.cs` test shape:

1. `BobCrossAgency_Proto_DroppedAtServer_AliceVesselUnchanged` — full wire round-trip; watcher does NOT receive Bob's crafted proto for Alice's vessel; the in-store Vessel reference is unchanged (Bob's bytes did not replace the authoritative record).
2. `AliceSameAgency_Proto_RelaysToWatcher` — positive control; same-agency proto reaches watcher.
3. `UnassignedSentinelVessel_AnyAgencyProtoRelays` — spec §10 Q3 e2e verification.

### 4. ForkBuildInfo entry

- Append `"v4-proto-write-guard"` to [Server/ForkBuildInfo.cs:18-47](../../Server/ForkBuildInfo.cs#L18-L47) `ActiveFixes[]` in commit-chronological order (between `S7-EPL` and `5.18g-hardening`).
- Operator banner visibility: `[fix:per-agency-career] refusing relay` Warning log is the runtime grep target (same prefix as the existing helper).

### 5. CLAUDE.md Stack Notes entry

- Append a chronological entry to the "Stack Notes & Patterns Learned" section documenting the proto-path gap, the framing-rationale-was-incomplete root cause, and the recipe for future write-counterpart-style guards.

### 6. Scoping doc cross-link

- The detailed scoping doc at [docs/research/v4-vessel-proto-cross-agency-write-guard.md](../../docs/research/v4-vessel-proto-cross-agency-write-guard.md) is referenced from the new code's XML doc-comment + the ForkBuildInfo entry + this breakage analysis. Not edited in this commit; pre-exists from session 39 scoping work.

---

## Scope lock — IS NOT

- **NOT** a fix for the race-craft-pre-create attack (Bob pre-broadcasting a proto for a vessel-id Alice is about to create). Documented in the scoping doc §3.a as a known limitation; closing it requires a `VesselReserveMsgData` pre-registration design which is Stage 6 territory.
- **NOT** any new wire MsgData type. The fix reuses existing infrastructure entirely.
- **NOT** any change to `RawConfigNodeInsertOrUpdate` or `RejectIfCrossAgencyWrite`. Both are unmodified; the fix only adds a CALL to the helper from a new site.
- **NOT** a fix for the `HandleVesselsSync` path. That handler is server-→-client only (server emits proto replies in response to a sync request); no inbound proto-write surface to gate.
- **NOT** a protocol-version bump. v4 stays at 0.31.0. The fix is a server-side behavior change with no wire-format impact. (v5 will need a bump for WOLF Phase 4's new MsgData slots — separate commit.)
- **NOT** any per-field validation of proto bytes (e.g. "Bob can update position but not parts list"). The cross-agency gate is all-or-nothing — if Bob is cross-agency relative to the target vessel, the entire proto is dropped.
- **NOT** any change to the relay framing for accepted protos. The [[5.18b relay-vs-store note]] still applies for peer-client interpretation; this fix changes only the server's accept/reject decision.
- **NOT** WOLF Phase 4 work. Phase 4 ships in v5; this fix is the v4 prerequisite.

---

## Edge cases enumerated

### Wire-protocol semantics

1. **Cross-agency proto on existing vessel** — guard fires; `Warning` logged via the existing helper's log line; store unchanged; no relay. Closes the documented exploit. Pinned by `GateOn_CrossAgencyProto_Rejected` (ServerTest) + `BobCrossAgency_Proto_DroppedAtServer_AliceVesselUnchanged` (MockClientTest).

2. **Same-agency proto on existing vessel** — guard returns false; legacy path runs unchanged; vessel state updates; relay broadcasts. Pinned by `GateOn_SameAgencyProto_AllowedThrough` + `AliceSameAgency_Proto_RelaysToWatcher`.

3. **New vessel (not in store)** — guard returns false via the vessel-not-in-store bypass; `RawConfigNodeInsertOrUpdate` runs; sender's agency is stamped per 5.16b branch (b). First proto wins. Pinned by `GateOn_NewVesselId_AllowedThrough`.

4. **Unassigned-sentinel vessel (`OwningAgencyId == Guid.Empty`)** — guard returns false via the sentinel bypass; any agency may proto. Spec §10 Q3 compliance. Pinned by `GateOn_UnassignedSentinelVessel_AllowedThrough` + `UnassignedSentinelVessel_AnyAgencyProtoRelays`.

5. **Gate off (`PerAgencyCareer=false`)** — guard returns false; legacy path runs unchanged; vanilla relay behaviour preserved. Dual-mode silence. Pinned by `GateOff_AnyProto_AllowedThrough`.

6. **Sandbox / Science game mode** — guard returns false (agency surface inactive); legacy path runs. Pinned by `GateOnButSandboxMode_AnyProto_AllowedThrough`.

7. **Sender has no agency mapping** — guard returns false via the defensive bypass. Production path: `OnPlayerAuthenticated` runs `RegisterAgency` on the same Lidgren receive thread before any vessel CliMsg can be processed, so this case is structurally impossible in production. Defensive test only. Pinned by `GateOn_SenderHasNoAgencyMapping_AllowedThrough`.

### Interaction with existing handlers

8. **`HandleVesselRemove` + new proto guard** — independent; `HandleVesselRemove` already calls `RejectIfCrossAgencyWrite` at line 244. No interaction.

9. **`HandleVesselCouple` + new proto guard** — independent; `HandleVesselCouple` already calls `RejectIfCrossAgencyWrite` at line 410. The couple-side reconciler at `AgencyVesselCoupleReconciler.Reconcile` runs only on accepted couples; no new interaction.

10. **5.18b force-full-sync** — `HasReceivedInitialVesselsSync` triggers `GetVesselInConfigNodeFormat` to ship server-authoritative bytes to the reconnecting client. Server-→-client direction; doesn't go through `HandleVesselProto`. No interaction.

11. **`AgencyVesselCoupleReconciler` (Mod-compat S1)** — runs only inside `HandleVesselCouple` post-decision. Not on the proto path. No interaction.

12. **`AgencyVesselSyncPolicy.ShouldFullSync`** — server-side decision about whether to send a full sync; doesn't affect proto acceptance. No interaction.

### Race / concurrency

13. **`Task.Run` fire-and-forget in `RawConfigNodeInsertOrUpdate`** — the cross-agency guard runs SYNCHRONOUSLY on the Lidgren receive thread BEFORE the `Task.Run` hand-off. Rejection short-circuits both store update + relay atomically. Pinned by the call-site ordering (line 285 = guard, line 304 = `RawConfigNodeInsertOrUpdate` call).

14. **Concurrent cross-agency proto bursts** — N rejection log lines fire from N threads. The `LunaLog.Warning` path is thread-safe (Stage 5.18g introduced atomic console output via the BUG-037 fix). KSP-side a buggy or hostile client at ~2.5s proto cadence could spam this; if soak shows the flood is real, rate-limit per (sender, vessel) — keep the flat Warning for now and react if needed. Same posture as the existing relay-path Warning at line 226.

15. **setvesselagency transfer A→B** — after the admin command mutates `vessel.OwningAgencyId = B`, the guard now reads B as the authoritative owner. Old owner A's next proto is rejected (cross-agency). Transient Warning-flood during the handoff window until A's client receives `AgencyVisibilityMsgData` and stops sending. Acceptable per the 5.18d slice (e) caller contract.

16. **deleteagency demoting vessels** — `DeleteAgencyCommand.cs:241-269` demotes vessels to `OwningAgencyId = Guid.Empty` (Unassigned sentinel). Post-demotion, any agency may proto for them (sentinel bypass). Correct per spec §10 Q3.

### Race-craft-pre-create (documented limitation)

17. **Bob pre-creates Alice's vessel-id** — Bob sends a proto for a Guid Alice's KSP is about to create. Vessel not in store → guard bypasses → Bob's proto lands → vessel stamped to Bob's agency. Alice's subsequent legitimate proto for the same Guid is then rejected (cross-agency). Alice's own vessel is locked out by Bob's race.

    **Narrow exploit** — requires Bob to know Alice's freshly-minted persistent Guid within a ~2.5s window; KSP-side Guids are not predictable. **NOT closed** by this fix; deferred to Stage 6 / `VesselReserveMsgData` pre-registration design. Operator mitigation: watch for `[fix:per-agency-career] refusing relay` Warning bursts targeting an unknown vessel-id; admin `deleteagency` Bob if observed. Documented in scoping doc §3.a + the new code's comment block.

### Build + test surface

18. **Existing `RejectIfCrossAgencyWrite` contract** — fix changes NEITHER the helper's signature NOR its bypass cases. Pure additive call site. Pinned regression: existing `VesselMsgReaderCrossAgencyTest.cs` (7 cases for the 11 relayed types) continues to pass without modification.

19. **Existing `HandleVesselProto` past-subspace check** — unchanged; ordering preserved (past-subspace runs FIRST so a past-subspace cross-agency proto still gets the past-subspace log line, not the cross-agency one). Pinned implicitly by the ordering in the existing past-subspace test fixture.

20. **`VesselSyncMsgData` (legacy sync request)** — server-side handler at `HandleVesselsSync` doesn't dispatch through `HandleVesselProto`. The fix doesn't affect sync replies. Pinned by the call-graph (line 33 `case VesselMessageType.Sync` → `HandleVesselsSync` is a separate handler).

---

## Lens-review surface

Multi-lens review per [[feedback-review-lens-framing]] + [[feedback-integration-logic-review]] runs BEFORE commit. Specific surfaces to verify:

- **General lens** — line placement correct (between past-subspace check and sender-agency resolution); cross-check helper bypass cases against proto context (vessel-not-in-store fallthrough is intentional + safe per the 5.16b stamp logic); no regression in past-subspace + zero-bytes + RemovedVessels ordering.
- **Consumer lens** — test coverage matches the 7 + 3 cases; `MockClientTest` harness has the helper hooks to craft Bob's modified proto (verified by reading `CrossAgencyVesselRelayTest.cs` template); breakage analysis edge cases all have tests.
- **Upgrade lens** — behaviour under setvesselagency / deleteagency / 5.18b reconnect verified above; no soak regression vs the existing 5.17a-write surface; no new boot-time diagnostic needed (the fix is gate-conditional via the existing helper, dual-mode silent under gate off).
- **Integration-logic lens** — trace end-to-end: (a) Alice creates vessel + Bob attempts cross-agency proto + Alice's legit proto continues working; (b) setvesselagency A→B happens mid-session + A's stale protos rejected post-handoff + B's protos work; (c) deleteagency demotes vessels to Unassigned + any agency's protos work on those vessels post-demotion.

---

## File-size compliance

- `Server/Message/VesselMsgReader.cs` — pre-fix 447 lines; +19 lines added (1 code line + 1 blank + 17 comment block) + the post-review XML doc extension on `RejectIfCrossAgencyWrite` adds ~18 more lines = final 484 lines. Well under the 600 soft / 900 hard cap.
- `ServerTest/VesselMsgReaderProtoCrossAgencyTest.cs` — new test file, ~220 lines. Test files exempt from the cap.
- `MockClientTest/ProtoCrossAgencyRejectionTest.cs` — new test file, ~260 lines. Test files exempt.
- `Server/ForkBuildInfo.cs` — +1 array entry; no structural growth.
- `CLAUDE.md` — +2 chronological Stack Notes entries; project doc, no cap.
- `.claude/breakage-analyses/proto-cross-agency-write-guard.md` — this file, ~250 lines. Doc, no cap.

---

## Post-ship items (NOT in this commit)

- **Race-craft-pre-create closure** — `VesselReserveMsgData` design. Stage 6 / future iteration.
- **Rate-limiting on the rejection log line** — if soak shows the Warning flood is real under hostile clients, add per-(sender, vessel) throttle. Mirrors the existing relay-path Warning at line 226 which has the same posture.
- **WOLF Phase 4 (v5)** — pre-spec at [docs/research/mks-lmp-compatibility-phase-4-prespec.md](../../docs/research/mks-lmp-compatibility-phase-4-prespec.md); the §8.e Path A re-derivation was corrected in session 39 to reference this fix as a prerequisite. Slice A starts after this fix ships in v4.

---

## Rollback plan

Single-line fix; revert is `git revert <sha>` clean. No data migrations, no wire-format changes, no persistence schema impact. Operators downgrading from a v4-with-proto-guard build to a pre-v4 build see the cross-agency-write hole reopen but no data loss. Pre-v4 → v4-with-proto-guard upgrade is silent — no boot diagnostics, no operator action required.

---

## Cross-links

- Scoping doc: [docs/research/v4-vessel-proto-cross-agency-write-guard.md](../../docs/research/v4-vessel-proto-cross-agency-write-guard.md)
- Edit site: [Server/Message/VesselMsgReader.cs](../../Server/Message/VesselMsgReader.cs)
- Helper reused: `RejectIfCrossAgencyWrite` at [Server/Message/VesselMsgReader.cs:208-228](../../Server/Message/VesselMsgReader.cs#L208-L228)
- Downstream consumer: [Server/System/Vessel/VesselDataUpdater.cs:73-169](../../Server/System/Vessel/VesselDataUpdater.cs#L73-L169)
- Phase 4 pre-spec (corrected to reference this fix): [docs/research/mks-lmp-compatibility-phase-4-prespec.md](../../docs/research/mks-lmp-compatibility-phase-4-prespec.md) §1.b + §8.e
- Related precedent: [[stage-5-18g-untrusted-cohort-hardening]] breakage analysis (same session, same untrusted-cohort hardening lineage)
- Related precedent: [[S1-Couple]] breakage analysis (also added `RejectIfCrossAgencyWrite` interaction)
- [[feedback-research-first]] — pre-spec lens review caught the gap by reading actual files (vs trusting the "5.17a closes it" claim)
- [[feedback-review-lens-framing]] — multi-lens review will run pre-commit per established discipline
