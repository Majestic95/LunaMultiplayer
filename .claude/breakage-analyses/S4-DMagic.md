# S4 (DMagic asteroid science + anomaly records per-agency) — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `6fea630a` (docs(mod-compat) re-walk DMagic audit)
**Discipline:** Per [[feedback-breakage-analysis]] — mandatory before non-trivial changes.
**Structural authority:** [docs/mod-compat/dmagic-orbital-science.md](../../docs/mod-compat/dmagic-orbital-science.md) "Re-walked 2026-05-19" + Decisions §A-§E. [docs/mod-compat/implementation-spec.md](../../docs/mod-compat/implementation-spec.md) §S4 (post-re-walk).
**Upstream pin:** `F:/tmp/mks-external/DMagicOrbitalScience` SHA `a4e805b9` ([[reference-mks-external-clones]]).
**Precedents:** S2 (SCANsat, commit `9fddb7fd`) — Path B router + projector splice + D2 catch-up. S4 follows the same shape with two dict fields instead of two-collection-plus-multi-Sensor-nesting.

---

## Scope lock — IS

- New `Server/System/Agency/AgencyDMagicAsteroidEntry.cs` — POCO with 5 fields (`Title` string, `BaseValue` / `SciVal` / `Science` / `Cap` floats) mirroring DMagic's `DMScienceData`.
- New `Server/System/Agency/AgencyDMagicAnomalyEntry.cs` — POCO with `BodyIndex` int, `Name` string, `Latitude` / `Longitude` / `Altitude` doubles mirroring DMagic's `DMAnomalyObject`.
- New `Server/System/Agency/AgencyDMagicRouter.cs` — Path B router with `TryRoute(ClientStructure, ConfigNode)` + internal helpers `UpsertAsteroidScienceEntries` + `UpsertAnomalyEntries`. NO migration helper (no vessel keying). NO cross-agency rejection (no vessel keying).
- Edits to `Server/System/Agency/AgencyState.cs`:
  - `Dictionary<string, AgencyDMagicAsteroidEntry> DMagicAsteroidScience` (Ordinal key on Title)
  - `Dictionary<string, AgencyDMagicAnomalyEntry> DMagicAnomalies` (Ordinal key on `$"{BodyIndex}|{Name}"` composite)
  - Serialize emits `DMAGIC_ASTEROID_SCIENCE` + `DMAGIC_ANOMALIES` child nodes (PascalCase disk shape per S2 convention)
  - Parse handles both with per-entry try/catch + invariant culture on doubles + floats per BUG-013
- Edits to `Server/System/Agency/AgencyScenarioProjector.cs`:
  - `"DMScienceScenario"` added to `CareerScenarios`
  - `case "DMScienceScenario":` in `Project` switch
  - New `SpliceDMagicScienceIntoScenario` method:
    - Strip + splice `Asteroid_Science → DM_Science` children (flat dict iteration; one child per agency entry)
    - Strip + splice `Anomaly_Records → DM_Anomaly_List → DM_Anomaly` nested children. **Group agency entries by BodyIndex first; emit one DM_Anomaly_List per body** with all that body's anomalies as DM_Anomaly children. Field names lowercase per DMagic wire (`title`/`bsv`/`scv`/`sci`/`cap` on asteroid; `Body` int on list wrapper; `Name`/`Lat`/`Lon`/`Alt` on anomaly).
    - Lat/Lon/Alt emit via `ToString("R", InvariantCulture)` per Invariant 9. DMagic stock uses `"N5"` (5 decimal places + culture-sensitive); the projector's stricter `"R"` round-trip is BUG-013 defense. KSP-side OnLoad uses `parse("Lat", (double)0)` which accepts both formats.
- Edits to `Server/System/Scenario/ScenarioBaseDataUpdater.cs`:
  - Add `"DMScienceScenario"` to the dispatch table inside `RawConfigNodeInsertOrUpdate`'s Task.Run body: `if (scenarioModule == "DMScienceScenario" && AgencyDMagicRouter.TryRoute(client, scenario)) return;`. Slots alongside the existing `"SCANcontroller"` dispatch from S2.
- Edits to `Server/System/HandshakeSystem.cs`:
  - Extend the existing S2 `SendScenariosToClient(client, "SCANcontroller")` call site to also include `"DMScienceScenario"`. **Update the stale "Future S3 (FFT) + S4 (DMagic)" comment** from S2's commit — S3 is retired (commit `9404bfae`); only S4 is added.
- Edits to `Server/System/Agency/AgencySystem.cs`:
  - New `WarnAboutSharedDMagicOnUpgrade()` boot diagnostic following the kolony/SCANsat pattern (counts pre-existing Asteroid_Science + Anomaly_Records children + Warning + recovery options).
  - Hazard predicate in `RefuseStartupIfUpgradeHazardWithoutOverride()` so a populated `DMScienceScenario` triggers fail-closed under `AllowEnablePerAgencyOnExistingUniverse=false`.
  - Caller dispatch in both LoadExistingAgencies + the standalone path (mirrors WarnAboutSharedSCANsatOnUpgrade slotting from S2).
- New ServerTest classes:
  - `ServerTest/AgencyDMagicRouterTest.cs` — gate=off / null-client / Sandbox short-circuit + per-asteroid upsert + per-anomaly upsert + per-entry isolation on malformed Title/Name + missing-container no-op (M8 equivalent for both containers).
  - `ServerTest/AgencyDMagicProjectorTest.cs` — strip-then-splice on both containers + nested DM_Anomaly_List emission (group-by-BodyIndex) + empty-agency M9 empty-container retention + null-agency input-unchanged.
  - `ServerTest/AgencyStateDMagicRoundTripTest.cs` — invariant culture on float (asteroid) + double (anomaly) fields under comma-decimal thread culture + per-entry isolation + pre-S4 file forward-compat.
- CLAUDE.md update — Stack Notes entry on the audit-via-prespec catch + ServerTest inventory (+~20 tests) + system inventory (AgencyDMagicRouter + new AgencyState fields).

## Scope lock — IS NOT

- **No client-side Harmony.** Path B (D1) — server-only slice.
- **No dedicated wire.** Mutation rides existing DMScienceScenario broadcasts; catch-up rides the existing D2 helper.
- **No cross-agency rejection.** Asteroid science is Title-keyed (not vessel-keyed); anomaly records are body+name-keyed (not vessel-keyed). No "agency owns this vessel" concept applies. Latest-wins upsert under the 1:1 player↔agency Operating Rule.
- **No transferagency migration.** Same reasoning — entries are not vessel-keyed.
- **No protocol bump.** No new wire types.
- **No FFT slot.** S3 was retired (commit `9404bfae`); the catch-up array stays at two scenario names (SCANcontroller + DMScienceScenario).
- **No DMRecoveryWatcher / DMTransmissionWatcher patching.** Decision §E confirms these are client-side; their mutations flow to the server via the standard DMScienceScenario broadcast which Path B intercepts.

## Files touched

| File | Change | Risk |
|---|---|---|
| `Server/System/Agency/AgencyDMagicAsteroidEntry.cs` | NEW (~30 lines) | Low. Pure POCO. |
| `Server/System/Agency/AgencyDMagicAnomalyEntry.cs` | NEW (~35 lines) | Low. Pure POCO. |
| `Server/System/Agency/AgencyDMagicRouter.cs` | NEW (~180 lines incl. doc) | **Medium-HIGH.** Simpler than S2's router (no cross-agency check, no migration) but the nested-anomaly parse needs care. Per-entry isolation at two levels for anomalies (per-DM_Anomaly_List wrapper + per-DM_Anomaly child) — mirrors the S2 multi-Sensor pattern. |
| `Server/System/Agency/AgencyState.cs` | +~140 lines (2 dict fields + Serialize + Parse for both) | **Medium.** Touches canonical persistence; per-entry try/catch + invariant culture must be uniform with S2 patterns. |
| `Server/System/Agency/AgencyScenarioProjector.cs` | +1 CareerScenarios entry + 1 switch case + ~110-line splice | **HIGH.** New splice with **2-level nested anomaly emission** (group-by-BodyIndex). Per-entry isolation per Invariant 4; whole-scenario fallback per Invariant 5. |
| `Server/System/Agency/AgencySystem.cs` | +~80 lines (WarnAboutSharedDMagicOnUpgrade + hazard predicate + 2 dispatch calls) | Low. Pure boot-time read + log; mirrors WarnAboutSharedSCANsatOnUpgrade from S2 verbatim. |
| `Server/System/Scenario/ScenarioBaseDataUpdater.cs` | +2 lines (additional dispatch case) | Low. Slots alongside the S2 "SCANcontroller" dispatch. |
| `Server/System/HandshakeSystem.cs` | +1 scenario name to the existing call + comment fix | Low. The stale "Future S3 + S4" comment from S2's commit becomes "S2 + S4 shipped" — also clears the technical-debt comment-fix noted in the S3 retirement commit. |
| `ServerTest/AgencyDMagic*Test.cs` (3 new test files) | NEW (~700 lines / ~20-25 cases) | Low. Test code. |
| `CLAUDE.md` | Stack Notes entry + ServerTest inventory + system inventory | Low. |

## Direct breakage risk — S4 itself

### 1. Nested anomaly serialization

The wire shape is 2-level nested: `Anomaly_Records { DM_Anomaly_List { Body=N, DM_Anomaly {...} DM_Anomaly {...} } DM_Anomaly_List { ... } }`. The AgencyState dict is FLAT (composite-keyed). The projector splice must group entries by `BodyIndex` and emit one `DM_Anomaly_List` per group.

**Risk: group-by yields empty wrappers.** If an agency has anomalies on Body 5 and Body 7 but the splice emits only one List wrapper for both, DMagic's OnLoad reads "Body=last-emitted" and assigns ALL anomalies to that body. Mitigation: use `entries.GroupBy(e => e.BodyIndex)` (or a manual dict-of-list) + emit one wrapper per group with the correct `Body` value.

**Test pinning:** the projector test must verify "agency with anomalies on 2 bodies → 2 DM_Anomaly_List wrappers in projected blob, each with correct Body value + correct child anomalies."

### 2. Floating-point round-trip across formats

DMagic's OnSave uses `"N5"` (5 decimal places, culture-sensitive). The projector emits `"R"` + InvariantCulture per Invariant 9. DMagic's OnLoad uses `parse("Lat", (double)0)` which accepts both formats. **Risk: minor precision drift** — N5 truncates beyond 5 decimal places; if an operator's seed scenario has N5-format anomaly coordinates and an agency's accumulated state has full-precision "R" doubles, the projected blob will have higher precision than the operator-seed. DMagic accepts both, so this is fine in practice. **Caveat:** if a future DMagic version becomes strict about "N5"-format input, the projector's "R" output could break. Unlikely; documented in the splice comment.

### 3. Stat aggregation race window for asteroid science

`DMScienceData.Science` is a running accumulator. Two clients reporting science recovery on the same asteroid in the same 30s SHA window would both broadcast updated `sci` values. Path B suppresses the shared-store write; each client's broadcast lands at the router and updates the SENDER's agency state. Under 1:1 player↔agency, no within-agency race. Across agencies, no cross-contamination (each updates only their own state). **Safe by design.**

### 4. Cross-agency anomaly "discovery" semantics

If agency A and agency B both visit Mun monolith on the same tick, both clients' DMScienceScenario broadcasts will include the anomaly. Router accepts each into the sender's agency. Both agencies end up with the anomaly record — that's CORRECT (each independently discovered it). No "first agency wins" semantics needed; the agency that scanned it owns the record on their side, but multi-agency discovery is collaborative not exclusive. Document explicitly.

### 5. Empty-agency state on first connect

Decision §2 (each agency starts at 0% on every body, no shared-baseline overlay) applies the same way for DMagic — a new agency has empty `DMagicAsteroidScience` + empty `DMagicAnomalies`. First projection emits empty containers (M9 retention). DMagic's OnLoad iterates `Asteroid_Science → DM_Science` returning empty; `Anomaly_Records → DM_Anomaly_List` returning empty. Acceptable.

### 6. Path B suppression model

Identical to S2: under gate=on, router returns `true` and caller skips AddOrUpdate. `CurrentScenarios["DMScienceScenario"]` stays at operator-seed baseline forever. Per-agency state accumulates separately. Under gate=off, router returns false and the legacy AddOrUpdate runs unchanged.

### 7. AgencyState lock scope under multi-collection mutation

`AgencyDMagicRouter.TryRoute` mutates both `DMagicAsteroidScience` and `DMagicAnomalies` for the same agency. Holds `AgencySystem.GetAgencyLock(agencyId)` ONCE around the entire batch (both upserts). Same pattern as S2.

### 8. Projector splice lock scope

`SpliceDMagicScienceIntoScenario` reads both collections under one lock + `.ToArray()` snapshot. Same as S2 — snapshots taken inside single critical section so no torn read across the two collections.

### 9. Per-entry isolation at TWO levels for anomalies

A malformed `DM_Anomaly_List` (e.g. unparseable `Body` value) should drop the wrapper + its children but keep sibling lists. A malformed `DM_Anomaly` child (e.g. missing `Name`) should drop only that child, keeping sibling anomalies within the same body. Mirror the S2 multi-Sensor pattern: outer per-wrapper try/catch + inner per-anomaly try/catch.

### 10. CelestialBody index resolution

DMagic's OnLoad uses `anomalyList.parse("Body", (CelestialBody)null)` which KSP's parse method resolves via `FlightGlobals.Bodies[flightGlobalsIndex]`. The fork-side router NEVER resolves CelestialBody — it stores the `int BodyIndex` as-is and emits it as `int` on the wire. Operator running a planet-pack that adds/removes bodies could see anomaly records for indices that no longer exist; DMagic's OnLoad returns null and skips silently. Acceptable; not S4-specific.

## Indirect breakage risk — other code paths

### 1. Backup flow

`AgencyState.Serialize` → `FileHandler.WriteAtomic` unchanged. `BackupSystem.RunBackup` iterates Agencies + SaveAgency per. No new contention.

### 2. Test harness reset

`ServerHarness.ResetPerTestState` clears `AgencySystem.Reset` → `Agencies.Clear` drops all dict entries by value. Implicit cleanup; no harness change needed.

### 3. Universe directory layout

No new subdirectory. DMagic state persists inside the existing `Universe/Agencies/<guid>.txt`. No `Universe.CheckUniverse` change.

### 4. Forward-compat for pre-S4 agency files

A pre-S4 file has no `DMAGIC_ASTEROID_SCIENCE` / `DMAGIC_ANOMALIES` child nodes. `AgencyState.Parse` checks `GetNode(...)?.Value`; null → skip → empty dicts. Same shape as S2's forward-compat.

### 5. Reverse-compat for post-S4 → pre-S4 downgrade

Post-S4 agency file has the new sections. Pre-S4 server's Parse doesn't read them; on next save Serialize drops them. Same as Slice B/C/D + S2 — downgrade loses Phase 3 / mod-compat state. Documented in spec §10.

### 6. CC contract progress against per-agency asteroid science

Asteroid contracts read `DMScienceData.Science` for diminishing-returns checks. Per-agency partitioning means agency A's contract progress is independent from B's — closes the leak the audit listed at failure-mode §1.

### 7. AntimatterManager UI (not S4 — defensive note)

Not affected by S4. DMagic and FFT share no namespace surface.

## Edge cases

1. **Empty agency state on first connect** → empty containers emitted (M9). Acceptable.
2. **Single body with single anomaly** → 1 DM_Anomaly_List wrapper + 1 DM_Anomaly child. Test pinned.
3. **Single body with multiple anomalies** → 1 DM_Anomaly_List + N DM_Anomaly children inside. Test pinned.
4. **Multiple bodies each with anomalies** → N DM_Anomaly_List wrappers + per-body grouped children. Test pinned.
5. **Asteroid Title duplicates** → dict overwrite (latest-wins). Acceptable per kolony precedent.
6. **Malformed DM_Anomaly_List Body value** → drop wrapper + its children, keep siblings.
7. **Malformed DM_Anomaly child Name** → drop child, keep siblings.
8. **Pre-0.31 upgrade-in-place with accumulated asteroid science** → operator gets `WarnAboutSharedDMagicOnUpgrade` Warning + RefuseStartup unless override flag is set.
9. **Gate-off / Sandbox mode** → router returns false, legacy AddOrUpdate runs unchanged.
10. **Comma-decimal server locale** → router/state/projector all force InvariantCulture. Round-trip preserves Lat/Lon/Alt without comma corruption.
11. **N5-truncated → R-precision drift** → DMagic accepts both formats. No regression.

## Migration / upgrade hazard surface

New `WarnAboutSharedDMagicOnUpgrade` joins the existing diagnostic family (kolony / planetary / orbital / contracts / SCANsat). Same shape:
- Boot-time check: Agencies.Count == 0 + non-pristine universe + `DMScienceScenario` in CurrentScenarios + non-empty Asteroid_Science or Anomaly_Records children.
- If hazard detected: Warning with recovery options (accept loss + override flag, fresh-start workflow, stay on shared-agency).
- `RefuseStartupIfUpgradeHazardWithoutOverride` adds the same hazard predicate.

## Rollback plan

If post-ship the per-agency DMagic behavior turns out wrong:
- Revert the projector entry (delete from CareerScenarios + remove switch case) → router still accumulates state but it's never read; clients see shared-baseline.
- Per-agency files retain DMAGIC_* sections; data preserved for re-enabled future build.
- Single-commit revert sufficient.

## Test plan summary

| Test class | Cases | Pins |
|---|---|---|
| `AgencyDMagicRouterTest` | ~6-8 | gate-off / null-client / Sandbox short-circuit; per-asteroid upsert; per-anomaly upsert (nested parse); per-entry isolation at both levels; missing-container no-op; per-batch lock held once |
| `AgencyDMagicProjectorTest` | ~6-8 | strip + splice for Asteroid_Science + Anomaly_Records; multi-body nested anomaly emission (group-by-BodyIndex); empty agency → empty containers (M9); whole-scenario parse fallback; null-agency input-unchanged |
| `AgencyStateDMagicRoundTripTest` | ~6 | Asteroid + Anomaly serialize/parse round-trip; invariant culture on all floats + doubles under de-DE thread culture (BUG-013); per-entry parse-failure isolation at both nesting levels; pre-S4 file forward-compat |

**MockClientTest** — NOT added in this slice (matches S2 decision — DMScienceScenario blobs are awkward to synthesize in mock-client; server-only ServerTest coverage sufficient for v1).

## Risk classification summary

- **HIGH risk areas:** `SpliceDMagicScienceIntoScenario` (new projector splice with **nested anomaly emit + group-by-BodyIndex**). The group-by-emit-correctly pattern is the load-bearing logic; tests pin multi-body emission.
- **MEDIUM risk:** `AgencyDMagicRouter.TryRoute` (new router with two-collection mutation + nested anomaly parse); `AgencyState.cs` Serialize/Parse extension (must honor invariant culture).
- **LOW risk:** new POCO files (2); ingress dispatch (1-line addition); D2 catch-up call site (1-line addition); upgrade-lens diagnostic (mirrors SCANsat verbatim); CLAUDE.md updates.

No protocol bump. No `IgnoredScenarios` change. No client-side code. No new wire messages. No vessel-keyed migration (entries are Title-keyed + BodyIndex+Name-keyed).
