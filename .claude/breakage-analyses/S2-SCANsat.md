# S2 (SCANsat per-agency coverage) — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `5a0171fb` (docs(mod-compat) re-walk cleanup; doc-only ancestor `2efe5f85` carries the verified structural spec)
**Discipline:** Per [[feedback-breakage-analysis]] + the audit-via-prespec recipe step 5. Mandatory before this non-trivial commit.
**Structural authority:** [docs/mod-compat/SCANsat.md](../../docs/mod-compat/SCANsat.md) "Re-walked 2026-05-19" subsection + Decisions §1-§9. [docs/mod-compat/implementation-spec.md](../../docs/mod-compat/implementation-spec.md) §S2 (post-re-walk).
**Upstream pin:** `F:/tmp/mks-external/SCANsat` SHA `0d67371` ([[reference-mks-external-clones]]).

---

## Scope lock — IS

- New `Server/System/Agency/AgencyCoverageBodyEntry.cs` — POCO with 11 fields mirroring SCANsat `Progress → Body` (incl. nullable `ClampHeight` + `LandingTarget`).
- New `Server/System/Agency/AgencyScannerSensorRecord.cs` — nested POCO mirroring `Sensor` children inside a Vessel.
- New `Server/System/Agency/AgencyScannerEntry.cs` — POCO with `VesselId` / `VesselName` / `List<AgencyScannerSensorRecord>` mirroring `Scanners → Vessel`.
- Edits to `Server/System/Agency/AgencyState.cs`:
  - `Dictionary<string, AgencyCoverageBodyEntry> Coverage` (Ordinal key)
  - `Dictionary<Guid, AgencyScannerEntry> Scanners`
  - Serialize sections for both (`SCAN_COVERAGE` + `SCAN_SCANNERS` child nodes) mirroring `KOLONY_ENTRIES`
  - Parse sections for both with per-entry try/catch + `LunaLog.Warning("[fix:S2-SCANsat] ...")` on malformed entries
- New `Server/System/Agency/AgencyScanRouter.cs` — Path B router with `TryRoute(ClientStructure, ConfigNode)` + `MigrateForVesselTransfer(source, dest, movedVesselId)`.
- Edit `Server/System/Agency/AgencyScenarioProjector.cs`:
  - `"SCANcontroller"` added to `CareerScenarios`
  - `case "SCANcontroller": return SpliceSCANsatCoverageIntoScenario(serializedText, targetAgency);` in `Project` switch
  - New `SpliceSCANsatCoverageIntoScenario` method mirroring `SpliceAgencyKolonyEntries` (strip+splice Progress + Scanners; leave SCANResources + UI scalars alone)
- Edit `Server/System/Scenario/ScenarioBaseDataUpdater.cs`:
  - Add `ClientStructure client` parameter to `RawConfigNodeInsertOrUpdate` (chose extension-not-overload — see Risk §1.1)
  - Inside `Task.Run`, dispatch to `AgencyScanRouter.TryRoute` for `"SCANcontroller"` scenario; on `true`, return before `AddOrUpdate`.
- New helper in `Server/System/ScenarioSystem.cs` — `SendScenariosToClient(ClientStructure, params string[] scenarioNames)` (D2 catch-up).
- Edit `Server/System/HandshakeSystem.cs` — call `SendScenariosToClient(client, "SCANcontroller")` after the existing Path A catch-up block (per D2). S3/S4 names omitted from this commit; their slices will add their own.
- Edit `Server/Command/Command/SetVesselAgencyCommand.cs` — add `AgencyScanRouter.MigrateForVesselTransfer` call alongside the existing kolony + orbital migration calls, plus a Scanners-side `[fix:S2-SCANsat-Mig]` log line.
- New `ServerTest/AgencyScanRouterTest.cs` (~8-10 cases)
- New `ServerTest/AgencyScenarioProjectorSCANsatTest.cs` (~6-8 cases)
- New `ServerTest/AgencyStateSCANsatRoundTripTest.cs` (~6-8 cases)
- New `ServerTest/AgencyTransferAgencySCANsatMigrationTest.cs` (~3-4 cases)
- Edit CLAUDE.md — add S2 entry to ServerTest inventory + Stack Notes entry on the multi-Sensor finding + the AgencyState fields list.

## Scope lock — IS NOT

- **No client-side Harmony.** Path B (D1) — no `LmpClient` changes in this slice. Existing 30s SHA broadcast already triggers the inbound; existing periodic `SendScenarioModules` emits the projected outbound to clients.
- **No dedicated `AgencyScanMsgData` wire.** Path B explicitly omits per-slice wire surface; mutation rides the existing `SCANcontroller` scenario channel; catchup rides the new D2 helper.
- **No `IgnoredScenarios.IgnoreSend` entry.** Path B explicitly leaves the broadcast in place; the router suppresses the shared-store WRITE inside the ingest.
- **No owner-only echo.** Path B does not echo on per-batch success; clients converge via the projected scenario at next SendScenarioModules tick (or via D2 catch-up on handshake).
- **No protocol bump.** Wire surface unchanged; the catchup helper rides the existing scenario-data channel.
- **No `SCANResources` partition.** Decision §6 — shared, untouched by router + projector.
- **No root-level KSPField UI scalar partition.** Decision §7 — shared baseline frozen at operator seed under gate=on; router suppresses shared-store writes so each client's runtime UI tweaks stay LOCAL only.
- **No FFT / DMagic work.** S3 + S4 are mechanical copies; tracked separately. The D2 helper signature already accepts `params string[]` so adding `"FarFutureTechnologyPersistence"` / `"DMScienceScenario"` to the call site is a 1-line edit in those slices.
- **No LunaCompat sidecar work.** Operator-policy note added to README.md as documented in the spec, but the actual Luna Compat config changes are operator-side, not in this fork.
- **No `transferagency` rewiring.** Slice E-2 (`SetVesselAgencyCommand`) already exists; this slice only ADDS a single Scanners migration call alongside the existing kolony/orbital/planetary calls.

## Files touched

| File | Change | Risk |
|---|---|---|
| `Server/System/Agency/AgencyCoverageBodyEntry.cs` | NEW (~50 lines) | Low. Pure POCO. |
| `Server/System/Agency/AgencyScannerSensorRecord.cs` | NEW (~30 lines) | Low. Pure POCO. |
| `Server/System/Agency/AgencyScannerEntry.cs` | NEW (~30 lines) | Low. Pure POCO with `List<>` reference. |
| `Server/System/Agency/AgencyState.cs` | +120 lines (2 dict fields + ~50 lines Serialize + ~60 lines Parse) | **Medium.** Touches the canonical persistence path; per-entry try/catch + invariant-culture per BUG-013 precedent must be uniform. |
| `Server/System/Agency/AgencyScanRouter.cs` | NEW (~280 lines incl. doc) | **HIGH.** New router; per-entry isolation + cross-agency rejection + vessel-not-in-store drop + multi-Sensor nested upsert; mirrors `AgencyKolonyRouter` shape. |
| `Server/System/Agency/AgencyScenarioProjector.cs` | +1 CareerScenarios entry + 1 switch case + ~100-line splice method | **HIGH.** New splice; strip-shared/keep-foreign for Progress + Scanners + UI scalars + SCANResources untouched; per-entry isolation per Invariant 4 + whole-scenario fallback per Invariant 5. |
| `Server/System/Scenario/ScenarioBaseDataUpdater.cs` | Signature extension on `RawConfigNodeInsertOrUpdate` + 2-line dispatch | **Medium.** Touches every call site of `RawConfigNodeInsertOrUpdate`. Grep + edit each. |
| `Server/System/ScenarioSystem.cs` | New `SendScenariosToClient` helper (~30 lines) | Low. Same emit shape as `SendScenarioModules` per-name loop. |
| `Server/System/HandshakeSystem.cs` | +1 line for catch-up call site | Low. Existing handshake catch-up section. |
| `Server/Command/Command/SetVesselAgencyCommand.cs` | +1 call to `AgencyScanRouter.MigrateForVesselTransfer` + log line | Low. Parallels existing kolony/orbital migration calls. |
| `ServerTest/Agency*Test.cs` (4 new test files) | NEW (~25-30 test cases total) | Low. Test code. |
| `CLAUDE.md` | Stack Notes entry + ServerTest inventory update | Low. |

## Direct breakage risk — S2 itself

### 1. `RawConfigNodeInsertOrUpdate` signature extension

The current signature is `public static void RawConfigNodeInsertOrUpdate(string scenarioModule, string scenarioAsConfigNode)`. The router needs `ClientStructure` for sender-agency derivation.

**Decision: extend the signature** (`+ ClientStructure client`) rather than overload. Reasoning:

- Overloading produces two callers with subtly different gate-off semantics — the bare-signature version would never invoke router-side suppression, which is fine for now but invites a future bug where someone adds a new caller using the bare signature and silently bypasses per-agency routing.
- Caller-site count is small (verify pre-implementation; `grep "RawConfigNodeInsertOrUpdate"`). Threading the client through is mechanical.

**Risk:** missed call site → compile fails (caught at build). **No runtime risk.**

**Resolution:** grep every call site, thread the `client` through. Where the caller doesn't have a client context (server-boot scenario loads, internal storage migrations), pass `null` — the router will short-circuit to `false` on `null` client.

### 2. Path B suppression model under gate=on

Under gate=on, router returns `true` and caller skips `CurrentScenarios.AddOrUpdate("SCANcontroller", ...)`. The server's `CurrentScenarios["SCANcontroller"]` therefore stays AT THE OPERATOR-SEED BASELINE forever:

- Pre-S2 universe: baseline is whatever the operator's pre-0.31 universe had (potentially accumulated shared coverage).
- Fresh-start universe: baseline is the empty `SCANcontroller` scenario KSP auto-creates on first save.

**Per-agency state accumulates separately in `AgencyState.Coverage` / `Scanners` — that's the authoritative store.** Projector strips Progress/Scanners children from the baseline and splices in per-agency entries. UI scalars + SCANResources pass through unchanged from the baseline. Decision §2 (each agency starts at 0%) is honoured because `AgencyState.Coverage` starts empty for each new agency.

**Risk:** operator-seed baseline has a partial UI scalar set (e.g. operator started with `bigMapVisible=true`); under gate=on this gets locked in for all clients. **Acceptable** — Decision §7. If an operator wants to clean the baseline, they edit `Universe/Scenarios/SCANcontroller.cfg` between sessions.

### 3. `Map` blob round-trip integrity

`Map` is opaque Base64-CLZF2-BinaryFormatter with URL-safe substitution (`/`→`-`, `=`→`_`). The fork-side router/state never decodes it. Round-trip path:

```
client OnSave → AddValue("Map", body_scan.shortSerialize())
  → SCANcontroller scenario blob → server RawConfigNodeInsertOrUpdate
  → AgencyScanRouter.TryRoute → AgencyState.Coverage[BodyName].Map = string
  → AgencyState.Serialize → ConfigNode value
  → FileHandler.WriteAtomic → disk
  → AgencyState.Parse → string
  → Projector splice → ConfigNode value in projected blob
  → SendScenarioModules → client OnLoad → SCANdata.shortDeserialize(map_string)
```

**Risk: KSP-side `ConfigNode.AddValue` escapes special characters.** The Base64 alphabet (`A-Za-z0-9-_`) contains no special characters; the URL-safe substitution explicitly uses `-` and `_` which need no escaping. **Verified safe** against `ConfigNode` value-escaping rules (no `{`, `}`, `=`, `\n` in valid Base64-URL-safe payloads).

**Risk: very large Map blobs (~50KB+) hit a length limit somewhere.** ConfigNode values are arbitrary-length strings; `LunaConfigNode.CfgNode.ConfigNode.ToString()` writes them directly. The Lidgren NetBuffer for scenario broadcasts compresses via QuickLZ + supports MB-sized payloads. **No concerns under normal SCANsat usage** (60° resolution scans = ~10KB Base64 per body; full 1° resolution = ~50KB; either is fine).

### 4. Multi-Sensor nested serialize/parse

`AgencyScannerEntry.Sensors` is `List<AgencyScannerSensorRecord>`. Serialize: emit a `VESSEL` child node per entry, then a `SENSOR` child node per sensor inside. Parse: read VESSEL nodes, for each VESSEL read its SENSOR children, populate `Sensors` list.

**Risk: parse-time per-sensor isolation.** A malformed Sensor child should drop ONLY that sensor, not the whole Vessel record. Mirror per-entry isolation pattern recursively — outer per-Vessel try/catch, inner per-Sensor try/catch. Document in code.

**Risk: empty Sensors list.** A Vessel with zero sensors is technically valid SCANsat state during a brief window. Serialize emits a VESSEL node with no SENSOR children. Parse handles via `foreach` returning empty. Splice emits a Vessel node with no Sensor children — SCANsat's OnLoad iterates GetNodes("Sensor") returning empty. **No issue.**

### 5. Cross-agency Vessel claim semantics

Per Invariant 8: cross-agency-claim logs Warning. Behaviour per Decision §3: only the vessel's owning agency may upsert a `Scanners → Vessel` record for that vessel.

```csharp
// Pseudocode inside AgencyScanRouter.TryRoute per-Vessel loop:
if (!VesselStoreSystem.CurrentVessels.TryGetValue(vesselGuid, out var v)) {
    LunaLog.Debug("[fix:S2-SCANsat] vessel-not-in-store drop");
    continue;
}
if (v.OwningAgencyId != Guid.Empty && v.OwningAgencyId != agencyId) {
    LunaLog.Warning("[fix:S2-SCANsat] cross-agency vessel claim rejected");
    continue;
}
// upsert sensor list...
```

**Vessel-not-in-store: DROP not REJECT** — matches kolony router precedent. Rationale: same as kolony, the projector reads from authoritative `VesselStoreSystem` so a not-in-store entry would never project anyway; pragmatic drop avoids logging noise.

**Risk: Unassigned-sentinel vessel (Guid.Empty OwningAgencyId).** Decision: ANY agency may submit a Scanners record for an Unassigned vessel (matches spec §10 Q3, pre-0.31 upgrade path). Same bypass as kolony router.

### 6. Coverage entry has no vessel-id key, so no vessel-not-in-store check applies

The `AgencyCoverageBodyEntry` is body-keyed (string). There's no per-body ownership concept — every agency tracks coverage of every body independently. Router's body loop has only the malformed-entry try/catch + the latest-wins upsert; no cross-agency check. **Correct semantics per Decision §1 / §2.**

### 7. AgencyState lock scope under multi-collection mutation

`AgencyScanRouter.TryRoute` mutates BOTH `Coverage` and `Scanners` for the same agency. Per the AgencyState concurrency contract, both reads and writes hold `AgencySystem.GetAgencyLock(agencyId)`. The router acquires this lock ONCE around the entire batch (Coverage + Scanners), mirroring `AgencyKolonyRouter` shape. **Safe.**

### 8. Projector splice scope under reads of multiple collections

`SpliceSCANsatCoverageIntoScenario` reads `targetAgency.Coverage` + `targetAgency.Scanners` under per-agency lock + `.ToArray()` snapshot each, then iterates outside the lock. Same pattern as `SpliceAgencyKolonyEntries` (single collection) but doubled. **Safe** — both snapshots are taken inside the same critical section, so no torn read across the two collections.

### 9. ScenarioSystem.SendScenariosToClient (D2) emit path

The helper needs to emit a per-scenario payload to a single client. Look at the existing `SendScenarioModules` body for the emit shape — uses `ScenarioStoreSystem.GetScenarioInConfigNodeFormat` to produce the bare-key-value text, then `ProjectForClient` to per-agency-filter it, then ships as a `ScenarioDataMsgData` via `MessageQueuer`.

**Risk: emit path doesn't match `SendScenarioModules`** — silent client-side parse failure. Mitigation: read `SendScenarioModules` verbatim and reuse the same emit shape in a per-scenario loop.

### 10. transferagency Scanners migration

`AgencyScanRouter.MigrateForVesselTransfer(source, dest, movedVesselId)` mirrors `AgencyKolonyRouter.MigrateForVesselTransfer`:
- Lookup `source.Scanners[movedVesselId]`; if absent, return empty result.
- Remove from source, add to destination (overwrite any pre-existing collision per kolony precedent).
- Dual-lock caller contract (per Slice E-1) — `SetVesselAgencyCommand` acquires both locks in Guid.CompareTo order before calling.

**Risk: destination collision.** Same as kolony — by construction a vessel belongs to one agency at a time so destination can't legitimately hold a collision; defensively, source-wins (warning logged with `[fix:S2-SCANsat-Mig]`). Document in helper XML.

**Risk: missing entry on source.** A `transferagency` call for a vessel that has no scanner record is fine — empty result, no migration. Test pinned.

## Indirect breakage risk — other code paths

### 1. Backup flow

`AgencyState.Serialize` → `FileHandler.WriteAtomic` is unchanged. `BackupSystem.RunBackup` calls `AgencySystem.SaveAllAgencies` which iterates `Agencies` + calls `SaveAgency` per — all under per-agency lock. **No new contention** (Coverage + Scanners are added FIELDS, not new locks).

### 2. Test harness reset

`ServerHarness.ResetPerTestState` (`MockClientTest`) currently clears `AgencySystem.Reset` which calls `Agencies.Clear` — that drops the dict entries which carry Coverage + Scanners by value. **Implicit cleanup; no harness change needed.**

### 3. Universe directory layout

No new subdirectory. Coverage + Scanners persist inside the existing per-agency file (`Universe/Agencies/<guid>.txt`). **No `Universe.CheckUniverse` change needed.**

### 4. Forward-compat for pre-S2 agency files

`AgencyState.Parse` for a pre-S2 file (no `SCAN_COVERAGE` / `SCAN_SCANNERS` child nodes): the new parse sections check `node.GetNode("SCAN_COVERAGE")?.Value`; null → skip → resulting `state.Coverage` is empty. Same for Scanners. **Forward-compat verified** — pre-S2 agency files load cleanly into S2 server.

### 5. Reverse-compat for post-S2 → pre-S2 downgrade

A post-S2 agency file has `SCAN_COVERAGE` / `SCAN_SCANNERS` child nodes. A pre-S2 server's `AgencyState.Parse` doesn't read those nodes; they're silently ignored. On next save, the pre-S2 server's Serialize doesn't emit them; data is lost. **Acceptable** — same shape as Slice B/C/D forward-compat: downgrade loses Phase 3 state. Documented in spec §10.

### 6. CC contract progress against per-agency Coverage

The "cross-agency contract progress leak" listed in the SCANsat.md failure-modes list (§1) closes when this slice ships. CC contracts that read `SCANcontroller.body_data[body].getCoveragePercentage()` will read agency A's coverage on agency A's client, agency B's coverage on B's client — because each client receives a different `SCANcontroller` blob projected for their agency. **Closes by design.**

### 7. 5.18b vessel-ownership client mirror

5.18b's `AgencyMembership.VesselOwnership` registry is populated from VesselSync replies (authoritative) + 5.18c `AgencyVisibilityMsgData` broadcasts. S2 does NOT touch the registry — Scanners records reference `VesselId` for routing, but the projector's per-vessel filtering reads `VesselStoreSystem.CurrentVessels[guid].OwningAgencyId` server-side. **No coupling.**

### 8. transferagency lock release

After SetVesselAgencyCommand mutates `vessel.OwningAgencyId`, source's player's locks on the moved vessel become cross-agency-stale. Existing 5.17a `ReleasePlayerLocks` filtered-by-vessel-id handles this. S2's Scanners migration does not add new lock-release concerns. **Existing flow unchanged.**

### 9. Background SCANsat scanning behavior (client-side)

Background scanning continues unchanged. Client's SCANsat plugin polls `SCANdata.updateCoverage()` per FixedUpdate; on next 30s SHA tick, the updated SCANcontroller blob ships to the server. Server-side: router picks up the change and persists to agency state. **No behavior change on the client.**

## Edge cases

1. **Empty Progress + empty Scanners + populated SCANResources + populated UI scalars at server seed** → projector emits empty Progress {} and Scanners {} containers (M9); SCANResources + UI scalars pass through. Test pinned.

2. **Body with all-null optionals** (no ClampHeight, no LandingTarget) → serialize emits no field for nulls; parse yields null for missing fields. Round-trip test pinned.

3. **Vessel with zero sensors** → AgencyScannerEntry.Sensors = empty List. Serialize emits VESSEL node with no SENSOR children. Splice emits a Vessel node with no Sensor children. SCANsat OnLoad's `foreach (var node_sensor in node_vessel.GetNodes("Sensor"))` iterates zero times. Acceptable; documented.

4. **Vessel with 5+ sensors** (theoretical max — heavy science ship) → all sensors round-trip. Multi-Sensor test case covers N=2; mention in comment that the loop scales arbitrarily.

5. **Body with ~50KB Map string** → serialize keeps as string; ConfigNode + Lidgren wire handle it fine (existing precedent: contract Base64 blobs already ride the same path at 5-50KB per contract).

6. **Two clients submit conflicting Vessel records for the same VesselId** → 5.17a cross-agency lock guard prevents concurrent claim by construction. Within an agency, latest-wins (1:1 player:agency rule).

7. **Cross-agency Vessel claim from gate-on client** (e.g. desync between agency stamp and broadcast) → router rejects with Warning; sender's `Scanners` upsert silently fails for THAT vessel; other entries in same blob unaffected. Test pinned.

8. **Vessel-not-in-store claim** → DROP at router (Debug log). Acceptable.

9. **Gate-off** → router returns false early; AddOrUpdate runs as normal; legacy shared behavior preserved. Test pinned.

10. **Sandbox mode** → `AgencySystem.PerAgencyEnabled = false` (career-mode gate); router returns false. Test pinned implicitly via gate-off test.

11. **Pre-0.31 upgrade-in-place with accumulated shared coverage** → server-seed scenario carries old Progress + Scanners children at gate=on first boot. First projection: splice strips ALL Progress/Scanners children (each agency's `AgencyState.Coverage` starts empty) → each agency sees empty Progress + Scanners. SCANResources + UI scalars survive (operator config). Decision §2 honored.

12. **Operator manually clears `Universe/Agencies/<id>.txt` mid-run** → AgencyState reloads empty on next file read (or stays as in-memory copy until restart). No special handling needed.

13. **transferagency with vessel that has zero scanner records** → MigrateForVesselTransfer returns empty result; no migration. Acceptable.

14. **transferagency dual-source-and-destination Scanners collision** → source-wins, Warning logged via `[fix:S2-SCANsat-Mig]`. Test pinned.

15. **HandshakeSystem catch-up to client that just registered** — catch-up sends projected SCANcontroller blob with empty Progress/Scanners (newly-registered agency has empty state). Client's SCANsat OnLoad handles empty containers. **No regression from gate-off.**

16. **HandshakeSystem catch-up after reconnect of long-lived agency** — projector splices accumulated Coverage + Scanners; client's local SCANcontroller state syncs to server snapshot at handshake-complete (no 30s wait per D2). **Improves UX.**

## Migration / upgrade hazard surface

- **No new upgrade-hazard diagnostic required.** The existing `WarnAboutSharedContractsOnUpgrade` (5.17d) + `WarnAboutSharedKolonyOnUpgrade` (Slice B) family already covers shared-baseline-strip-on-first-projection semantics. SCANsat coverage is a subset of the same shape; no operator surprise.

- **README.md operator-policy update** — single new paragraph documenting LunaCompat SCANsat-server-plugin disable rule under gate=on. Not a forced opt-in; soak feedback will tell us if operators hit conflicts. The Decision §5 framing is "fork-side takes precedence; disable LunaCompat's SCANsat entry to avoid double-relay churn."

## Rollback plan

If post-ship the per-agency SCANsat behavior turns out wrong:
- Revert the projector entry (delete from CareerScenarios + remove the switch case) → router still accumulates state but it's never read; clients see the shared-baseline (= pre-S2 behavior on per-agency files, possibly empty).
- Per-agency files retain the SCAN_COVERAGE / SCAN_SCANNERS sections; data is preserved for a re-enabled future build.
- Single-commit revert is sufficient; no migration script needed.

## Test plan summary

**ServerTest pinning** (mirrors kolony + planetary precedent):

| Test class | Cases | Pins |
|---|---|---|
| `AgencyScanRouterTest` | ~8-10 | Per-body upsert; per-vessel upsert with nested sensors; multi-Sensor preservation; cross-agency rejection (Warning); vessel-not-in-store DROP (Debug); empty container no-op (M8); gate-off short-circuit; per-batch lock held once |
| `AgencyScenarioProjectorSCANsatTest` | ~6-8 | Strip + splice for Progress + Scanners; SCANResources untouched; UI scalars untouched; empty agency state → empty containers (M9); whole-scenario parse fallback (Error log + input unchanged); multi-Sensor round-trip through projection |
| `AgencyStateSCANsatRoundTripTest` | ~6-8 | Coverage + Scanners serialize/parse round-trip; invariant culture on all doubles + floats (BUG-013); optional-field round-trip (ClampHeight + LandingTarget null vs populated); Map blob byte-equal preservation; per-entry parse-failure isolation (Body) + per-Sensor parse-failure isolation (nested) |
| `AgencyTransferAgencySCANsatMigrationTest` | ~3-4 | Vessel V's `AgencyScannerEntry` (with 2 sensors) moves A→B; absent from A post-migration; nested sensor list intact at destination; per-body Coverage unchanged on both sides; destination-collision warning logged |

**MockClientTest** — NOT added in this slice. The MockClient harness covers wire-level integration but SCANsat's blob is heavy (a SCANcontroller blob with 50+ scalars + Map blobs is awkward to synthesize in a mock-client). Server-only ServerTest coverage is sufficient for v1; if MockClient adds value for S3 / S4 followups, revisit.

## Risk classification summary

- **HIGH risk areas:** `AgencyScanRouter.TryRoute` (new router with multi-collection mutation), `SpliceSCANsatCoverageIntoScenario` (new projector splice with multi-collection partition). Both mirror well-established kolony precedent + are unit-pinned.
- **MEDIUM risk:** `AgencyState.cs` Serialize/Parse extension (must honour invariant-culture per BUG-013); `ScenarioBaseDataUpdater.RawConfigNodeInsertOrUpdate` signature extension (must thread client through every call site).
- **LOW risk:** new POCO files (3 of them); D2 catch-up helper (mirrors existing per-name dispatch shape); transferagency migration call (single call slotted alongside existing kolony/orbital/planetary migration calls); CLAUDE.md updates.

**No protocol bump.** No `IgnoredScenarios` change. No client-side code. Slice ships server-only.
