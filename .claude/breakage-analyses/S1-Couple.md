# S1 (vessel-couple ownership reconciler) — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `06cc7444` (Mod-compat S4 ship)
**Discipline:** Per [[feedback-breakage-analysis]] — mandatory before non-trivial changes.
**Structural authority:** [docs/mod-compat/implementation-spec.md](../../docs/mod-compat/implementation-spec.md) §S1 + [docs/mod-compat/kerbal-attachment-system.md](../../docs/mod-compat/kerbal-attachment-system.md) "Decisions ratified — 2026-05-18".
**Upstream pin verified:** `F:/tmp/mks-external/KAS` SHA `86f801dd` — `Source/api_impl/LinkUtilsImpl.cs:96` confirms `srcPart.Couple(tgtPart)` (stock KSP `Part.Couple`). Cross-checked against `LmpClient/Harmony/Part_Couple.cs` (the Harmony patch that turns every `Part.Couple` into `PartEvent.onPartCoupled` → `VesselCoupleMsgData` on the wire). KAS coupling and stock docking are indistinguishable at the server's `HandleVesselCouple` ingress.
**Precedents:** Stage 5.18d `SetVesselAgencyCommand` (the worked precedent for `OwningAgencyId` mutation contract: lock + broadcast + flush + per-router migration). S2 + S4 (Path B suppression + projector splice pattern — not applicable here; S1 is a server-side helper on the ingress path, not a scenario router).

---

## Scope lock — IS

- New `Server/System/Agency/AgencyVesselCoupleReconciler.cs` — pure static helper.
  - Public `Reconcile(Guid keptVesselId, Guid mergedVesselId)`.
  - Rule table: kept-wins, with Unassigned-adopt exception (kept Empty + merged tracked → adopt merged stamp).
  - **M1 (multi-lens review must-fix):** mutation + paired vessel reads held under `VesselDataUpdater.GetVesselLock(keptVesselId)` to serialise against the proto-ingest path's `existingStored` read + `AddOrUpdate` write.
  - **M2 (multi-lens review must-fix):** on the adopt branch, broadcast `AgencyVisibilityMsgData` via `AgencySystemSender.BroadcastVisibilityChange` so peer clients' 5.18b `AgencyMembership.VesselOwnership` mirror updates immediately (KSP strips `lmpOwningAgency` on owner resends per the 5.18b relay-vs-store contract), then `BackupSystem.RunBackup` for crash-window persistence.
- Edits to `Server/Message/VesselMsgReader.cs::HandleVesselCouple` — single call site inserted between the BUG-005/006 authority-subspace update and the `RemoveVessel` call (after the existing `RejectIfCrossAgencyWrite` 5.17a write-path guard).
- New `ServerTest/AgencyVesselCoupleReconcilerTest.cs` — 10 cases covering the full rule table + dual-mode silence + race guards: gate=off / Sandbox short-circuit; kept-not-in-store / merged-not-in-store defensive races; same-agency / both-Empty idempotency; kept-Unassigned + merged-tracked adopt; merged-Unassigned + kept-tracked retain; cross-agency A-kept and B-kept Warning paths.
- Edit to `Server/ForkBuildInfo.cs::ActiveFixes` — new `"S1-Couple"` entry.
- Edit to `CLAUDE.md`:
  - Test count bump 519 → 529 + new `AgencyVesselCoupleReconcilerTest` line.
  - `AgencySystem` inventory row extended with the new helper.
- Edit to `docs/mod-compat/implementation-spec.md`:
  - S5/S6 hook targets corrected against verified upstream source (`ScienceContext.UpdateOnboardScience` instead of `GetUnloadedAndLoadedVesselScience`; `KIS_Shared.CreatePart` instead of `KISAddonPickup.FinishAttach`).
  - Tracking table extended with S1 + S4 shipped rows; S5 + S6 marked as separate-session LunaCompat sidecar work.
  - Header status block notes the 2026-05-19 audit-via-prespec re-walk caught the wrong-target errors before any LunaCompat code shipped.

---

## Scope lock — IS NOT

- **NOT** a per-router cleanup for the destroyed merged vessel's `AgencyState` entries (kolony / orbital / scanners — leaked entries remain in the source agency's state until next admin intervention). Documented as deferred follow-up in the reconciler XML. Pre-S1 this leak existed already; S1 does not make it worse.
- **NOT** a third-agency cross-reference inspection (`InspectThirdAgencyCrossReferences` from `SetVesselAgencyCommand`). Deferred as follow-up alongside per-router cleanup.
- **NOT** any client-side change. The reconciler is server-only; client-side `AgencyMembership.RecordOwnership` already absorbs the broadcast `AgencyVisibilityMsgData` (5.18b/5.18c contract).
- **NOT** a wire-protocol change. Reuses existing `AgencyVisibilityMsgData`.
- **NOT** any test or implementation work in LunaCompat — S5/S6 implementation belongs in `Majestic95/LunaCompat` (fork-of-sidecar), separate session. This commit corrects the spec's hook-name errors so the next LunaCompat-session author starts from accurate targets.

---

## Edge cases enumerated

1. **Gate off** — `PerAgencyEnabled` false (or non-Career game mode). Reconciler returns immediately; no observable behaviour change vs pre-S1. Pinned by `Reconcile_GateOff_NoMutationEvenWhenStampsDiffer` + `Reconcile_SandboxMode_NoMutation`.
2. **Kept vessel raced out of store** between `HandleVesselCouple` dispatch and reconcile (concurrent `VesselRemove`). `TryGetValue` returns false; Debug log; no-op. Pinned by `Reconcile_KeptNotInStore_NoThrowNoMutation`.
3. **Merged vessel raced out of store** before reconcile reads its stamp. Treated as `Guid.Empty`; reduces to kept-tracked + merged-Empty branch (no mutation). Pinned by `Reconcile_MergedNotInStore_TreatsMergedAsEmpty`.
4. **Same agency on both vessels** — idempotent no-op (no log). Pinned by `Reconcile_SameAgency_NoMutation`.
5. **Both Unassigned (pre-0.31 upgrade scenario)** — idempotent no-op. Pinned by `Reconcile_BothEmpty_NoMutation`.
6. **Kept Unassigned + merged tracked** (the only branch that mutates) — adopt merged stamp; Debug log; broadcast + flush. Pinned by `Reconcile_KeptUnassigned_MergedTracked_KeptAdoptsMergedStamp` (in-memory mutation) + manual verification of the broadcast/flush calls (the broadcast is a no-op in the unit test env with no connected clients; `BackupSystem.RunBackup` is safe to call without a populated universe — verified by green ServerTest run).
7. **Merged Unassigned + kept tracked** — kept retains, no mutation, Debug log. Pinned by `Reconcile_KeptTracked_MergedUnassigned_KeptUnchanged`.
8. **Cross-agency couple (both non-Empty, differ)** — kept wins per KSP determinism; merged vessel destroyed by caller's `RemoveVessel` + `VesselRemove` broadcast; Warning log. Pinned by `Reconcile_CrossAgency_AKept_KeptStampPreserved` + `Reconcile_CrossAgency_BKept_KeptStampPreserved`.
9. **Concurrent proto-ingest race** (M1) — reconciler holds `VesselDataUpdater.GetVesselLock` so a racing proto-ingest's `existingStored` read sees the mutated value and preserves it on the replacement `Vessel` instance. The orphan-write race documented in `SetVesselAgencyCommand.cs:297-312` is closed.
10. **Client-side mirror staleness on adopt** (M2) — `BroadcastVisibilityChange` pushes the new ownership to all peer clients before any subsequent proto-relay can land with KSP-stripped `lmpOwningAgency`. 5.18b's preservation rule (Empty never downgrades a tracked entry) means even if a proto without `lmpOwningAgency` lands first, the peer client's `VesselOwnership` map stays correct after the visibility broadcast.
11. **Server crash between reconcile and next periodic flush** (M2) — `BackupSystem.RunBackup` persists the adopted stamp synchronously; crash-window data loss closed.
12. **KAS coupling vs stock docking** — both surface as `VesselCoupleMsgData` on the same handler (verified at `KAS/Source/api_impl/LinkUtilsImpl.cs:96`). Reconciler is mod-agnostic.
13. **KIS re-attach** — does NOT route through `Part.Couple`; calls `KIS_Shared.MoveAssembly` instead. **NOT covered by S1.** S6 (LunaCompat sidecar) is load-bearing for KIS-attach ownership correctness. Documented in implementation-spec.md §S6.

---

## Tests authored

`ServerTest/AgencyVesselCoupleReconcilerTest.cs` — 10 cases listed above. Mirrors `AgencyScanRouterTest` setup pattern (sample vessel from `XmlExampleFiles/Others`; direct-poke into `AgencySystem.Agencies` + `AgencyByPlayerName`; per-test reset via `AgencySystem.Reset()` + `VesselStoreSystem.CurrentVessels.Clear()`).

Tests do NOT directly assert the broadcast / flush invocations — `AgencySystemSender.BroadcastVisibilityChange` requires a populated `ServerContext.Clients` and `BackupSystem.RunBackup` requires a universe directory. Both are gated internally on `PerAgencyEnabled` and on empty-input early-returns, so they are no-op in the unit-test environment. End-to-end coverage of the broadcast → client mirror propagation belongs in a future `MockClientTest` integration case.

---

## Test totals

- ServerTest: 519 → 529 (+10).
- LmpCommonTest / LmpClientTest / MockClientTest: unchanged. No wire-protocol additions; no client-side mirror changes.

---

## Multi-lens review outcomes

Per [[feedback-review-lens-framing]]: two parallel agents (general-correctness; consumer + upgrade combined).

| Finding | Lens | Resolution |
|---------|------|------------|
| M1 — `VesselDataUpdater.GetVesselLock` race | general + consumer/upgrade (both caught) | Fixed: read + mutation under per-vessel lock. |
| M2 — adopt branch missing broadcast + flush | consumer/upgrade | Fixed: `BroadcastVisibilityChange` + `BackupSystem.RunBackup` after lock release. |
| SHOULD FIX — per-router cleanup for destroyed merged vessel | consumer | Deferred — documented as known follow-up in reconciler XML. Pre-S1 leak; S1 does not amplify. |
| SHOULD FIX — third-agency `InspectThirdAgencyCrossReferences` | consumer | Deferred alongside per-router cleanup. |
| CONSIDER — log convention parity with `RejectIfCrossAgencyWrite` (`[fix:per-agency-career]` vs `[fix:S1-Couple]`) | general | Kept `[fix:S1-Couple]` for mod-compat family grep parity with S2/S4. Documented in `ForkBuildInfo.ActiveFixes`. |
| CONSIDER — `mergedVesselId == keptVesselId` defensive test | general | Skipped — code already short-circuits at same-agency branch; behavior safe by accident. |
| CONSIDER — operator-visible "merged raced out of store" diagnostic | general | Skipped — soak-risk-low; would generate noise on legitimate not-in-store races. |

---

## Acceptance criteria

- Cross-agency stock docking: agency A docks to agency B's station. Both clients see the merged vessel owned by whichever side KSP kept. No flicker. Warning line `[fix:S1-Couple] cross-agency couple: ...` in `/log`.
- Cross-agency KAS coupling: identical behaviour (because KAS rides `Part.Couple`).
- Mixed Unassigned + tracked couple: kept side adopts the tracked stamp; `AgencyVisibilityMsgData` broadcast lands at all peer clients within one tick.
- Server restart immediately after an adopt: disk vessel.cfg carries the new stamp (next `LoadExistingVessels` round-trips correctly).
- Gate=off (or non-Career game mode): SCANsat behaviour is identical to pre-S1; no reconciler activity in logs.

---

## Tracking

- Pre-implementation grep confirmed insertion point in `HandleVesselCouple` (between BUG-005/006 subspace stamp and `RemoveVessel`).
- LunaCompat / X-Science / KIS source-walk completed in same session — used to correct S5/S6 hook names in the spec; no code shipped in this commit for those slices.
- LunaCompat fork-vs-PR decision: fork at `Majestic95/LunaCompat`, S5+S6 implementation in a separate session.
