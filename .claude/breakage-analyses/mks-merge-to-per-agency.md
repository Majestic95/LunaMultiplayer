# Breakage Analysis: Merge `feature/per-agency-mks` → `feature/per-agency`

**Date:** 2026-05-18
**Author:** Claude (session resumed from [[project-mks-merge-to-per-agency]])
**Trigger:** `[[feedback-breakage-analysis]]` — analyze before running the merge command.

---

## Branch state (verified)

```
feature/per-agency-mks  tip 5f596961   (Phase 3 closed)
feature/per-agency      tip 89f9619e   (GUI MVP + 5.18d slices i/c/j/h)
merge-base              8f609963        (5.18d slice g)

mks side ahead by 18 commits
per-agency side ahead by 17 commits
```

Decisions captured: **merge commit** (preserves Phase 3 history), **full breakage analysis** (this doc), **both branches' baselines run** (below).

---

## Pre-merge test baselines

| Suite           | feature/per-agency-mks @ 5f596961 | feature/per-agency @ 89f9619e |
|-----------------|------------------------------------|-------------------------------|
| ServerTest      | 434 ✅                              | 348 ✅                          |
| LmpCommonTest   | 28 ✅                               | 20 ✅                           |
| MockClientTest  | 88 ✅                               | 65/67 (1–2 known harness flakes) |
| LmpClientTest   | 143 ✅                              | 103 ✅                          |
| **Total**       | **693 ✅**                          | **534/536** ⚠                  |

Per-agency MockClientTest flake `PastSubspaceProto_IsRejected_VesselUntouched_AndNotRelayed` matches `[[project-mock-harness-flakes]]` Sub-theory-1-REFUTED-s14 — known harness issue, fault is downstream of Lidgren send, NOT introduced by either branch.

**Post-merge gate:** for each suite, the merged tree must produce a count **≥ max(mks_tip, per_agency_tip)** plus the per-agency-only test additions since merge-base that the mks side does not yet have. Drop below that = real merge-introduced regression.

---

## Scope lock

**IS:**
- Merge commit on top of `feature/per-agency` (89f9619e + merge-commit), bringing all 18 mks-side commits (Phases 0/1/1.5R0/2/3 Slices A–E-2 + handoff docs + pre-spec docs) into per-agency line.
- Manual resolution of 2 textually-conflicting files.
- Sanity checks on 6 files mks touched but per-agency did NOT (auto-merged but they're in the agency surface — verify they didn't break).
- Build + test the merged tree; multi-lens review on the merge commit; bug-review receipt.
- Memory + CLAUDE.md updates to reflect the unified track.

**IS NOT:**
- New code on the merged tree. (Any [MUST FIX] from the multi-lens review will land as a separate post-merge commit, not amended into the merge.)
- Pushing to upstream. `feature/per-agency` is fork-local per CLAUDE.md (and so is `feature/per-agency-mks`).
- Touching the GUI worktree (`F:\luna-multiplayer-gui` on `feature/admin-gui`) — its parent is `feature/per-agency` and it'll pull the merge naturally when next rebased.
- Smoke-testing the 6 backlog items from `[[project-mks-smoke-backlog]]` — those are operator-blocked.

---

## Files touched

### Real textual conflicts (2)

#### 1. `Server/System/Agency/AgencyScenarioProjector.cs` — CONFLICT
**Diff sizes vs merge-base:** mks +~390 lines, per-agency +~215 lines.

**Per-agency additions (slice (j)):**
- New entry `"ContractSystem"` in the projector keys array (line ~76).
- New `SharedContractStates = { "Offered", "Generated" }` HashSet (after the keys array).
- New `case "ContractSystem":` branch in `Project()` (line ~172).
- New private method `SpliceAgencyContractsIntoScenario` (~210 lines, after the existing `SpliceAgencyTechIntoResearchAndDevelopment`).

**MKS-side additions (Phase 3 slices B/C/D-1):**
- `using LmpCommon.Message.Data.Agency;` import.
- 3 new entries in the keys array: `"KolonizationScenario"`, `"PlanetaryLogisticsScenario"`, `"ScenarioOrbitalLogistics"`.
- 3 new case branches in `Project()`.
- 3 new private splice methods (`SpliceAgencyKolonyEntries`, `SpliceAgencyPlanetaryEntries`, `SpliceAgencyOrbitalTransfers`) totaling ~350 lines.

**Resolution: TAKE-BOTH.** All additions are non-overlapping in intent (per-agency adds the contract splice; mks adds the 3 MKS-scenario splices). Conflicts are git-textual only because they target adjacent hunks. Steps:
1. Keys array: include all 4 new entries.
2. `SharedContractStates` HashSet: keep (per-agency-only addition).
3. Switch: include all 4 new cases.
4. File body: keep all 4 new private methods.
5. Import: keep `using LmpCommon.Message.Data.Agency;` (mks-only addition; the per-agency contract splice doesn't need it).
6. Verify no duplicate case labels and no method-name collisions.

#### 2. `Server/System/Agency/AgencySystem.cs` — CONFLICT
**Diff sizes vs merge-base:** mks +~640 lines, per-agency +~85 lines.

**Per-agency additions (slice (j)):**
- Single method rewrite: `WarnAboutSharedContractsOnUpgrade` (lines ~594–650 in merge-base). Comment refresh + log-message refresh reflecting that slice (j) made the strip real (was "Stage 5.18a hasn't shipped yet"). Counter variable renamed `active` → `nonShared`. Logic now strips on empty-state too (matches projector behavior).

**MKS-side additions (Phase 3 slices B/C/D-1 + E-1 + E-2):**
- `LoadExistingAgencies` early-return branch (~line 176): adds 6 new `WarnAboutShared*OnUpgrade` calls + `RefuseStartupIfUpgradeHazardWithoutOverride()`.
- `LoadExistingAgencies` main path (~line 275): adds 3 new Warn calls for kolony/planetary/orbital.
- `RefuseStartupIfUpgradeHazardWithoutOverride` (~line 379): adds 3 new hazard predicates.
- 6 new private methods at the end: `WarnAboutSharedKolonyOnUpgrade`, `WarnAboutSharedPlanetaryOnUpgrade`, `WarnAboutSharedOrbitalOnUpgrade`, and 3 more inherited from band-1 (`WarnAboutSharedTechOnUpgrade`, `WarnAboutSharedResearchOnUpgrade`, `WarnAboutSharedProgressFacilityOnUpgrade` — already on per-agency from band-1, **verify mks didn't accidentally duplicate them**).
- Method additions further down (`TryResolveAgencyToken` extensions, `Reset` extensions, new public-API methods for Slice E-1 `TryDeleteAgency`/`TryRenameAgencyOwner` migration helpers).

**Resolution: TAKE-BOTH with per-method precision.** The conflict is at the boundary between `WarnAboutSharedTechOnUpgrade` end and `WarnAboutSharedContractsOnUpgrade` start — both sides added/modified nearby content. Steps:
1. For `WarnAboutSharedContractsOnUpgrade` itself: **keep the per-agency version** (the slice (j) text + counter rename are the correct post-slice-(j) wording).
2. Keep ALL mks-side LoadExistingAgencies extensions (both the early-return suite + main-path additions).
3. Keep ALL mks-side `RefuseStartupIfUpgradeHazardWithoutOverride` hazard predicates.
4. Keep ALL 3 new mks-side Warn methods (`Shared{Kolony,Planetary,Orbital}OnUpgrade`).
5. **Verify `WarnAboutSharedTechOnUpgrade`/`Research`/`ProgressFacility` are NOT duplicated** — band-1 added them on master, so they're already present at merge-base. If mks-side diff includes "+" lines for these, they're a removal-then-re-add artifact and should resolve to the single existing copy.
6. Keep ALL mks-side method additions further down (TryResolveAgencyToken extensions, Reset extensions, TryDeleteAgency, TryRenameAgencyOwner).

### Auto-merged (no manual work, but verify post-merge)

#### 3. `CLAUDE.md` — AUTO-MERGED
mks-side rewrote the Admin Commands inventory + per-agency-career family. Per-agency-side added per-agency-career rows. Take-both worked because git resolved at the hunk level. **Verify post-merge:** the Admin Commands list contains all of `listagencies` / `setagency*` / `transferagency` / `deleteagency` / `setvesselagency`.

#### 4. `LmpCommonTest/SerializationTests.cs` — AUTO-MERGED
mks-side added kolony/planetary/orbital round-trip tests + 3 forward-compat-tail tests. Per-agency-side (slice (j)) likely added an AgencyContractMsgData test if not already present. **Verify post-merge:** LmpCommonTest count == sum of both branches' additions (= 28 mks-side; check if per-agency added any new ones beyond the ones at merge-base).

### Single-sided changes (mks-only, no conflict expected; verify by listing)

These were touched by mks but NOT by per-agency. Auto-merge should leave them at mks-side state:

- `Server/ForkBuildInfo.cs` — mks appended several `[fix:MKS-*]` and `[fix:per-agency-career]` ActiveFixes entries.
- `Server/Command/CommandHandler.cs` — mks added `SetVesselAgencyCommand` registration.
- `Server/Command/Command/SetVesselAgencyCommand.cs` + `SetVesselAgencyCommandParser.cs` — new files.
- `Server/System/Agency/AgencyState.cs` — added KolonyEntries / PlanetaryEntries / OrbitalTransfers dicts + their ToConfigNode/FromConfigNode.
- `Server/System/Agency/AgencySystemSender.cs` — added kolony/planetary/orbital catchup methods + a SendCatchupAfterStateChange overload.
- `Server/System/HandshakeSystem.cs` — added 3 new catchup calls at handshake auth time (after the existing 5.17d contract catchup).
- `Server/System/Vessel/VesselDataUpdater.cs` — added public `GetVesselLock(VesselId)` accessor for SetVesselAgencyCommand.
- `Server/System/Scenario/*` — possible ScenarioSystem.PerAgencyOnlyIgnoreSend extensions for MKS scenarios.
- All new MKS Harmony patches in `LmpClient/Harmony/Mks/`.
- All new MKS MsgData in `LmpCommon/Message/Data/Agency/` (AgencyKolonyEntry, AgencyKolonyStateMsgData, AgencyPlanetary{Entry,StateMsgData}, AgencyOrbital{Entry,StateMsgData}).
- All new MsgData wire-symmetry entries: `AgencySrvMsg.cs` + `AgencyCliMsg.cs` SubTypeDictionary additions.
- New router files: `AgencyKolonyRouter.cs`, `AgencyPlanetaryRouter.cs`, `AgencyOrbitalRouter.cs`.
- All new ServerTest / MockClientTest / LmpClientTest test files for kolony/planetary/orbital/setvesselagency.

### Per-agency-only single-sided changes

These were touched by per-agency but NOT by mks. Auto-merge leaves them at per-agency state:

- `Tools/AdminGui/` — entire Avalonia GUI app (slice 1A through 1H).
- `Server/Message/LockMsgReader.cs` + new `LockCrossAgencyDeniedMsgData.cs` (slice c).
- `LmpClient/Systems/Lock/LockMessageHandler.cs` (slice c).
- `Server/System/Vessel/VesselSyncForceFullSync*.cs` (slice i).
- `LmpClient/Systems/VesselFlightState/VesselRecoveryEventListener.cs` (slice h).
- `LmpClient/Systems/Vessel/VesselRemoveSystem.cs` (slice h's hook).

---

## Direct breakage risks

**R1: Conflict-resolution drops the mks-side LoadExistingAgencies early-return Warn suite.**
The mks-side added 6 Warn calls + RefuseStartup hookup in the early-return branch (when `Universe/Agencies/` doesn't exist). This is the canonical first-flip operator trajectory — without it, an operator who enables PerAgencyCareer for the first time gets silent strip with no warning. Resolution-time check: post-merge, the early-return branch of `LoadExistingAgencies` must call all 8 Warn methods + RefuseStartup.

**R2: Conflict-resolution drops the per-agency-side WarnAboutSharedContractsOnUpgrade text refresh.**
Per-agency-side updated the comment + log message of this method to reflect slice (j)'s real strip behavior. The merged version must keep the new wording, not the merge-base "Stage 5.18a hasn't shipped" wording. Resolution-time check: post-merge, diff `WarnAboutSharedContractsOnUpgrade` against per-agency's version of just that method (`git diff feature/per-agency..HEAD -- Server/System/Agency/AgencySystem.cs` filtered to that method should be empty).

**R3: `using LmpCommon.Message.Data.Agency;` import accidentally dropped during projector resolution.**
The mks-side splices reference `AgencyKolonyEntry` and friends, which live in `LmpCommon.Message.Data.Agency`. If the merge drops the import, the projector won't compile. Resolution-time check: `cd F:/luna-multiplayer && dotnet build Server/Server.csproj -c Release` must succeed after conflict resolution.

**R4: `SharedContractStates` HashSet placement collides with mks-side adjacent code.**
The HashSet is per-agency-only. It needs to land in a location that doesn't fight with mks-side's adjacent additions. Recommended placement: immediately AFTER the keys array (where the per-agency branch placed it), since the mks-side's adjacent additions are in the keys array itself + below in the file body (the splice methods). Resolution-time check: HashSet exists exactly once in the merged file.

**R5: Duplicate `WarnAboutShared{Tech,Research,ProgressFacility}OnUpgrade` methods.**
These already exist at merge-base (band-1 5.17e added them). If the mks-side diff shows "+" lines for them, it's a removal-then-re-add (probably from a refactor that touched whitespace around them). Resolution must collapse to exactly one copy of each. Resolution-time check: `grep -c "private static void WarnAboutSharedTechOnUpgrade" Server/System/Agency/AgencySystem.cs` should return 1.

---

## Indirect breakage risks (semantic conflicts not in textual conflicts)

**R6: Dual-mode silence regression.**
All new splice methods (per-agency + mks) must early-return / no-op when `PerAgencyCareer=false`. The textual conflict resolution doesn't touch this — but if any new splice is called from a path that doesn't gate, gate-off mode breaks. Verify path:
- `AgencyScenarioProjector.Project` is called from `ProjectForClient` which gates on `PerAgencyCareer && !Sandbox`. ✅ (Both branches' new splice methods are called from `Project`, so they inherit the gate.)
- `AgencySystem.WarnAboutShared*OnUpgrade` are all called from `LoadExistingAgencies` which is in the agency-load path; they early-return when `Agencies.Count > 0` or `VesselStoreSystem.CurrentVessels.IsEmpty`. The gate-off path doesn't call `LoadExistingAgencies` at all (it's only invoked when `PerAgencyCareer=true`). ✅

**Test verification: a ServerTest with `PerAgencyCareer=false` ingesting any of the kolony/planetary/orbital ScenarioModule blobs must produce zero MKS-router routing and zero `[fix:MKS-*]` log lines.** Existing `ServerTest.AgencyKolonyRouterTest`/`AgencyOrbitalRouterTest`/etc. have dual-mode-disabled-no-op cases; they should pass post-merge.

**R7: MKS-not-installed regression.**
Per-agency clients without MKS in their KSP install shouldn't see any MKS-specific behavior or wire traffic. Path:
- All MKS Harmony patches self-disable via `MKS == null` check in their `Prepare()` (mks-side convention from Phase 0).
- `ScenarioSystem.PerAgencyOnlyIgnoreSend` filters the 3 MKS scenarios; when those scenarios don't exist in `CurrentScenarios`, the filter is a no-op.
- MKS MsgData types (`AgencyKolonyStateMsgData` etc.) are owner-only echoes; a client without MKS receives them and the routing-to-AgencyState happens client-side regardless of MKS presence (the data is opaque). No KSP-side render path involved.

**Test verification: LmpClientTest `AgencyMembershipDecisionTest` + new MKS-related tests must pass on the merged tree.** No MKS DLL is in `External/KSPLibraries/` in CI, so this is also CI's defense.

**R8: ForkBuildInfo.ActiveFixes order pollution.**
Both sides may have appended entries. The merged list must be in commit-chronological order (matches the convention from `[[stack-notes-fork-build-info]]`). Resolution-time check: post-merge, the list should read:
- ... (merge-base entries through slice g) ...
- per-agency entries: slice h, slice i, slice c, slice j (in their commit chronological order on the per-agency branch)
- mks entries: Phase 0 / 1 / 1.5R0 / 2 / Slice A / B / C / D-1 / D-2 / E-1 / E-2 (in their commit chronological order on the mks branch)

The mks branch was committed BEFORE the per-agency branch's slices in real wall-clock time but they're on diverged branches. The merge commit's history will linearize them. For ActiveFixes, the convention says "commit-chronological order"; given both branches are append-only here, the post-merge list will have BOTH suffixes appended — order between the per-agency cluster and the mks cluster is fine either way (the merge commit's parents determine the canonical order, and `git log --topo-order` will show them).

**Decision: just verify no duplicates and no entries dropped.** Specific ordering between the per-agency cluster and mks cluster is operator-readable cosmetics; not load-bearing.

**R9: HandshakeSystem.HandleHandshakeRequest catchup ordering.**
mks-side added `SendKolonyCatchupTo` / `SendPlanetaryCatchupTo` / `SendOrbitalCatchupTo` calls after the existing 5.17d `SendContractCatchupTo`. Per-agency didn't touch this method. The merged ordering will be: contracts → kolony → planetary → orbital. **This is the mks-side intended order; no change needed.** Verify post-merge: `Server/System/HandshakeSystem.cs` catchup section reads in order: Handshake/State (5.15c), Agency catchup (5.16a), Contract catchup (5.17d), Kolony/Planetary/Orbital catchup (Phase 3 slices B/C/D).

**R10: AgencyState dict-serialization order divergence.**
mks-side added 3 new dicts to AgencyState's ToConfigNode/FromConfigNode (KolonyEntries, PlanetaryEntries, OrbitalTransfers). Per-agency-side did NOT touch AgencyState. The merged AgencyState round-trip stability is determined by mks-side's contract — the existing per-router round-trip tests pin the order. Verify post-merge: AgencyStateTest passes including the new MKS-cohort assertions.

**R11: VesselDataUpdater.GetVesselLock visibility regression.**
mks-side added a public `GetVesselLock(VesselId)` accessor. Per-agency-side did NOT touch VesselDataUpdater. **No conflict** but verify: the per-agency branch may have happened to rename the private Semaphore field or change its key type during 5.18d slice i (force-full-sync-on-reconnect — it touches VesselSyncSystem and VesselSync messaging). Spot-check: post-merge, the private Semaphore is still keyed by VesselId and the public GetVesselLock returns it unchanged.

**R12: Test-fixture compatibility.**
Both branches may have referenced specific fixture filenames. Post-merge, `LmpClientTest` and `MockClientTest` resolve fixtures via `AppContext.BaseDirectory` walk; this is robust. **Risk seam:** if mks-side added a test that requires a specific fixture filename only present on its branch (and not on per-agency's bin/Release output), the merged build might fail to copy it. Test-pass on the merged tree is the validation.

---

## Schema impact

**Wire protocol:** NO BUMP expected.
- mks-side added new MsgData types within the existing `AgencyCliMsg` (channel 21) / `AgencySrvMsg` (channel 22) families using `MaxEntryCount`-prefixed counts with forward-compat-tail Removed-key arrays. This is additive and obeys the wire-extension recipe from `[[reference-agency-wire-extension]]`. Existing 0.31.0 clients receive new MsgData and skip them (factory's unknown-subtype path); existing 0.31.0 servers receive new MsgData from new clients and skip them likewise.
- per-agency-side added one new MsgData (`LockCrossAgencyDeniedMsgData` in slice c) following the same extension pattern.
- Neither side bumped `LmpVersioning` protocol version. Verify post-merge: `LmpCommon/LmpVersioning.cs` still reports `0.31.0`.

**On-disk format (AgencyState ConfigNode):**
- mks-side added 3 new dict serialization sections. AgencyState round-trip from a pre-mks-merge .agency file (no KOLONY/PLANETARY/ORBITAL sections) must read fine and treat the dicts as empty. Verify: `AgencyStateTest.AgencyStateTest_LoadsLegacyFileWithoutMksDicts` if such a test exists; otherwise verify by inspecting `AgencyState.FromConfigNode` for `?.GetValue` null-checks.

**Settings format:**
- Per-agency side may have added `AllowEnablePerAgencyOnExistingUniverse=false` (slice (j) refused-startup gate). The mks-side `RefuseStartupIfUpgradeHazardWithoutOverride` checks this same flag (verified via mks-side method comment). **Both sides use the same flag name** — no conflict expected. Verify: `GameplaySettings.xml` schema after merge has exactly one `AllowEnablePerAgencyOnExistingUniverse` field.

---

## Test plan

### Build verification
1. `dotnet build Server/Server.csproj -c Release` — must be clean (no NEW warnings beyond the documented 29-baseline).
2. `dotnet build LmpClient/LmpClient.csproj -c Release` — must be clean (no NEW warnings beyond the documented 7-baseline).
3. Inspect MSBuild output for any added Server warnings beyond `CA1416 ScreenshotSystem` (24 entries, pre-existing).

### Suite verification
4. `dotnet test ServerTest/ServerTest.csproj -c Release` — expect ≥ 434 + per-agency-only additions since slice g.
5. `dotnet test LmpCommonTest/LmpCommonTest.csproj -c Release` — expect ≥ 28 + per-agency-only additions since slice g.
6. `dotnet test MockClientTest/MockClientTest.csproj -c Release` — expect ≥ 88 + per-agency-only additions since slice g; tolerate the known harness flake but not new failures.
7. `dotnet test LmpClientTest/LmpClientTest.csproj -c Release` — expect ≥ 143 + per-agency-only additions since slice g.

### Spot-test verification (specific tests that exercise the conflict-resolved code paths)
- `ServerTest.AgencyScenarioProjectorTest` — exercises `Project()` switch table; must pass for all 4+ keys.
- `ServerTest.AgencyKolonyRouterTest` / `AgencyPlanetaryRouterTest` / `AgencyOrbitalRouterTest` — exercises the new MKS splices.
- `ServerTest.AgencyContractRouterTest` — exercises slice (j)'s adjacent code.
- `ServerTest.AgencyStateTest` — exercises the new MKS dicts' ConfigNode round-trip.
- `MockClientTest.AgencyContractRoutingTest` — exercises slice (j)'s e2e routing.
- `MockClientTest` Phase 3 e2e tests for kolony/planetary/orbital.

### Manual inspection
- `git log --oneline -1 HEAD~1` should still show `89f9619e feat(gui): brighter theme...`.
- `git log --oneline HEAD` should show the merge commit on top.
- `git show --stat HEAD` should show ~25–30 files changed (mostly mks-side additions + 2 resolved conflicts).
- `grep -c "private static void WarnAboutSharedTechOnUpgrade" Server/System/Agency/AgencySystem.cs` == 1 (R5 check).
- `grep -c "using LmpCommon.Message.Data.Agency" Server/System/Agency/AgencyScenarioProjector.cs` == 1 (R3 check).
- `grep -c "SharedContractStates" Server/System/Agency/AgencyScenarioProjector.cs` ≥ 2 (declaration + at least one use; R4 check).

### Post-merge multi-lens review
Per `[[feedback-review-lens-framing]]` + `[[feedback-integration-logic-review]]`:
- **General lens:** correctness review of the merge resolution itself.
- **Integration-logic 4th lens:** trace 6–10 dual-mode-silence scenarios end-to-end through the merged code (e.g., gate=off + MKS-installed + KSP saves a `KolonizationScenario` blob → does the server route correctly? gate=on + MKS-not-installed + agency mints fresh → does anything regress?). 

Receipt for the merge commit's diff sha1 lands in `.claude/review-receipts/<sha1>.txt` per the require-bug-review hook.

---

## Recovery plan if something goes badly wrong

- The merge is purely in the local worktree until I `git push`. **I do NOT push as part of this work; pushing is a separate operator decision.**
- If the merge commit needs to be redone: `git -C F:/luna-multiplayer reset --hard 89f9619e` (per-agency tip) returns to pre-merge state. Pre-merge tip is also captured in this analysis (89f9619e + 5f596961).
- If a [MUST FIX] from the multi-lens review demands changes: I land them as a separate post-merge commit (`fix(merge): ...`) rather than amending the merge commit, per `[[feedback-breakage-analysis]]` "create new commits, don't amend after hook failure" guidance.

---

## Sign-off

Analysis complete. Ready to run `git merge feature/per-agency-mks`. Resolution plans for the 2 textual conflicts are documented above. Edge cases R1–R12 are seeded for the post-merge multi-lens review.
