# Mod-compat implementation spec — consolidated roadmap

**Status.** Ratified 2026-05-18 against fork `c36d6f97`; **re-ratified 2026-05-19** against fork `c7d8e9f2` (post-MKS merge `d4ff0511` + BUG-038 XML prologue fix + per-agency contract relay v3 hotfix). The post-MKS-rebase addendum (D1/D2/D3 design decisions + M1–M11 mechanical rebases) has been folded into this document; the addendum itself is archived at [archive/implementation-spec-post-mks-rebase.md](archive/implementation-spec-post-mks-rebase.md) for the historical paper trail.

All per-mod audits ([SCANsat](SCANsat.md), [MJ2](MechJeb2.md), [`[x]` Science](x-science-continued.md), [OPM](outer-planets-mod.md), [KER](kerbal-engineer-redux.md), [KAS](kerbal-attachment-system.md), [KIS](kerbal-inventory-system.md), [Trajectories](trajectories.md), [DPAI](docking-port-alignment-indicator.md), [TweakScale](tweakscale.md), [Near Future + FFT](near-future-and-far-future.md), [DMagic](dmagic-orbital-science.md)) are complete. All design questions are resolved. This document consolidates the resulting code slices in implementation order.

**Operating context.** Branch `feature/per-agency`. All slices are gated behind `AgencySystem.PerAgencyEnabled` (the combined `PerAgencyCareer=true && GameMode==Career` check) and produce **zero observable behaviour change** when the gate is off, per the Stage 5 dual-mode contract.

**Citation policy.** References below use the **symbol name** as the primary anchor (e.g. `SpliceAgencyKolonyEntries`), with line numbers as a parenthetical aid where helpful. Line numbers drift on every MKS-adjacent commit; symbol names are stable contracts.

---

## Operating rule (supersedes any prior framing)

**1 player ↔ 1 agency under `PerAgencyCareer=true` (gate=on). Permanent design.**

- Each player owns exactly one agency. Each agency has exactly one player.
- The only "multiple players share state" mode is gate=off (`PerAgencyCareer=false`), which is the legacy LMP shared-scenario behavior.
- Multi-player-per-agency under gate=on is **NOT** being implemented and is **NOT** a roadmap item.
- Stale allusions to "multiple players per agency" in older pre-spec docs (`05a-plaguenz-audit.md`, `mks-lmp-compatibility-phase-2-prespec.md`, `phase-3-prespec.md`) are speculative and do NOT override this rule.

**Implications**:

- **Latest-wins upsert semantics are correct** for every per-agency dictionary (Coverage bitmaps, factory inventory, asteroid science, anomaly records, kolony entries, etc.). Only one client ever writes to a given `AgencyState[X]` slot. **No OR-merge / max-merge / CRDT-style convergence is needed.**
- **Cross-agency isolation is structurally enforced** by sender authority: `AgencySystem.AgencyByPlayerName[client.PlayerName]` is the server-derived agency id; the client's message content does not carry a trusted agency id ([AgencyKolonyRouter.cs](../../Server/System/Agency/AgencyKolonyRouter.cs) top-of-`TryRoute` for the canonical pattern).
- **Reconnect "catch-up" is owner-singular**. There are no teammates awaiting fan-out.

---

## Scope summary

| Slice | Owner | Touches | Size | Depends on |
|-------|-------|---------|------|------------|
| **S1** | Merge-ownership reconciler | Core LMP fork | Server agency + vessel-message ingress | ~60 lines + ServerTest | — |
| **S2** | SCANsat per-agency coverage | Core LMP fork | AgencyState + new router + projector entry + operator-policy doc | ~200 lines + ServerTest + LunaCompat config disable | S1 not required, but builds against the same patterns |
| **S3** | FFT antimatter factory per-agency | Core LMP fork | AgencyState + new router + projector entry | ~140 lines + ServerTest | S2 (copy-paste pattern) |
| **S4** | DMagic asteroid + anomaly per-agency | Core LMP fork | AgencyState + new router + projector entry (two child collections) | ~180 lines + ServerTest | S2 (copy-paste pattern) |
| **S5** | `[x]` Science foreign-agency filter | Luna Compat / sidecar Harmony | `ScienceContext` vessel enumeration | ~15 lines Harmony postfix | None |
| **S6** | KIS re-attach re-stamp | Luna Compat / sidecar Harmony | `KISAddonPickup` attach finalisation | ~10 lines Harmony postfix | None |

**Mods with no work owed:** MechJeb2, Kerbal Engineer Redux, OPM (data-only), Trajectories, DPAI, TweakScale, Near Future suite (non-FFT). Documented in their respective audits.

**Open verification items (not new code, but two-client repros that confirm or unblock):**
- KAS cross-agency couple ownership behaviour against the running fork (informs whether S1 needs to fire at all — but built proactively per the decision).
- KIS `lmpOwningAgency` stamp survival through PART snapshot (informs whether S6 fires in practice — but built proactively per the decision).

---

## Ordering recommendation

Suggested build sequence — but slices are independent enough to parallelise once the pattern is set:

1. **S2 first** (SCANsat). Largest, sets the per-agency-ScenarioModule pattern at full complexity (per-body splice + scanner-node filter + LunaCompat coordination policy). Validates the architecture before replicating it.
2. **S3 + S4 in parallel after S2 lands** (FFT + DMagic). Same pattern; smaller scope each; both copy from S2.
3. **S1 in parallel** with any of the above (no dependency on the per-agency-ScenarioModule pattern; touches the docking-merge ingress instead).
4. **S5 + S6 in parallel** with any core slice (sidecar Harmony work; independent of core LMP).

Estimated build sequence wall-clock if serialised: ~5 review-bounded slices' worth of work. Two of the six (S5, S6) belong in Luna Compat sidecar and can be assigned to whoever owns that adjunct.

---

## Architectural decisions (ratified 2026-05-18)

### D1 — Path B (router-on-ingress) for S2 / S3 / S4

The fork's established per-agency career architecture (**Path A**) has seven components:

1. Client-side Harmony postfix on KSP/mod mutation method (e.g. `KolonizationManager_TrackLogEntryPostfix`)
2. Dedicated wire path (`Agency*MsgData`)
3. Server-side router (`Agency*Router`)
4. Server-side projector splice in `AgencyScenarioProjector.CareerScenarios`
5. `IgnoredScenarios.IgnoreSend` entry to suppress the SHA broadcast
6. Owner-only echo on each router success (`SendKolonyStateToOwner`)
7. Connect-time catch-up (`SendKolonyCatchupTo`)

Every per-agency career system in the fork uses Path A: contracts, tech/science/parts, strategies/achievements/facilities, kolony (Slice B), planetary (Slice C).

The mod-compat slices S2/S3/S4 adopt **Path B** instead, simpler:

1. ~~No client-side Harmony~~ (relies on the existing 30s SHA broadcast for `SCANcontroller` / `FarFutureTechnologyPersistence` / `DMScienceScenario`).
2. ~~No dedicated wire.~~
3. Server-side router intercepting `RawConfigNodeInsertOrUpdate` (instead of a dedicated `MsgReader`).
4. Server-side projector splice — same as Path A.
5. ~~No `IgnoredScenarios` entry~~ (broadcast still fires; router suppresses shared-store write inside the ingest).
6. ~~No owner echo.~~
7. Synchronous catch-up via `ScenarioSystem.SendScenariosToClient` — see **D2**.

**Rationale.** Path B is correct for these three because (a) SCANsat's pixel-scan mutation is internal `SCAN_Data` state without a clean public API to Harmony-postfix — the [SCANsat audit](SCANsat.md) explicitly punts mutation-side hookability to Luna Compat; (b) FFT antimatter factory and DMagic asteroid/anomaly mutations are infrequent enough that the SHA-broadcast bandwidth overhead is negligible; (c) the existing `RawConfigNodeInsertOrUpdate` ingress is mod-agnostic — it works for any third-party `ScenarioModule` without requiring a per-mod Harmony patch surface. Path A remains the canonical choice for stock-career systems and for mods that already expose a usable mutation event (kolony, planetary).

**No correctness concerns** under the 1:1 player↔agency rule above: there is no concurrent-writer race within an agency, so latest-wins on `RawConfigNodeInsertOrUpdate` is correct semantics. Single-slice migration to Path A available as a follow-up if telemetry warrants.

### D2 — Synchronous connect-time catch-up

Under Path B, an owner reconnecting receives per-agency state only on the first `SendScenarioModules` tick post-connect (≤30s under the standard SHA pass cadence). Before that tick, the owner's client briefly observes the shared-scenario baseline (typically empty for a fresh server, or the operator-supplied seed under upgrade-in-place).

**Decision.** Add synchronous connect-time catch-up via a single shared helper in `ScenarioSystem`, called once from `HandshakeSystem.HandleHandshakeRequest` immediately after the existing Path A catch-ups (`SendKolonyCatchupTo` / `SendPlanetaryCatchupTo` / `SendContractCatchupTo`).

```csharp
// Server/System/ScenarioSystem.cs — new helper.
internal static void SendScenariosToClient(ClientStructure client, params string[] scenarioNames)
{
    foreach (var name in scenarioNames)
    {
        if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue(name, out var raw))
            continue;
        // ProjectForClient is a no-op when PerAgencyEnabled=false (preserves gate=off behavior).
        var projected = AgencyScenarioProjector.ProjectForClient(name, raw, client);
        // Emit via the same wire path SendScenarioModules uses for its per-scenario loop.
        // Concrete wiring TBD at implementation time against ScenarioSystem.SendScenarioModules' internals.
    }
}
```

Call site:

```csharp
// Server/System/HandshakeSystem.cs HandleHandshakeRequest, after the existing catch-up block:
ScenarioSystem.SendScenariosToClient(client,
    "SCANcontroller",                  // S2
    "FarFutureTechnologyPersistence",  // S3
    "DMScienceScenario");              // S4
```

**Gate behavior**: `SendScenariosToClient` is safe to call unconditionally. `AgencyScenarioProjector.ProjectForClient` early-returns under gate=off, so under gate=off the helper sends the unmodified shared scenario — the legitimate gate=off behavior anyway.

**Why one shared helper rather than per-slice `Send*CatchupTo` methods?** Under Path B (D1) there is no per-slice wire surface to ship a dedicated method over; the data already flows through the scenario channel. If a slice later migrates to Path A independently, that slice gains its own dedicated catch-up at that time.

### D3 — `transferagency` migration for S2 Scanners (Option A: migrate-with-vessel)

S2's `Scanners` dict is `Dictionary<Guid VesselId, AgencyScannerEntry>` — vessel-keyed. When an admin runs `transferagency` to move a vessel from agency A to agency B (5.18d slice (e)), the MKS Slice E precedent for vessel-keyed entries (see `AgencyKolonyRouter.MigrateVesselToAgency`) is **migrate-with-vessel**: value-field-scan source agency's dict for entries with matching `VesselId`, move to destination agency's dict.

Vessel V transferred A→B: V's scanner records move from `AgencyState[A].Scanners` to `AgencyState[B].Scanners` (overwrite any pre-existing entry there). Per-body `Coverage` does **NOT** migrate (coverage is body-keyed, agency-scoped — A's discoveries of Eve stay A's; B retains B's).

**Slice E implementation note**: extend the existing `transferagency` migration scan loop with a parallel pass on `Scanners`. S3 (single-record FFT factory) and S4 (asteroid-name-keyed + body+name-keyed) have no vessel-keyed entries and do not need migration logic.

---

## Project-wide invariants (apply to every slice below)

These are the Stage 5 contracts every per-agency code path in this fork already honours; the new slices MUST continue to honour them.

1. **Dual-mode silence.** Every public entry point in a new router or projector entry early-returns when `AgencySystem.PerAgencyEnabled` is false. The shared-scenario path runs unchanged.
2. **Career-only.** `PerAgencyEnabled` already enforces `GameMode == Career`. Science / Sandbox / non-Career modes do not engage any new code path.
3. **Per-agency lock acquisition for reads + writes.** Any access to `AgencyState` collections takes `AgencySystem.GetAgencyLock(agencyId)` around the read or mutation (the snapshot-inside-lock pattern in `SpliceAgencyKolonyEntries`).
4. **Per-entry isolation on parse failure.** A malformed stored payload logs and continues the batch — never aborts the whole projection or routing operation. Matches `AgencyContractRouter` Q6 commitment (b).
5. **Whole-scenario parse failure → fall through.** If the outer scenario text fails to parse during projection, log the error at `Error` level and return the input unchanged. The player gets the shared blob rather than a hung handshake. Matches `SpliceAgencyKolonyEntries`'s fallback.
6. **ServerTest pinning per splice.** Every new projector entry gets a unit-style ServerTest that exercises both the splice happy path AND the whole-scenario parse-failure fallback. Mirrors the existing kolony + planetary splice tests.
7. **No new agency state without explicit per-agency-lock contract.** New `AgencyState` fields document the "reads also need the lock" contract on their XML doc-comment, same shape as `AgencyState.KolonyEntries` and `AgencyState.TechNodes`.
8. **Operator-grep logging convention.** Every `LunaLog` line from a new router or splice carries a `[fix:<slice-tag>]` prefix (per the `[fix:MKS-R2]` precedent in `AgencyKolonyRouter`), and follows the Warning-vs-Debug split: **Warning** for cross-agency-claim rejections (operator-visible); **Debug** for hot-path race-window drops like malformed Guid or vessel-not-in-store (operator-invisible by default).
9. **Invariant-culture serialisation.** Every double-valued field on a new entry type round-trips through `ToString("R", CultureInfo.InvariantCulture)` (and parses through `double.Parse(..., CultureInfo.InvariantCulture)`). Pinned by a ServerTest mirroring `AgencyStateTest.Serialize_UsesInvariantCultureForDoubles`. Precedent: BUG-013 — a comma-decimal server locale would otherwise corrupt the on-disk and on-wire formats silently.

---

## Canonical post-MKS reference patterns

These are the reference patterns S2/S3/S4 build against. Cite by **symbol name** in subsequent slices; line numbers drift.

### Strip-then-splice projector entry — `SpliceAgencyKolonyEntries`

`Server/System/Agency/AgencyScenarioProjector.cs::SpliceAgencyKolonyEntries`. The post-MKS canonical splice template. Replaces the older `SpliceAgencyTechIntoResearchAndDevelopment` as the reference pattern.

Three contracts every splice MUST honour:

1. **Find-or-create container.** Handles both "inbound blob has the container with shared children to strip" and "inbound blob has no container at all" (fresh server, empty modlist):

   ```csharp
   var container = node.GetNode("CONTAINER_NAME")?.Value;
   if (container == null)
   {
       container = new ConfigNode("") { Name = "CONTAINER_NAME" };
       node.AddNode(container);
   }
   else
   {
       // .ToArray() snapshots the enumeration so RemoveNode during iteration
       // doesn't invalidate the cursor.
       foreach (var existing in container.GetNodes("CHILD_NAME").ToArray())
           container.RemoveNode(existing.Value);
   }
   // then splice per-agency children
   ```

2. **Snapshot inside the per-agency lock.** Acquire `AgencySystem.GetAgencyLock(targetAgency.AgencyId)`, snapshot the dict values to an array, release the lock before iterating. Iteration of the snapshot happens outside the lock so a slow per-entry serialise doesn't extend the writer-blocking window.

3. **Per-entry try/catch + whole-scenario fallback.** Per-entry serialise wrapped in `try { ... } catch { /* drop this entry, keep others */ }`. Outer ConfigNode parse wrapped in `try { ... } catch { log Error; return scenarioText; }`.

### Router shape — `AgencyKolonyRouter.TryRoute`

`Server/System/Agency/AgencyKolonyRouter.cs::TryRoute`. The post-MKS canonical router shape:

- Single try/catch per entry wrapping classify + lookup + cross-agency check + upsert.
- `accepted` list collected outside the catch.
- `AgencySystem.SaveAgency(agencyId)` once the batch is complete.
- Owner echo via `AgencySystemSender.SendKolonyStateToOwner(client, agencyId, accepted)` — **omitted for Path B** (S2/S3/S4 rely on the existing SHA broadcast + D2 sync catch-up).
- Per-agency lock acquired **once for the entire batch loop**, not per-entry.

Establishes the canonical reference for future reviewers; ensures new routers adopt the post-MKS isolation pattern rather than the older two-step shape.

### Serialize / Parse with per-entry isolation — `AgencyState`

`Server/System/Agency/AgencyState.cs`. The post-MKS canonical persistence shape:

- Serialize: invariant-culture doubles (`ToString("R", CultureInfo.InvariantCulture)`), see the `KolonyEntries` and `PlanetaryEntries` serialisation paths.
- Parse: per-entry try/catch with `LunaLog.Warning("[fix:<tag>] ... skipped entry ...")` on malformed entries (the `[fix:MKS-R2]` review-finding-#3 standard). Older 5.17e-4 parse paths silently skip; this is the upgraded standard.

---

## S1 — Merge-ownership reconciler

**Goal.** When `Part.Couple` collapses two vessels into one (stock docking OR KAS pipe-coupling OR any future mod that triggers the stock coupling pathway), the surviving vessel's `lmpOwningAgency` is reconciled deterministically from the kept side. The non-kept vessel's agency stamp is cleared. Covers both KAS coupling and stock docking in one place.

**New files**

- `Server/System/Agency/AgencyVesselCoupleReconciler.cs`
  - `public static class AgencyVesselCoupleReconciler`
  - `public static void OnVesselCoupled(Guid keptVesselId, Guid mergedVesselId)` — public entry; takes the two pre-merge vessel IDs and resolves the surviving stamp.
  - Lookup logic: `AgencySystem.VesselOwnership.TryGetValue(keptVesselId, ...)` → that agency wins. `mergedVesselId` is unregistered.
  - Both lookups under `AgencySystem.GetAgencyLock` for whichever agency claims `keptVesselId`.
  - Log convention: `[fix:S1-Couple]`. Warning on cross-agency couple reconcile (operator-visible); Debug on intra-agency reconcile (hot path).

**Existing files to modify**

- `Server/Message/VesselMsgReader.cs`
  - At the point where docking events are ingressed (search for `OnVesselDock` / proto coupling handlers — likely `HandleVesselProto` and similar). Add a single call to `AgencyVesselCoupleReconciler.OnVesselCoupled` when KSP reports a couple event.
  - Defer the exact hook line until source-walked at implementation time — this is the most uncertain part of the slice and requires reading the existing vessel-coupling ingress fully. The MKS merge did not change this ingress (verified during the addendum's grounding pass).

**Tests**

- `ServerTest/Agency/AgencyVesselCoupleReconcilerTest.cs`
  - Two agencies; agency A's vessel + agency B's vessel; couple event with A's vessel kept → surviving vessel stamped with A.
  - Same as above with B's vessel kept → surviving stamped with B.
  - Couple with one untracked vessel (no agency stamp on either side) → no exception, no stamp.
  - Couple within the same agency → idempotent.

**Acceptance**

- Cross-agency stock docking: agency A docks to agency B's station. Both clients see the merged vessel owned by whichever side KSP kept. No flicker.
- Cross-agency KAS coupling: identical behaviour (because KAS rides `Part.Couple`).

---

## S2 — SCANsat per-agency coverage

**Goal.** Each agency owns their own `SCANcontroller` state (per-body coverage bitmaps + the `Scanners` vessel-GUID list). Each agency starts at zero coverage on first connect to a fresh server. Operator policy: LunaCompat's SCANsat server plugin entry is disabled when `PerAgencyCareer` is on.

**Architecture: Path B (per D1).** Server-side router intercepts `RawConfigNodeInsertOrUpdate`; no client-side Harmony, no dedicated wire, no `IgnoredScenarios` entry, no owner echo. Synchronous connect-time catch-up via `ScenarioSystem.SendScenariosToClient` (per D2).

**New files**

- `Server/System/Agency/AgencyCoverageBodyEntry.cs`
  - Persistent shape mirrors SCANsat's `Progress` child node: `string BodyName`, `byte[] BodyScan` (the integer-serialised bitmap), `double TerrainConfigMin`, `double TerrainConfigMax`, palette settings, landing-target lat/lon. Serialise via the post-MKS pattern (invariant-culture doubles per **Invariant 9**).
- `Server/System/Agency/AgencyScannerEntry.cs`
  - Persistent shape mirrors SCANsat's `Scanners` child node: `Guid VesselId`, `int SensorType`, `float Fov`, `double MinAlt`, `double MaxAlt`. Indexed by `VesselId`. Invariant-culture doubles.
- `Server/System/Agency/AgencyScanRouter.cs`
  - `public static class AgencyScanRouter`
  - `public static bool TryRoute(ClientStructure client, string scenarioText)` — caller is the `RawConfigNodeInsertOrUpdate` ingress for module name `SCANcontroller`. Returns `true` when the per-agency path handled the inbound; caller suppresses the shared-scenario write.
  - Router shape mirrors `AgencyKolonyRouter.TryRoute` (see "Canonical post-MKS reference patterns" above): single try/catch per entry, batch loop holds the per-agency lock once, no owner echo (Path B).
  - Parses the inbound scenario blob, splits per-body and per-vessel state into the sender's `AgencyState.Coverage` / `AgencyState.Scanners`, persists via `AgencySystem.SaveAgency`.
  - Per-body upsert (latest-wins, correct under 1:1 player↔agency).
  - Per-vessel scanner upsert filtered to the sender's owned vessels only (cross-agency scanner-record claims rejected with `LunaLog.Warning("[fix:S2-SCANsat] ...")`).
  - **Missing-container fallback (M8).** If the inbound blob has no `Progress` or `Scanners` container at all (operator running with an empty SCANsat install state, or pre-first-scan client), treat each missing container as a no-op for that category — NOT as a parse failure. Whole-scenario parse failure (malformed blob) still falls through to the input-unchanged path per Invariant 5.
  - Log convention: `[fix:S2-SCANsat]`. Warning on cross-agency-claim rejection; Debug on race-window drops (malformed Guid, vessel-not-in-store).

**Existing files to modify**

- `Server/System/Agency/AgencyState.cs`
  - Add:
    ```csharp
    /// <summary>
    /// [Mod-compat S2 — SCANsat] Per-agency per-body coverage state. Keyed by
    /// CelestialBody name (Ordinal compare matches SCANsat's stock convention).
    ///
    /// **Concurrency contract** (same shape as <see cref="KolonyEntries"/>):
    /// mutations AND reads MUST hold <see cref="AgencySystem.GetAgencyLock"/>.
    /// Dictionary's non-concurrent enumerator throws (or worse) on a mid-
    /// iteration mutation; the per-agency lock is the only safe enumeration
    /// path.
    /// </summary>
    public Dictionary<string, AgencyCoverageBodyEntry> Coverage { get; }
        = new Dictionary<string, AgencyCoverageBodyEntry>(StringComparer.Ordinal);

    /// <summary>
    /// [Mod-compat S2 — SCANsat] Per-agency per-vessel active-scanner records.
    /// Keyed by vessel GUID. Migrates with vessel under
    /// <c>transferagency</c> (D3 — see migration policy below).
    ///
    /// **Concurrency contract** (same shape as <see cref="KolonyEntries"/>):
    /// mutations AND reads MUST hold <see cref="AgencySystem.GetAgencyLock"/>.
    /// </summary>
    public Dictionary<Guid, AgencyScannerEntry> Scanners { get; }
        = new Dictionary<Guid, AgencyScannerEntry>();
    ```
  - Add Serialize / Parse paths mirroring the post-MKS `KolonyEntries` shape: invariant-culture doubles, per-entry try/catch with `LunaLog.Warning("[fix:S2-SCANsat] skipped malformed entry ...")` on parse failure.
- `Server/System/Agency/AgencyScenarioProjector.cs`
  - Add `"SCANcontroller"` to `CareerScenarios`.
  - Add `case "SCANcontroller": return SpliceSCANsatCoverageIntoScenario(serializedText, targetAgency);` to the `Project` switch.
  - New `SpliceSCANsatCoverageIntoScenario(string, AgencyState)` — round-trip through `ConfigNode`. Build via the canonical strip-then-splice template (see "Canonical post-MKS reference patterns" → `SpliceAgencyKolonyEntries`). Find-or-create both `Progress` and `Scanners` containers; strip shared children; emit per-agency children from `targetAgency.Coverage` and `targetAgency.Scanners`. UI preference scalars (`colours`, `map_*`, etc.) pass through unchanged. Per-entry isolation + whole-scenario parse-failure fallback per Invariants 4 + 5.
  - **Empty-container retention (M9).** After stripping shared children, emit an empty `Progress { }` and empty `Scanners { }` container if the agency has no entries (rather than omitting the containers entirely). Matches stock SCANsat's `SCANcontroller.OnLoad` expectation that the containers exist even when empty. Defensive default — verifiable against [SCANsat upstream](https://github.com/KSPModStewards/SCANsat) at implementation time.
- `Server/System/ScenarioSystem.cs` — new helper per D2:
  ```csharp
  internal static void SendScenariosToClient(ClientStructure client, params string[] scenarioNames);
  ```
- `Server/System/HandshakeSystem.cs::HandleHandshakeRequest` — call site for the helper immediately after the existing Path A catch-up block. Pass `"SCANcontroller"` (this slice) + `"FarFutureTechnologyPersistence"` (S3) + `"DMScienceScenario"` (S4) in a single call so the helper's per-name loop handles them uniformly.
- `Server/System/Scenario/ScenarioDataUpdater.cs` (or whichever method handles `RawConfigNodeInsertOrUpdate` for `SCANcontroller`)
  - Before writing the shared scenario, call `AgencyScanRouter.TryRoute(client, scenarioAsConfigNode)`. If true, suppress the shared-store write.
- **`transferagency` migration (per D3).** Extend the Slice E vessel-migration scan loop with a parallel pass: value-field-scan `AgencyState[A].Scanners` for entries with `VesselId == V`, move to `AgencyState[B].Scanners` (overwrite any pre-existing entry there). Per-body `Coverage` does **NOT** migrate (coverage is body-keyed, agency-scoped). S3 + S4 do not need migration logic — single-record / asteroid-name-keyed / body+name-keyed.

**Operator policy doc update**

- [README.md](README.md) — extend the "Operator policy — modlist uniformity" section with: "Under `PerAgencyCareer=on`, the operator MUST disable LunaCompat's SCANsat server plugin entry in `Universe\LunaCompat\ModSettingsStructure.xml`. The fork's `AgencyScanRouter` takes precedence."
- [SCANsat.md](SCANsat.md) — ensure the operator-config snippet is named.

**Tests**

- `ServerTest/Agency/AgencyScanRouterTest.cs`
  - Two agencies; agency A submits a `SCANcontroller` blob with Eve coverage. Agency B's submitted blob with Duna coverage. Verify each agency's `AgencyState.Coverage` contains only their own bodies after both routes complete.
  - Per-vessel scanner filter: agency A's vessel-GUID scanners present after route; agency B's not.
  - Empty `Progress` / `Scanners` container in inbound → no-op for the missing category (M8).
  - Cross-agency-claim rejection logs `Warning` (M4).
- `ServerTest/Agency/AgencyScenarioProjectorSCANsatTest.cs`
  - Synthesise a shared `SCANcontroller` blob with both agencies' content; project for agency A → output has ONLY agency A's bodies + scanners.
  - Project against an agency with empty `Coverage`/`Scanners` → output has empty `Progress` and empty `Scanners` containers (UI prefs survive). Pins M9.
  - Whole-scenario parse failure → return input unchanged, log `Error`.
- `ServerTest/Agency/AgencyStateSCANsatRoundTripTest.cs`
  - Invariant-culture round-trip on every double-valued field (`TerrainConfigMin`/`Max`, `Fov`, `MinAlt`, `MaxAlt`, landing-target lat/lon). Mirrors `AgencyStateTest.Serialize_UsesInvariantCultureForDoubles` (Invariant 9 / BUG-013 precedent).
  - Per-entry parse-failure isolation: malformed `Progress` child does not abort `Parse`, logs `Warning("[fix:S2-SCANsat] ...")`.
- `ServerTest/Agency/AgencyTransferAgencySCANsatMigrationTest.cs` (D3)
  - Two agencies A + B. Vessel V's `Scanners` record in `AgencyState[A].Scanners`. `transferagency V A→B` → record moves to `AgencyState[B].Scanners`; absent from A; `AgencyState[B].Coverage` and `[A].Coverage` both unchanged.

**Acceptance**

- Two agencies on a `PerAgencyCareer=on` Career server with SCANsat installed.
- Agency A scans 60% of Eve; agency B's Eve coverage stays at 0%.
- Agency A accepts the SCANsat CC contract "scan 80% of Duna"; agency B accepts the same. They each progress independently against their own coverage. **Closes the cross-agency contract progress leak documented in [SCANsat.md](SCANsat.md).**
- **Connect-time delivery.** Synchronous catch-up via `SendScenariosToClient` from `HandleHandshakeRequest` (D2). Owner receives projected per-agency state at handshake-complete; no 30s window of shared-baseline observation.
- Gate=off (or non-Career game mode): SCANsat behaviour is identical to stock Luna MP.

---

## S3 — FFT antimatter factory per-agency

**Goal.** Each agency owns its own Far Future Technologies antimatter factory state (level, antimatter inventory, deferred consumption). Each agency starts at level 0 with 0 antimatter on first connect.

**Architecture: Path B (per D1).** Same shape as S2.

**Pattern.** Direct copy of S2 with a single-record AgencyState entry instead of two collections. The shared scenario module name is `FarFutureTechnologyPersistence`; the child node FFT keys is `AMFactoryConfigNodeName` (see [near-future-and-far-future.md](near-future-and-far-future.md) for source). No `transferagency` migration needed (single-record, agency-scoped, no vessel keying).

**New files**

- `Server/System/Agency/AgencyFarFutureFactoryEntry.cs`
  - `int FactoryLevel`, `double AntimatterInventory`, `double DeferredConsumption`, `bool FirstLoad`. Mirrors FFT's persisted shape exactly. Invariant-culture doubles (Invariant 9).
- `Server/System/Agency/AgencyFarFutureRouter.cs`
  - `TryRoute(ClientStructure, string scenarioText)` — parses the inbound `FarFutureTechnologyPersistence` blob; reads the `AMFactoryConfigNodeName` child node into the sender's `AgencyState.FarFutureFactory`; suppresses the shared-store write. Router shape mirrors `AgencyKolonyRouter.TryRoute`. Log convention: `[fix:S3-FFT]`.

**Existing files to modify**

- `Server/System/Agency/AgencyState.cs`
  - Add: `public AgencyFarFutureFactoryEntry FarFutureFactory { get; set; }` (with the standard "reads also need the per-agency lock" XML doc-comment per Invariant 7). Single nullable record (no dictionary) since FFT has one global factory state, not per-body. Add Serialize / Parse paths mirroring post-MKS canonical shape.
- `Server/System/Agency/AgencyScenarioProjector.cs`
  - Add `"FarFutureTechnologyPersistence"` to `CareerScenarios`.
  - New `SpliceFarFutureFactoryIntoScenario(string, AgencyState)` — find-or-create + strip-then-splice the existing `AMFactoryConfigNodeName` child, splice in the per-agency entry (or omit if the agency has no factory yet). Per-entry isolation + whole-scenario fallback per Invariants 4 + 5.
- `ScenarioDataUpdater` ingress for `FarFutureTechnologyPersistence` → call `AgencyFarFutureRouter.TryRoute`.
- D2 catch-up: included in the `SendScenariosToClient` call site in `HandleHandshakeRequest` per S2's section.

**Tests**

- `ServerTest/Agency/AgencyFarFutureRouterTest.cs` — single-agency upsert, two-agency isolation, cross-agency-claim rejection logs Warning.
- `ServerTest/Agency/AgencyScenarioProjectorFarFutureTest.cs` — splice happy path, empty agency state (no factory record yet), parse-failure fallback.
- `ServerTest/Agency/AgencyStateFarFutureRoundTripTest.cs` — invariant-culture round-trip on `AntimatterInventory` + `DeferredConsumption` (Invariant 9 / BUG-013 precedent).

**Acceptance**

- Two agencies on the same server with FFT installed: agency A produces 50 antimatter; agency B's inventory remains 0. Agency A upgrades to level 2; agency B's factory stays at level 0.
- Connect-time delivery: synchronous catch-up via D2.
- Gate=off: shared global factory behaviour matches stock Luna MP.

---

## S4 — DMagic asteroid science + anomaly records per-agency

**Goal.** Each agency owns their own asteroid diminishing-returns log and their own discovered-anomaly records. Each agency starts with empty collections on first connect.

**Architecture: Path B (per D1).** Same shape as S2/S3.

**Pattern.** Same as S2/S3 but with two child collections (asteroid science + anomaly records) under the same scenario name (`DMScienceScenario`). No `transferagency` migration needed (asteroid-name-keyed + body+name-keyed; not vessel-keyed).

**New files**

- `Server/System/Agency/AgencyDMagicAsteroidEntry.cs`
  - Mirrors DMagic's `DMScienceData` shape: `string Title`, `double BaseValue`, `double ScientificValue`, `double Accumulated`, `double Cap`. Keyed by asteroid name (DMagic's identifier). Invariant-culture doubles.
- `Server/System/Agency/AgencyDMagicAnomalyEntry.cs`
  - Mirrors DMagic's `DMAnomalyObject` shape: `string Name`, `double Latitude`, `double Longitude`, `double Altitude`, `int BodyIndex`. Keyed by `BodyIndex + Name`. Invariant-culture doubles.
- `Server/System/Agency/AgencyDMagicRouter.cs`
  - `TryRoute` — parses inbound `DMScienceScenario`; splits the two child collections into sender's `AgencyState.DMagicAsteroidScience` / `AgencyState.DMagicAnomalies`; suppresses shared-store write. Router shape mirrors `AgencyKolonyRouter.TryRoute`. Log convention: `[fix:S4-DMagic]`.

**Existing files to modify**

- `Server/System/Agency/AgencyState.cs`
  - Add (with the standard per-agency-lock XML doc-comment per Invariant 7):
    ```csharp
    public Dictionary<string, AgencyDMagicAsteroidEntry> DMagicAsteroidScience { get; }
        = new Dictionary<string, AgencyDMagicAsteroidEntry>(StringComparer.Ordinal);
    public Dictionary<string, AgencyDMagicAnomalyEntry> DMagicAnomalies { get; }
        = new Dictionary<string, AgencyDMagicAnomalyEntry>(StringComparer.Ordinal);
    ```
  - Add Serialize / Parse paths mirroring post-MKS canonical shape (per-entry try/catch with `LunaLog.Warning("[fix:S4-DMagic] ...")` on malformed entries).
- `Server/System/Agency/AgencyScenarioProjector.cs`
  - Add `"DMScienceScenario"` to `CareerScenarios`.
  - New `SpliceDMagicScienceIntoScenario(string, AgencyState)` — find-or-create + strip both shared child collections, splice in per-agency entries. Per-entry isolation + whole-scenario fallback per Invariants 4 + 5.
- `ScenarioDataUpdater` ingress for `DMScienceScenario` → call `AgencyDMagicRouter.TryRoute`.
- D2 catch-up: included in the `SendScenariosToClient` call site in `HandleHandshakeRequest` per S2's section.

**Tests**

- `ServerTest/Agency/AgencyDMagicRouterTest.cs` — upserts on both collections, two-agency isolation, cross-agency-claim rejection logs Warning.
- `ServerTest/Agency/AgencyScenarioProjectorDMagicTest.cs` — splice happy path, empty agency state, parse-failure fallback.
- `ServerTest/Agency/AgencyStateDMagicRoundTripTest.cs` — invariant-culture round-trip on every double field (Invariant 9 / BUG-013).

**Acceptance**

- Two agencies on the same server with DMagic Orbital Science installed: agency A milks asteroid X for full diminishing-returns; agency B visits the same asteroid and receives full science independently.
- Agency A discovers Mun anomaly Y; agency B's anomaly UI does not show Y as known until B discovers it themselves.
- Connect-time delivery: synchronous catch-up via D2.
- Gate=off: shared behaviour matches stock Luna MP.

---

## S5 — `[x]` Science foreign-agency filter (sidecar Harmony)

**Goal.** `[x]` Science Continued's onboard / unrecovered science checklist no longer shows science aboard vessels owned by other agencies.

**Owner.** Luna Compat sidecar (or a fork-specific Luna Compat companion if upstream is slow). NOT core LMP fork.

**Harmony target.** `KSP_X_Science.ScienceContext` — the vessel enumeration inside the data-rebuild method (precise method name TBD at implementation time; the file is `X-Science/ScienceContext.cs` upstream, search for `FlightGlobals.Vessels.Where(x => x.loaded)`).

**Hook shape**

```csharp
[HarmonyPatch(typeof(ScienceContext), "GetUnloadedAndLoadedVesselScience")]  // or whatever the upstream method is named
public static class ScienceContext_FilterByAgency_Patch
{
    public static IEnumerable<Vessel> Postfix(IEnumerable<Vessel> __result)
    {
        if (!LmpClientAgency.PerAgencyEnabled)
            return __result;
        var myAgency = LmpClientAgency.LocalAgencyId;
        return __result.Where(v => LmpClientAgency.GetVesselAgency(v) == myAgency);
    }
}
```

**Acceptance**

- `PerAgencyCareer=on`: agency A's `[x]` Science checklist no longer lists "Crew Report at Eve" if that report exists on agency B's vessel.
- Gate=off: filter is a pass-through; behaviour matches single-player.

---

## S6 — KIS re-attach re-stamp (sidecar Harmony)

**Goal.** When a KIS-stored part is attached to a new vessel via EVA, the resulting part takes the destination vessel's `lmpOwningAgency`, overwriting any stamp the snapshotted source part carried.

**Owner.** Luna Compat sidecar (KIS is already a Luna Compat covered mod).

**Harmony target.** `KIS.KISAddonPickup` — the attach-finalisation method (precise name TBD at implementation time; search the upstream source for `OnAttachToolUsed` / `CreatePart` / similar — the path that ends with `Part` instantiation on the destination vessel).

**Hook shape**

```csharp
[HarmonyPatch(typeof(KISAddonPickup), "FinishAttach")]  // precise name TBD
public static class KISAddonPickup_RestampAgency_Patch
{
    public static void Postfix(Part newPart, Vessel destinationVessel)
    {
        if (!LmpClientAgency.PerAgencyEnabled || destinationVessel == null || newPart == null)
            return;
        var destAgency = LmpClientAgency.GetVesselAgency(destinationVessel);
        if (destAgency != Guid.Empty)
            LmpClientAgency.StampPart(newPart, destAgency);
    }
}
```

If verification confirms the stamp does NOT actually survive the KIS snapshot round-trip, this postfix is an idempotent no-op (the new part already has the right stamp). Building proactively per the decision.

**Acceptance**

- Agency A Kerbal takes a fuel tank out of agency B's container and attaches it to agency A's craft. The new part on agency A's craft is stamped agency A, on both clients.

---

## Cross-cutting integration tests

In addition to per-slice ServerTests:

1. **End-to-end per-agency exploration** (S2 + S3 + S4 together): two agencies on a Career server with SCANsat + FFT + DMagic installed. Verify all three mods isolate independently after a full reconnect cycle (synchronous catch-up via D2 means the projected state is observed at handshake-complete, not 30s later).
2. **Gate=off regression**: same setup as (1) but `PerAgencyCareer=false`. All three mods behave exactly as stock Luna MP (shared global state for every surface). `SendScenariosToClient` is safe to call under gate=off — `ProjectForClient` is a no-op there.
3. **Couple-then-isolate** (S1 + S2): agency A and agency B couple their scanning vessels via stock docking. After the merge, the surviving vessel's `lmpOwningAgency` is the kept side (S1 → `AgencyVesselCoupleReconciler`). Subsequent SCANsat scanning by the merged vessel attributes coverage to the kept agency only (S2's per-vessel scanner filter).
4. **`transferagency` Scanner migration** (S2 + Slice E): admin transfers a SCANsat-equipped vessel A→B. Per-vessel scanner records move with the vessel (D3 Option A); per-body coverage stays put.

---

## Out of scope for this spec (tracked elsewhere)

- **Final Frontier ribbons** ([outer-planets-mod.md](outer-planets-mod.md) follow-up). Only relevant if Final Frontier is added to the operator's frozen modlist. Same architectural shape — new mod doc + projector entry — if pursued later.
- **Near Future System Heat / DynamicBatteryStorage cross-vessel sims** ([near-future-and-far-future.md](near-future-and-far-future.md) gap). Out of audit batch; revisit if those modules are added to the modlist.
- **Trajectories' empty-`OnSave` ScenarioModule** ([trajectories.md](trajectories.md)). No work needed today; if upstream Trajectories ever populates `OnSave` with state, audit re-runs.
- **LunaCompat sidecar source verification** — not present on the local dev machine; requires either a clone or WebFetch authorization for the S5/S6 hook-point confirmation.
- **Multi-player-per-agency design considerations** — explicitly out of scope per the Operating Rule above. Permanent.

---

## Tracking

| Item | Status |
|------|--------|
| Per-mod audits complete | ✅ 12 mods walked |
| Design questions answered | ✅ 12 ratified 2026-05-18 |
| Slices specified | ✅ 6 (S1–S6) |
| D1 (Path B for S2/S3/S4) | ✅ ratified 2026-05-18, folded in 2026-05-19 |
| D2 (synchronous connect-time catch-up via `SendScenariosToClient`) | ✅ ratified 2026-05-18, folded in 2026-05-19 |
| D3 (`transferagency` migrate-with-vessel for S2 Scanners) | ✅ ratified 2026-05-18, folded in 2026-05-19 |
| M1–M11 (mechanical rebases — post-MKS-canonical references, find-or-create container, log tags, lock contract, invariant culture, empty-container handling, sync catch-up) | ✅ applied 2026-05-19 |
| Slices implemented | ⏳ pending |
| Cross-cutting tests authored | ⏳ pending |
| Operator policy doc update for LunaCompat coordination | ⏳ pending (folded into S2) |
