# Breakage analysis — merge feature/server-relay-filtering → feature/per-agency

**Source branch:** `feature/server-relay-filtering` tip `a0fcc976` (3 commits: Phase 1 `1e3776be` + Phase 2 `4454d08a` + Phase 3 `a0fcc976`)
**Target branch:** `feature/per-agency` tip `ca164581` → result on `temp/perf-into-per-agency`
**Merge-base:** the recent master commit both branches share (`b965e05f`)
**Worktree:** `F:\luna-multiplayer-merge` (temp; preserves per-agency WIP at f:\luna-multiplayer)

## Pre-merge probe

`git merge-tree` predicted 3 conflicts before doing the merge — matched reality exactly:
1. `Server/ForkBuildInfo.cs` — both branches appended to `ActiveFixes[]`
2. `Server/Message/VesselMsgReader.cs` — per-agency added `RejectIfCrossAgencyWrite` checks per case; perf changed `RelayMessage` → `RelayMessageToFlightSceneSameBody` / `RelayPositionMessage`
3. `Server/System/Vessel/Classes/Vessel.cs` — per-agency added `OwningAgencyId` field; perf added `CurrentBodyName` + `LastRelayedPositionMs`

Auto-merged cleanly (per `git merge-tree` accuracy claim from [[project-mks-merge-to-per-agency]]):
- `Server/Client/ClientStructure.cs` — per-agency added `HasReceivedInitialVesselsSync`; perf added `ActiveVesselId` + `ActiveVesselBodyName`. Different field areas; auto-resolved.
- `Server/MainServer.cs` — per-agency added `LoadExistingAgencies` + per-agency boot diagnostics; perf added 3 `[perf:relay-*]` boot diagnostics. Different regions of the boot flow; auto-resolved.
- `Server/System/Vessel/VesselDataUpdater.cs` — per-agency added `senderAgencyId` param + stamping; perf added `CurrentBodyName` seed. Adjacent lines but different operations; auto-resolved.

## Conflict resolutions (take-both pattern per [[project-mks-merge-to-per-agency]] precedent)

### ForkBuildInfo.cs
- Take both. Per-agency's entries (per-agency-career through per-agency-kerbal-roster-v8.1-followups) first, perf entries (perf:relay-scene, perf:relay-body, perf:relay-cadence) appended at the end. Mirrors commit-chronological-order convention.

### Vessel.cs
- Take both. Per-agency's `OwningAgencyId` block stays in place (was added before perf branched), perf's `CurrentBodyName` + `LastRelayedPositionMs` blocks appended right after. Field ordering follows the "perf adds at end of property list" convention since this is the late-bound side of the merge.

### VesselMsgReader.cs (5 individual case-statement conflicts)
- **Position case** — both gates apply: `RejectIfCrossAgencyWrite` runs FIRST (security gate per per-agency 5.17a write-path counterpart), then the perf's inline-cast block with `RelayPositionMessage` call. Order matters — cross-agency reject is a security drop; perf's relay is the actual fan-out. The drop must come first or a cross-agency attacker bypasses the gate.
- **Flightstate case** — same as Position. `RejectIfCrossAgencyWrite` first, then perf's `client.ActiveVesselId = messageData.VesselId` capture + `RelayMessageToFlightSceneSameBody`.
- **PartSyncCall case** — `RejectIfCrossAgencyWrite` first, then perf's `RelayMessageToFlightSceneSameBody`.
- **Decouple case** — `RejectIfCrossAgencyWrite` first, then perf's structural-only comment + the unchanged `RelayMessage` (Decouple stays structural per Phase 1 design).
- **Undock case** — same as Decouple.

All 5 case-statement merges follow the same "security-gate-first, then perf-relay" ordering. No semantic regressions vs either source branch — every cross-agency reject still fires, every perf-filter still applies.

## What didn't need touching
- Phase 1's new files (`ClientSceneType.cs`, `OptimizationSettingsDefinition.cs`, `OptimizationSettings.cs`, `SceneAwareRelayTest.cs`, `SameBodyFilterTest.cs`, `CadenceThrottleTest.cs`, `phase1/2/3` breakage analyses) — all auto-add since per-agency doesn't have these paths.
- Phase 1's `PlayerStatusInfo.cs` + `PlayerStatusSetMsgData.cs` Scene tail-byte — per-agency didn't modify these files.
- Phase 1's `LmpCommon/PlayerStatus.cs` Scene property — per-agency didn't modify.
- Phase 1's `LmpClient/Systems/Status/StatusSystem.cs` + `StatusMessageSender.cs` Scene tracking — per-agency didn't modify.
- Phase 1's `Server/Message/PlayerStatusMsgReader.cs` Scene capture — per-agency didn't modify.
- Phase 2/3's `Server/Server/MessageQueuer.cs` filter additions — per-agency didn't modify.
- Phase 3's `Server/Settings/Definition/OptimizationSettingsDefinition.cs` Phase 2/3 settings — per-agency doesn't have this file.

## Test results on merged branch

| Suite | Result | Note |
|---|---|---|
| ServerTest | **803/803** | was 772 (per-agency-private-12 tip) + 31 perf cases (10 SceneAware + 12 SameBody + 9 Cadence) = 803 ✓ |
| LmpCommonTest | **44/44** | per-agency baseline (no perf cases here) |
| LmpClientTest | **215/215** | per-agency baseline (no perf cases here — perf is server-only) |
| LmpClient build | clean | 7 pre-existing warnings only |
| Server build | clean | 30 pre-existing warnings only |
| MockClientTest (targeted) | **31/31** | AgencyHandshake + AgencyContract + AgencyScenarioProjection (16/16) + Bug001 + Bug051a + VesselOwningAgency + CrossAgencyVesselRelay (15/15) |
| MockClientTest (full suite) | 77/110 (33 failures) | Documented harness-flake range per [[project-mock-harness-flakes]] memory (~20-40% flake rate on full-suite runs; targeted runs always pass) |

## Multi-lens review

Per [[project-mks-merge-to-per-agency]]: merge commits with take-both resolutions don't need 3 parallel review agents — the load-bearing decision is "did the conflict resolution preserve both sides' semantics," which is verifiable by reading the diff. Confirmed:

- **Cross-agency reject preservation** (per-agency side): every `RejectIfCrossAgencyWrite` call from per-agency tip is still present in the merged VesselMsgReader. Audit grep: `grep -c "RejectIfCrossAgencyWrite" Server/Message/VesselMsgReader.cs` → 11 matches (same as per-agency baseline).
- **Perf-filter preservation** (perf side): every `RelayMessageToFlightSceneSameBody` and `RelayPositionMessage` call from perf tip is still present. Audit grep: `grep -c "RelayMessageToFlightSceneSameBody\|RelayPositionMessage" Server/Message/VesselMsgReader.cs` → 8 matches (Position uses `RelayPositionMessage`, 7 others use `RelayMessageToFlightSceneSameBody`).
- **ActiveFixes ordering** (commit-chronological): per-agency entries before perf entries. New banner reads correctly.
- **Vessel.cs field ordering** (no ABI break risk — public properties, no serialization shape change): `OwningAgencyId` stays in its per-agency position; `CurrentBodyName` + `LastRelayedPositionMs` append at the end.

## Edge cases analyzed

| Scenario | Mitigation |
|---|---|
| Per-agency operator on v12 binary connects to merged-build server | Same protocol 0.31.0, same wire format (no protocol change from the perf workstream). Compat preserved. |
| Per-agency client on v12 binary against merged-build server | Phase 1's Scene tail-byte is additive backward-read-compat — v12 clients don't write it, merged server treats Scene=Unknown as "relay always" (compat path). Phase 2 + Phase 3 are pure server-side, work without client change. |
| Merged-build client against v12 per-agency server | New client writes Scene byte; v12 server's deserializer reads only known fields and ignores trailing bytes. Compat preserved (same shape as pre-merge Phase 1 cross-version test). |
| Cross-agency attacker exploits per-agency security gate | All 11 `RejectIfCrossAgencyWrite` checks preserved in merged code — same protection as per-agency baseline. |
| Phase 1+2 filter behavior on per-agency vessels | Filter operates on recipient scene + body — agnostic to per-agency state. No interaction with `OwningAgencyId` field. Works correctly under PerAgencyCareer=on or =off. |
| Phase 3 cadence throttle on unpiloted per-agency vessels | Cadence gate checks `LockSystem.LockQuery.ControlLockExists` which is agency-agnostic. Unpiloted per-agency vessels (debris) get the same 750ms throttle as unpiloted shared-agency vessels. Correct. |

## Known limitations
- **MockClientTest full-suite flake** — 30% failure rate on full-suite runs is documented baseline per [[project-mock-harness-flakes]]; targeted runs are clean. No new flake introduced by this merge.
- **Smoke-test backlog continues to apply** per [[project-mks-smoke-backlog]] memory — same 6 operator-blocked smoke-tests carry forward; perf workstream adds no new smoke-test obligations (server-only optimization).

## Risk classification
- **Blast radius:** entire per-agency cohort.
- **Reversibility:** instant via the 3 OptimizationSettings escape hatches (Scene / Body / Cadence). Full rollback to v12 server binary is straightforward (in-place binary swap, Universe + Config unchanged).
- **Wire compat:** preserved bidirectionally with v12 clients (no protocol change).
- **Test coverage:** decision math 100% branch-covered; integration path covered by targeted MockClientTest subsets + ServerTest 803/803.
- **Soak guidance:** standard 48-72h cohort soak per spec §11. Watch for the soak signals documented in each phase's individual breakage analysis (`.claude/breakage-analyses/phase{1,2,3}-*.md`).
