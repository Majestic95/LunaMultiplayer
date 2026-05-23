# Path B catch-up baseline-seed — breakage analysis

**Branch:** `feature/per-agency` (on top of v14 `c2e23cc0` — server-side-offload Phase 1+2+3)
**Target release:** `v0.31.0-per-agency-private-15`
**Bug:** Cohort soak report — SCANsat map resets to empty on every reconnect under `PerAgencyCareer=true`.

## Scope

Two files modified + two test files extended + one new MockClientTest + ForkBuildInfo entry + CLAUDE.md Stack Notes:

- `Server/System/Agency/AgencyScanRouter.cs` — new `SeedBaselineIfMissing` + `BuildStrippedBaseline` helpers (internal-visibility). Wired into `TryRoute` after `SaveAgency`.
- `Server/System/Agency/AgencyDMagicRouter.cs` — symmetric helpers for `DMScienceScenario`.
- `Server/ForkBuildInfo.cs` — appended `pathb-catchup-baseline` `ActiveFixes` entry.
- `ServerTest/AgencyScanRouterTest.cs` — +5 cases pinning helpers.
- `ServerTest/AgencyDMagicRouterTest.cs` — +4 cases (symmetric).
- `MockClientTest/AgencyPathBCatchupTest.cs` — **new file** with 3 end-to-end cases.
- `CLAUDE.md` — Stack Notes entry documenting the bug + general "Path B routers must seed baseline" pattern.

## Root cause (short)

Path B router suppresses `CurrentScenarios.AddOrUpdate` at `ScenarioBaseDataUpdater.cs:98-102` under gate=on. No other code path populates `CurrentScenarios["SCANcontroller"]` on a fresh universe (mod scenario, not in `GenerateDefaultScenarios`). Catch-up at `HandshakeSystem.cs:173` iterates `CurrentScenarios.Keys` — finds nothing → sends nothing. Client KSP loads default empty SCANcontroller. Subsequent 30s tick from now-empty client overwrites correct per-agency state on disk via unconditional `agency.Coverage[bodyName] = entry;` upsert.

## Edge cases

1. **Operator pre-seeded baseline** (`Universe/Scenarios/SCANcontroller.txt`) — `GetOrAdd` no-ops, operator file wins. ✓
2. **Pre-gate-off-era accumulated data** — same as (1). The existing `WarnAboutSharedSCANsatOnUpgrade` already warns about stale Body/Vessel children in this case. ✓
3. **Concurrent first-broadcasts from two agencies** — `ConcurrentDictionary.GetOrAdd` factory may run twice but only one result stored. Both baselines are functionally equivalent (root scalars only); race outcome doesn't affect correctness. ✓
4. **Empty inbound** (no Progress/Scanners containers) — strip is robust; baseline equals inbound minus nothing. Projector handles empty Progress containers (creates if missing). ✓
5. **Inbound mutated post-build** — `ConfigNode.ToString` round-trip produces fully-isolated deep clone. Verified by `BuildStrippedBaseline_IsolatedFromInboundTree` test. ✓
6. **BackupScenarios serialization** — already takes `GetSemaphore` per-key; my baseline is a fresh ConfigNode, no pre-existing writer contention. Disk file `Universe/Scenarios/SCANcontroller.txt` carries the stripped baseline only (no per-player progress), which is the correct behaviour going forward. ✓
7. **WarnAboutSharedSCANsatOnUpgrade interaction** — fires only when Agencies.Count==0 AND CurrentScenarios has the key AND Body/Vessel children exist. My baseline has zero Body/Vessel children → never trips the false-positive on a fresh-mint universe. ✓
8. **Hazard predicate `RefuseStartupIfHazardWithoutOverride` at AgencySystem.cs:641-653** — same predicate as (7), same outcome. ✓
9. **DMagic symmetric** — same patterns apply. ✓
10. **Interaction with v14 perf filters (relay-scene/body/cadence)** — completely separate code paths. v14 touches `VesselMsgReader` + `MessageQueuer` relay; my fix touches `Server/System/Agency/Agency*Router` + scenario catch-up. Zero overlap. ✓

## Test plan

- **Unit tests (ServerTest):** +9 cases. SeedBaselineIfMissing happy path, idempotency, strip semantics, isolation property, null-input no-op. Symmetric S2 + S4.
- **E2E (MockClientTest):** +3 cases. SCANsat owner reconnect catch-up, SCANsat cross-agency privacy, DMagic owner reconnect catch-up.
- **Negative verification:** I temporarily disabled `SeedBaselineIfMissing` in the SCANsat router and re-ran the catch-up tests — both SCANsat cases failed (`Catch-up did NOT include a SCANcontroller scenario`). Restoring the call brought them back green. Tests are meaningful, not rubber-stamps.
- **Full ServerTest 812/812 passing** on v14+fix base (v14 baseline 803, +9 from my new tests).
- **Targeted MockClientTest 19/19 passing** (Path B + Projection + Contract + Handshake).

## Risk assessment

**Low.**
- Additive helper functions, no existing behaviour modified except adding one line at end of `TryRoute`.
- `GetOrAdd` is atomic and idempotent.
- Pre-existing operator-seed paths preserved (factory only runs when key absent).
- Dual-mode silence preserved — TryRoute still returns false under gate=off before reaching SeedBaselineIfMissing.
- Negative test confirms the fix is load-bearing for the reported symptom.

## Wire/protocol impact

**None.** Protocol stays at 0.31.0. No client-side change required — operators upgrading from v14 only need the new Server binary; v14 LmpClient.dll works as-is.

## Reviewers

Multi-lens parallel:
- General + server-systems lens — verify GetOrAdd semantics, lock-order, no race against BackupScenarios.
- Consumer lens — verify a future Path B router author (S5/S6/S7+) reading this code learns the pattern correctly.
- Persistence lens — verify the on-disk baseline file written by BackupScenarios is operator-correct (no per-player leak, no stale data).
