# Mod-compat implementation spec — consolidated roadmap

**Status.** Ratified 2026-05-18 against fork `c36d6f97`; **re-ratified 2026-05-19** against fork `c7d8e9f2` (post-MKS merge `d4ff0511` + BUG-038 XML prologue fix + per-agency contract relay v3 hotfix). The post-MKS-rebase addendum (D1/D2/D3 design decisions + M1–M11 mechanical rebases) has been folded into this document; the addendum itself is archived at [archive/implementation-spec-post-mks-rebase.md](archive/implementation-spec-post-mks-rebase.md) for the historical paper trail.

**S5 / S6 hook names corrected 2026-05-19** against local clones at `F:/tmp/mks-external/X-Science` SHA `eb8bfd3a`, `F:/tmp/mks-external/KIS` SHA `1620d0cb`, `F:/tmp/mks-external/LunaCompat` SHA `25e164bf`. The 2026-05-18 audit's spec'd Harmony targets (`ScienceContext.GetUnloadedAndLoadedVesselScience` for S5; `KISAddonPickup.FinishAttach` for S6) **do not exist** in the actual mod source. Real targets are `ScienceContext.UpdateOnboardScience` (private void, `X-Science/ScienceContext.cs:122-203`) and the existing-LunaCompat-patched `KIS_Shared.CreatePart` chokepoint (already patched at `LunaCompat/Mods/KerbalInventorySystem/KisIntegration.cs:40-43` — S6 extends `PostfixCreatePart`). See §S5 + §S6 below for the corrected hook shapes. The csproj orphan check (the trap that retired S3) passes on both: `ScienceContext.cs` and `KISAddonPickup.cs` are in their respective compiled DLLs. Audit re-walk recipe from [[feedback-audit-via-prespec]] caught the wrong-target error before any code shipped.

**S2 re-walked 2026-05-19** against SCANsat SHA `0d67371` (local clone at `F:/tmp/mks-external/SCANsat`). The S2 section's entry shapes, container layout, and field names were corrected to match verbatim source — see [SCANsat.md](SCANsat.md) "Re-walked 2026-05-19" subsection for the authoritative structural facts. Decisions §6 (SCANResources shared), §7 (root UI scalars shared), §8 (all Body fields per-agency), §9 (multi-Sensor-per-Vessel nested) extend the 2026-05-18 ratification.

**S3 RETIRED 2026-05-19** against FFT SHA `ad59fbb5` (local clone at `F:/tmp/mks-external/FarFutureTechnologies`). The 2026-05-18 audit's central premise — that FFT ships a `FarFutureTechnologyPersistence` ScenarioModule — was structurally invalid: the orphan source file is not in FFT's csproj Compile list and references symbols that don't exist in the source tree. Stock FFT has no shared global state to partition; all real state is per-`PartModule` already partitioned via `lmpOwningAgency`. Full retirement record at §S3 + [near-future-and-far-future.md "Re-walked 2026-05-19"](near-future-and-far-future.md). FFT joins the "no work owed" list.

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
| ~~**S3**~~ | ~~FFT antimatter factory per-agency~~ | **RETIRED 2026-05-19** — orphan source file, not in compiled DLL; no shared global state in stock FFT. See §S3 below + [near-future-and-far-future.md "Re-walked 2026-05-19"](near-future-and-far-future.md). | (no work owed) | — |
| **S4** | DMagic asteroid + anomaly per-agency | Core LMP fork | AgencyState + new router + projector entry (two child collections) | ~180 lines + ServerTest | S2 (copy-paste pattern) |
| **S5** | `[x]` Science foreign-agency filter | Luna Compat / sidecar Harmony | `ScienceContext` vessel enumeration | ~15 lines Harmony postfix | None |
| **S6** | KIS re-attach re-stamp | Luna Compat / sidecar Harmony | `KISAddonPickup` attach finalisation | ~10 lines Harmony postfix | None |

**Mods with no work owed:** MechJeb2, Kerbal Engineer Redux, OPM (data-only), Trajectories, DPAI, TweakScale, Near Future suite, **FFT** (S3 retired 2026-05-19 — orphan file not in compiled DLL; see §S3). Documented in their respective audits.

**Open verification items (not new code, but two-client repros that confirm or unblock):**
- KAS cross-agency couple ownership behaviour against the running fork (informs whether S1 needs to fire at all — but built proactively per the decision).
- KIS `lmpOwningAgency` stamp survival through PART snapshot (informs whether S6 fires in practice — but built proactively per the decision).

---

## Ordering recommendation

Suggested build sequence — but slices are independent enough to parallelise once the pattern is set:

1. **S2 first** (SCANsat) — shipped 2026-05-19 (commit `9fddb7fd`). Largest, set the per-agency-ScenarioModule pattern at full complexity.
2. ~~**S3** (FFT)~~ — RETIRED 2026-05-19 as no-work-owed (audit-via-prespec re-walk caught the orphan-file premise). See §S3.
3. **S4 next** (DMagic). Same Path B pattern as S2 but with two child collections under `DMScienceScenario`.
4. **S1 in parallel** with S4 (no dependency on the per-agency-ScenarioModule pattern; touches the docking-merge ingress instead).
5. **S5 + S6 in parallel** with any core slice (sidecar Harmony work; independent of core LMP).

Estimated build sequence wall-clock if serialised: ~3 review-bounded slices' worth of work remaining after S2 + S3 retirement. Two of the original six (S5, S6) belong in Luna Compat sidecar and can be assigned to whoever owns that adjunct.

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
// Server/System/HandshakeSystem.cs HandleHandshakeRequest, after the existing catch-up block.
// S3 was retired 2026-05-19 — FFT's FarFutureTechnologyPersistence scenario module does not
// actually ship in the compiled FFT.dll (orphan source file). Only S2 + S4 will land:
ScenarioSystem.SendScenariosToClient(client,
    "SCANcontroller",                  // S2 (shipped)
    "DMScienceScenario");              // S4 (pending)
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

**Goal.** Each agency owns their own `SCANcontroller` state — both the `Progress → Body` children (full Body shape per Decision §8: coverage `Map` blob + per-body palette + terrain ranges) and the `Scanners → Vessel → Sensor` nesting (per Decision §9: vessel-keyed entries with nested sensor list). Each agency starts at zero coverage on first connect to a fresh server. `SCANResources` and the ~30 root-level UI scalars stay shared (Decision §6/§7). Operator policy: LunaCompat's SCANsat server plugin entry is disabled when `PerAgencyCareer` is on.

**Architecture: Path B (per D1).** Server-side router intercepts `RawConfigNodeInsertOrUpdate`; no client-side Harmony, no dedicated wire, no `IgnoredScenarios` entry, no owner echo. Synchronous connect-time catch-up via `ScenarioSystem.SendScenariosToClient` (per D2).

**New files**

> **Structural authority (2026-05-19 re-walk).** The entry shapes below were rewritten 2026-05-19 against the local SCANsat clone at `F:/tmp/mks-external/SCANsat` SHA `0d67371`. The 2026-05-18 audit-derived shapes assumed flat fields on Scanner and missed `SCANResources`; both were fixed in [SCANsat.md](SCANsat.md) Decisions §6/§7/§8/§9. See SCANsat.md "Re-walked 2026-05-19" subsection for the verbatim source citations.
>
> **Three root containers exist** in the `SCANcontroller` blob: `Scanners`, `Progress`, `SCANResources`. The Path B router touches only the first two; **`SCANResources` is shared** per Decision §6 (its `MinMaxValues` is per-body resource display range / world config, not player-discovered amount). The ~30 root-level `KSPField` UI scalars are also shared per Decision §7.
>
> **`Scanners → Vessel → Sensor` is three-level nested.** Each Vessel node may contain 1-N Sensor children (a single vessel running survey + altimetry + resource sensors simultaneously). The entry type below carries `List<AgencyScannerSensorRecord>` to preserve this shape — flat fields cannot represent it.

- `Server/System/Agency/AgencyCoverageBodyEntry.cs`
  - One per body. Mirrors SCANsat's `Progress → Body` child node verbatim:
    ```csharp
    public string BodyName;                 // -> "Name" (the celestial body's bodyName, e.g. "Kerbin")
    public bool Disabled;                   // -> "Disabled"
    public float MinHeightRange;            // -> "MinHeightRange"
    public float MaxHeightRange;            // -> "MaxHeightRange"
    public float? ClampHeight;              // -> "ClampHeight" (nullable; emit only if non-null)
    public string PaletteName;              // -> "PaletteName"
    public int PaletteSize;                 // -> "PaletteSize"
    public bool PaletteReverse;             // -> "PaletteReverse"
    public bool PaletteDiscrete;            // -> "PaletteDiscrete"
    public string Map;                      // -> "Map" — opaque Base64-CLZF2-BinaryFormatter blob (URL-safe `/`→`-`, `=`→`_`); round-trip as string, NEVER decode
    public string LandingTarget;            // -> "LandingTarget" (nullable; "lat,lon" combined string; emit only if non-null)
    ```
    Per Decision §8, ALL Body fields are partitioned per-agency (not just `Map`). Invariant-culture doubles + floats on serialize per **Invariant 9** (BUG-013 precedent).
- `Server/System/Agency/AgencyScannerSensorRecord.cs` *(new — required by multi-Sensor-per-Vessel finding)*
  - Nested record type — one per active sensor on a vessel. Mirrors `Scanners → Vessel → Sensor`:
    ```csharp
    public int SensorType;                  // -> "type" (SCANtype enum as int)
    public float Fov;                       // -> "fov"
    public double MinAlt;                   // -> "min_alt"
    public double MaxAlt;                   // -> "max_alt"
    public double BestAlt;                  // -> "best_alt"
    public bool RequireLight;               // -> "require_light"
    ```
    Invariant-culture doubles on serialize.
- `Server/System/Agency/AgencyScannerEntry.cs`
  - One per vessel. Mirrors `Scanners → Vessel`:
    ```csharp
    public Guid VesselId;                   // -> "guid" (lowercase)
    public string VesselName;               // -> "name" (lowercase) — informational; KSP looks it up at load time from FlightGlobals
    public List<AgencyScannerSensorRecord> Sensors;  // -> N "Sensor" child nodes inside the Vessel node
    ```
    Indexed in `AgencyState.Scanners` by `VesselId`. Migrates with vessel under `transferagency` per D3.
- `Server/System/Agency/AgencyScanRouter.cs`
  - `public static class AgencyScanRouter`
  - `public static bool TryRoute(ClientStructure client, string scenarioText)` — caller is the `RawConfigNodeInsertOrUpdate` ingress for module name `SCANcontroller`. Returns `true` when the per-agency path handled the inbound; caller suppresses the shared-scenario write (Path B per D1).
  - Router shape mirrors `AgencyKolonyRouter.TryRoute`: single try/catch per entry, batch loop holds the per-agency lock once, no owner echo (Path B), no dedicated wire.
  - Parses the inbound `SCANcontroller` blob; iterates the `Progress → Body` children into `AgencyState.Coverage` (body-keyed upsert); iterates the `Scanners → Vessel → Sensor` children into `AgencyState.Scanners` (vessel-keyed upsert with the nested sensor list). `SCANResources` and root-level UI scalars are explicitly IGNORED (Decision §6/§7 — shared, not partitioned). Persists via `AgencySystem.SaveAgency`.
  - Per-body upsert (latest-wins, correct under 1:1 player↔agency).
  - Per-vessel scanner upsert filtered to the sender's owned vessels only (cross-agency scanner-record claims rejected with `LunaLog.Warning("[fix:S2-SCANsat] ...")` per Invariant 8 — Warning, not Debug, for cross-agency claims).
  - **Missing-container fallback (M8).** If the inbound blob has no `Progress` or `Scanners` container at all (operator running with an empty SCANsat install state, or pre-first-scan client), treat each missing container as a no-op for that category — NOT as a parse failure. Whole-scenario parse failure (malformed blob) still falls through to the input-unchanged path per Invariant 5.
  - Log convention: `[fix:S2-SCANsat]`. Warning on cross-agency-claim rejection; Debug on race-window drops (malformed Guid, vessel-not-in-store).
  - **Suppression model under Path B**: under gate=on, the router's `TryRoute=true` causes the caller (`ScenarioDataUpdater.RawConfigNodeInsertOrUpdate`) to SKIP the `CurrentScenarios.AddOrUpdate("SCANcontroller", ...)` write entirely. Server's `CurrentScenarios["SCANcontroller"]` stays at the operator-seeded baseline (initial server load) forever; the projector splices per-agency state on top of that frozen baseline. Under gate=off, `TryRoute` early-returns `false` and the legacy AddOrUpdate path runs unchanged.

**Existing files to modify**

- `Server/System/Agency/AgencyState.cs`
  - Add:
    ```csharp
    /// <summary>
    /// [Mod-compat S2 — SCANsat] Per-agency per-body coverage state. Keyed by
    /// CelestialBody name (Ordinal compare matches SCANsat's stock convention —
    /// SCANsat reads `Body.Name` as a string and looks up
    /// `FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == body_name)`).
    /// Carries the full Body child-node shape per Decision §8: not just
    /// coverage bitmap but also per-player palette + terrain-range UI prefs.
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
    /// Keyed by vessel GUID. Each entry carries a nested
    /// <see cref="AgencyScannerEntry.Sensors"/> list — a single vessel may
    /// run survey + altimetry + resource sensors simultaneously, so the
    /// entry-per-vessel shape with N nested sensor records is the correct
    /// representation (Decision §9; flat fields cannot represent it).
    /// Migrates with vessel under <c>transferagency</c> (D3 — see migration
    /// policy below).
    ///
    /// **Concurrency contract** (same shape as <see cref="KolonyEntries"/>):
    /// mutations AND reads MUST hold <see cref="AgencySystem.GetAgencyLock"/>.
    /// </summary>
    public Dictionary<Guid, AgencyScannerEntry> Scanners { get; }
        = new Dictionary<Guid, AgencyScannerEntry>();
    ```
  - Add Serialize / Parse paths mirroring the post-MKS `KolonyEntries` shape: invariant-culture doubles + floats, per-entry try/catch with `LunaLog.Warning("[fix:S2-SCANsat] skipped malformed entry ...")` on parse failure. Persisted under `SCAN_COVERAGE` and `SCAN_SCANNERS` child nodes of the agency file (mirrors `KOLONY_ENTRIES` naming convention).
- `Server/System/Agency/AgencyScenarioProjector.cs`
  - Add `"SCANcontroller"` to `CareerScenarios`.
  - Add `case "SCANcontroller": return SpliceSCANsatCoverageIntoScenario(serializedText, targetAgency);` to the `Project` switch.
  - New `SpliceSCANsatCoverageIntoScenario(string, AgencyState)` — round-trip through `ConfigNode`. Build via the canonical strip-then-splice template (see "Canonical post-MKS reference patterns" → `SpliceAgencyKolonyEntries`). For BOTH `Progress` and `Scanners` containers: find-or-create the container, strip shared children (`Body` and `Vessel` respectively), emit per-agency children from `targetAgency.Coverage` and `targetAgency.Scanners`. **Do NOT touch `SCANResources`** (Decision §6: shared) and **do NOT touch the ~30 root-level UI scalars** (Decision §7: shared). Whole-scenario parse-failure fallback per Invariant 5; per-entry isolation per Invariant 4.
  - **Vessel-name source**: `AgencyScannerEntry.VesselName` is informational; SCANsat's `OnLoad` re-derives the vessel name from `FlightGlobals` at load time. Emit whatever the agency state carries; an empty string is acceptable.
  - **Sensor list serialise**: emit one `Sensor` child node per `AgencyScannerSensorRecord` inside its parent `Vessel` node. Order is not load-bearing (SCANsat enumerates sensors-as-a-set, not list).
  - **Empty-container retention (M9).** After stripping shared children, emit an empty `Progress { }` and empty `Scanners { }` container if the agency has no entries (rather than omitting the containers entirely). Matches `SCANcontroller.OnLoad`'s null-check shape: `node_progress != null` / `node_vessels != null` — empty containers are fine, missing containers cause the OnLoad branch to skip. Confirmed against `SCANsat/SCANcontroller.cs:614` and `:783` (re-walk 2026-05-19).
- `Server/System/ScenarioSystem.cs` — new helper per D2:
  ```csharp
  internal static void SendScenariosToClient(ClientStructure client, params string[] scenarioNames);
  ```
- `Server/System/HandshakeSystem.cs::HandleHandshakeRequest` — call site for the helper immediately after the existing Path A catch-up block. Pass `"SCANcontroller"` (this slice) + `"FarFutureTechnologyPersistence"` (S3) + `"DMScienceScenario"` (S4) in a single call so the helper's per-name loop handles them uniformly.
- `Server/System/Scenario/ScenarioBaseDataUpdater.cs` (confirmed at re-walk: `RawConfigNodeInsertOrUpdate` lives here, not in `ScenarioContractsDataUpdater.cs`)
  - Inside the existing `Task.Run` body, after the brace-strip + `new ConfigNode(trimmed)` parse, dispatch on `scenarioModule`: when `"SCANcontroller"`, call `AgencyScanRouter.TryRoute(client, scenario)`. If it returns `true`, RETURN before `CurrentScenarios.AddOrUpdate` (Path B suppression). Otherwise fall through to the normal AddOrUpdate path (gate-off, or per-agency router declined).
  - **Caveat at implementation time**: the current `RawConfigNodeInsertOrUpdate` signature is `(string scenarioModule, string scenarioAsConfigNode)` — it does NOT carry a `ClientStructure`. Either extend the signature to thread the sender's client through (cheap; ~3 call-site edits), OR introduce a sibling overload that takes a `ClientStructure`. The sibling-overload form is friendlier to upstream merge cost; the extended-signature form is friendlier to test code. Pick at code-time after confirming the call-site count via `grep RawConfigNodeInsertOrUpdate`.
- **`transferagency` migration (per D3).** Extend the Slice E vessel-migration scan loop with a parallel pass: value-field-scan `AgencyState[A].Scanners` for entries with `VesselId == V`, move to `AgencyState[B].Scanners` (overwrite any pre-existing entry there). Per-body `Coverage` does **NOT** migrate (coverage is body-keyed, agency-scoped). S3 + S4 do not need migration logic — single-record / asteroid-name-keyed / body+name-keyed.

**Operator policy doc update**

- [README.md](README.md) — extend the "Operator policy — modlist uniformity" section with: "Under `PerAgencyCareer=on`, the operator MUST disable LunaCompat's SCANsat server plugin entry in `Universe\LunaCompat\ModSettingsStructure.xml`. The fork's `AgencyScanRouter` takes precedence."
- [SCANsat.md](SCANsat.md) — ensure the operator-config snippet is named.

**Tests**

- `ServerTest/Agency/AgencyScanRouterTest.cs`
  - Two agencies; agency A submits a `SCANcontroller` blob with Eve `Body` coverage entry. Agency B's submitted blob with Duna `Body` entry. Verify each agency's `AgencyState.Coverage` contains only their own body name keys after both routes complete.
  - Per-vessel scanner filter: agency A's `Vessel.guid` present in `AgencyState[A].Scanners`; absent from `AgencyState[B].Scanners`.
  - Multi-Sensor-per-Vessel: agency A submits a Vessel node with TWO Sensor children (e.g. SCANtype.Altimetry + SCANtype.AnomalyDetail). Verify the resulting `AgencyScannerEntry.Sensors` list has both records preserved (Decision §9).
  - Empty `Progress` / `Scanners` container in inbound → no-op for the missing category (M8). Missing `SCANResources` container is also no-op (it's never partitioned anyway).
  - Cross-agency-claim rejection: agency B sends a Vessel node carrying agency A's owned vessel id → upsert rejected, `LunaLog.Warning("[fix:S2-SCANsat] ...")` emitted (Invariant 8).
  - Gate=off (`PerAgencyEnabled=false`): `TryRoute` early-returns `false`, agency-state untouched.
- `ServerTest/Agency/AgencyScenarioProjectorSCANsatTest.cs`
  - Synthesise a shared `SCANcontroller` blob with two `Body` children (Eve + Duna) + two `Vessel` children (one owned by A, one by B) + a `SCANResources` container with two `ResourceType` children. Project for agency A → output `Progress` contains ONLY agency A's bodies (from `AgencyState[A].Coverage`), output `Scanners` contains ONLY agency A's vessel; `SCANResources` UNCHANGED (Decision §6); root-level UI scalars UNCHANGED (Decision §7).
  - Project against an agency with empty `Coverage` / `Scanners` → output has empty `Progress { }` and empty `Scanners { }` containers (UI scalars + `SCANResources` survive). Pins M9.
  - Whole-scenario parse failure → return input unchanged, log `Error`.
  - Multi-Sensor preservation through projection: agency state holds a `Vessel` with two nested `Sensor` records; projector emits both back into the projected blob's `Vessel` child. Verifies sensor-list round-trip end-to-end.
- `ServerTest/Agency/AgencyStateSCANsatRoundTripTest.cs`
  - Invariant-culture round-trip on every floating-point field across both `AgencyCoverageBodyEntry` (`MinHeightRange`, `MaxHeightRange`, `ClampHeight`) and `AgencyScannerSensorRecord` (`Fov`, `MinAlt`, `MaxAlt`, `BestAlt`). Mirrors `AgencyStateTest.Serialize_UsesInvariantCultureForDoubles` (Invariant 9 / BUG-013 precedent).
  - Optional-field round-trip: entry with `ClampHeight == null` serialises without the field; entry with `LandingTarget == null` serialises without the field; both round-trip back to `null`.
  - `Map` opaque-blob round-trip: a synthetic Base64-CLZF2-style string round-trips byte-equal (no accidental Trim/Normalize).
  - Per-entry parse-failure isolation: a malformed `Body` child does not abort `Parse`, logs `Warning("[fix:S2-SCANsat] ...")`, sibling entries survive (Invariant 4).
- `ServerTest/Agency/AgencyTransferAgencySCANsatMigrationTest.cs` (D3)
  - Two agencies A + B. Vessel V's `AgencyScannerEntry` (carrying 2 sensors) in `AgencyState[A].Scanners`. `transferagency V A→B` → entry moves to `AgencyState[B].Scanners` with the nested sensor list intact; absent from A; `AgencyState[B].Coverage` and `[A].Coverage` both unchanged.

**Acceptance**

- Two agencies on a `PerAgencyCareer=on` Career server with SCANsat installed.
- Agency A scans 60% of Eve; agency B's Eve coverage stays at 0%.
- Agency A accepts the SCANsat CC contract "scan 80% of Duna"; agency B accepts the same. They each progress independently against their own coverage. **Closes the cross-agency contract progress leak documented in [SCANsat.md](SCANsat.md).**
- **Connect-time delivery.** Synchronous catch-up via `SendScenariosToClient` from `HandleHandshakeRequest` (D2). Owner receives projected per-agency state at handshake-complete; no 30s window of shared-baseline observation.
- Gate=off (or non-Career game mode): SCANsat behaviour is identical to stock Luna MP.

---

## S3 — RETIRED 2026-05-19 (no work owed)

> **Retired** by the audit-via-prespec re-walk against `F:/tmp/mks-external/FarFutureTechnologies` SHA `ad59fbb5`. The 2026-05-18 audit's premise — that FFT ships a `FarFutureTechnologyPersistence` ScenarioModule serializing global antimatter factory state — was structurally invalid:
>
> 1. `FarFutureTechnologyPersistence.cs` is NOT in `Source/FarFutureTechnologies/FarFutureTechnologies.csproj`'s `<Compile Include="..."/>` list. The shipped FFT.dll contains no ScenarioModule.
> 2. The orphan file references `FarFutureTechnologySettings.amFactoryConfigNodeName` (undefined; the settings class has 5 unrelated static fields) and the `AntimatterFactory` class (does not exist anywhere in the source tree). Even reviving the orphan in the csproj would produce two CS0117 / CS0246 build errors.
>
> **Stock FFT has no shared global state to partition.** All real FFT state is per-`PartModule` (antimatter tanks, fusion reactors, charge-up engines) which already rides vessel-proto sync correctly under `lmpOwningAgency` from Stage 5.16b. Antimatter "purchase at rollout" via the `AntimatterManager` UI singleton charges per-agency science (which Stage 5.17c projection + 5.17e-5 router already partitions) and produces antimatter into per-vessel tanks (already per-agency).
>
> **Verdict overturn**: FFT joins the existing "no work owed" list (KER, OPM, Trajectories, DPAI, TweakScale, Near Future non-FFT suite). Full retirement record + verbatim source citations in [near-future-and-far-future.md "Re-walked 2026-05-19"](near-future-and-far-future.md).
>
> **If a future custom-patch FFT** completes the orphan (writes the missing `AntimatterFactory` class + the `amFactoryConfigNodeName` constant + re-adds the orphan to the csproj), it would introduce global state and a cross-agency leak. No such patch is documented as of 2026-05-19; if an operator reports cross-agency antimatter visibility, revisit and consider implementing the original S3 sketch below against that variant. The sketch is retained for that contingency.
>
> The remaining S3 historical content (file additions + test sketch + acceptance criteria) stays below the rule. The Scope Summary table at the top of this document still lists S3 in case the contingency fires; the table's "Pending" status maps to the retirement state above.

### Historical implementation sketch (kept for the operator-patch contingency above)

**Goal (historical).** Each agency owns its own Far Future Technologies antimatter factory state (level, antimatter inventory, deferred consumption). Each agency starts at level 0 with 0 antimatter on first connect.

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

> **Structural authority (2026-05-19 re-walk).** Entry shapes below were rewritten against the local DMagic clone at `F:/tmp/mks-external/DMagicOrbitalScience` SHA `a4e805b9`. The 2026-05-18 audit-derived shapes had three errors corrected in [dmagic-orbital-science.md](dmagic-orbital-science.md) Decisions §A/§B/§C/§D/§E: (a) asteroid-science fields are **float not double**; (b) anomaly wire is **2-level nested per-body**, not flat composite-key; (c) anomaly numerics use `"N5"` culture-sensitive format. Wire-format verified against `Source/Scenario/DMScienceScenario.cs:68-182` OnSave/OnLoad bodies.
>
> The csproj Compile-list orphan check (the trap that retired S3) passes — `DMScienceScenario.cs` is at line 129 of `Source/DMagicOrbital.csproj`.

- `Server/System/Agency/AgencyDMagicAsteroidEntry.cs`
  - Mirrors DMagic's `DMScienceData` shape verbatim:
    ```csharp
    public string Title;          // -> "title" (dict key in DMagic's recoveredDMScience)
    public float BaseValue;       // -> "bsv"  (float, NOT double — DMScienceData.cs:39-40)
    public float SciVal;          // -> "scv"
    public float Science;         // -> "sci"  (the running accumulator)
    public float Cap;             // -> "cap"
    ```
    Keyed in `AgencyState.DMagicAsteroidScience` by `Title` (Ordinal — DMagic dict uses string-keyed lookup). Invariant-culture floats on serialize per **Invariant 9** (BUG-013 precedent).
- `Server/System/Agency/AgencyDMagicAnomalyEntry.cs`
  - Mirrors DMagic's `DMAnomalyObject` shape — but FLATTENED for storage convenience (the wire shape is 2-level nested per Decision §B):
    ```csharp
    public int BodyIndex;         // -> "Body" on the DM_Anomaly_List wrapper (flightGlobalsIndex)
    public string Name;           // -> "Name" on the DM_Anomaly child
    public double Latitude;       // -> "Lat"
    public double Longitude;      // -> "Lon"
    public double Altitude;       // -> "Alt"
    ```
    Keyed in `AgencyState.DMagicAnomalies` by composite `$"{BodyIndex}|{Name}"` (Ordinal). The projector reconstructs the nested wire shape on emit by grouping entries by BodyIndex into per-body `DM_Anomaly_List` wrappers. Invariant-culture doubles on serialize per Invariant 9.
- `Server/System/Agency/AgencyDMagicRouter.cs`
  - `TryRoute(ClientStructure, ConfigNode)` — parses inbound `DMScienceScenario`; iterates `Asteroid_Science → DM_Science` children into `AgencyState.DMagicAsteroidScience` (Title-keyed); iterates `Anomaly_Records → DM_Anomaly_List → DM_Anomaly` nested children into `AgencyState.DMagicAnomalies` (BodyIndex+Name composite-keyed); suppresses shared-store write (Path B per D1). Router shape mirrors `AgencyKolonyRouter.TryRoute` + `AgencyScanRouter.TryRoute`. Log convention: `[fix:S4-DMagic]`. Per-entry isolation (Invariant 4) at both levels for anomaly (per-body wrapper + per-anomaly child).
  - **No cross-agency rejection** — both asteroid science (Title-keyed) and anomaly records (Body+Name-keyed) are NOT vessel-keyed. There's no "agency owns this entry" concept at upsert time; latest-wins under the 1:1 player↔agency rule (Operating Rule). Same shape as AgencyKolonyRouter's body-keyed entries.
  - **No transferagency migration** — entries are not vessel-keyed, so vessel A→B transfer doesn't move asteroid-science or anomaly records.

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

**Owner.** Luna Compat sidecar (fork at `Majestic95/LunaCompat`, per the 2026-05-19 source-walk decision; PR back to `TheXankriegor/LunaCompat` after soak). **NOT this fork's codebase.**

**Hook target (corrected 2026-05-19).** `KSP_X_Science.ScienceContext.UpdateOnboardScience` — private void instance method at `X-Science/ScienceContext.cs:122-203`. The original spec'd `GetUnloadedAndLoadedVesselScience` does not exist in upstream. `UpdateOnboardScience` enumerates `FlightGlobals.Vessels.Where(x => x.loaded)` at line 134 AND walks unloaded vessels via `HighLogic.CurrentGame.flightState.Save(node)` → `node.GetNodes("VESSEL")` at lines 167-174 — both branches need filtering. Output goes into private `_onboardScience` dict exposed via public `OnboardScienceList` getter at line 33.

**Hook shape**

```csharp
[HarmonyPatch(typeof(ScienceContext), "UpdateOnboardScience")]
public static class ScienceContext_FilterByAgency_Patch
{
    public static void Postfix(ScienceContext __instance)
    {
        if (!LmpClientAgency.PerAgencyEnabled) return;
        var myAgency = LmpClientAgency.LocalAgencyId;
        // Filter __instance.OnboardScienceList in place — entries whose
        // underlying Vessel's lmpOwningAgency does not match myAgency are
        // removed. Two-path lookup:
        //   - loaded branch keys are vessel guids → use AgencySystem.TryGetOwningAgency
        //   - unloaded branch keys come from VESSEL ConfigNode → re-parse lmpOwningAgency field
        // Implementation detail belongs in LunaCompat, not this fork's spec.
    }
}
```

**Acceptance**

- `PerAgencyCareer=on`: agency A's `[x]` Science checklist no longer lists "Crew Report at Eve" if that report exists on agency B's vessel.
- Gate=off: filter is a pass-through; behaviour matches single-player.

---

## S6 — KIS re-attach re-stamp (sidecar Harmony)

**Goal.** When a KIS-stored part is attached to a new vessel via EVA, the resulting part takes the destination vessel's `lmpOwningAgency`, overwriting any stamp the snapshotted source part carried.

**Owner.** Luna Compat sidecar (fork at `Majestic95/LunaCompat`). KIS is already covered by an existing LunaCompat patch — S6 EXTENDS that patch, not a new one.

**Hook target (corrected 2026-05-19).** `KIS.KIS_Shared.CreatePart` — the 9-arg overload (`ConfigNode, Vector3, Quaternion, Part, Part, string, AttachNode, OnPartReady, bool`). This is the single chokepoint that BOTH `KISAddonPickup.CreateAttach` (new-part attach, `KISAddonPickup.cs:1411-1437`) AND `KISAddonPickup.MoveAttach` (re-attach via `KIS_Shared.MoveAssembly`) route through. The original spec'd `KISAddonPickup.FinishAttach` does not exist. **LunaCompat already patches this exact method** at `LunaCompat/Mods/KerbalInventorySystem/KisIntegration.cs:40-43` with a `PostfixCreatePart` that unflags-debris and broadcasts a vessel proto — S6 adds the agency restamp to the same postfix.

**Hook shape**

```csharp
// Extension to LunaCompat/Mods/KerbalInventorySystem/KisIntegration.cs::PostfixCreatePart
static void PostfixCreatePart(Part __result)
{
    // ... existing unflags-debris + SendVesselMessage logic ...

    if (LmpClientAgency.PerAgencyEnabled && __result?.vessel != null)
    {
        var destAgency = LmpClientAgency.GetVesselAgency(__result.vessel);
        if (destAgency != Guid.Empty)
        {
            // Stamp the destination vessel's lmpOwningAgency onto the new
            // part's top-level vessel ConfigNode field. The existing
            // PostfixCreatePart already calls SendVesselMessage immediately
            // after, so the stamp rides the same proto broadcast.
            // Implementation detail belongs in LunaCompat.
        }
    }
}
```

**Note on S1 interaction.** `KISAddonPickup.MoveAttach` calls `KIS_Shared.MoveAssembly` (not `Part.Couple` directly) — so the S1 server-side couple reconciler does NOT catch KIS re-attaches. S6 is therefore load-bearing for KIS-attach ownership correctness even after S1 ships. Stock docking + KAS pipe coupling DO go through `Part.Couple` and are covered by S1.

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
| S2 entry shapes corrected against verified SCANsat source (`0d67371`) — multi-Sensor nested, SCANResources shared, all-Body-fields per-agency, root UI scalars shared | ✅ applied 2026-05-19 (Decisions §6/§7/§8/§9 in SCANsat.md) |
| S2 implementation shipped (`feat(server,agency): Mod-compat S2 — SCANsat per-agency coverage + scanners` commit `9fddb7fd`) | ✅ 2026-05-19 |
| S3 RETIRED against verified FFT source (`ad59fbb5`) — orphan ScenarioModule not in csproj Compile list; FFT joins no-work-owed list | ✅ retired 2026-05-19 |
| S4 implementation shipped (`feat(server,agency): Mod-compat S4 — DMagic asteroid science + anomalies per-agency` commit `06cc7444`) | ✅ 2026-05-19 |
| S1 implementation shipped — couple reconciler covering stock docking + KAS pipe coupling; M1 (per-vessel lock race fix) + M2 (broadcast visibility + disk flush on adopt branch) applied via multi-lens review | ✅ 2026-05-19 |
| S5 / S6 hook targets corrected against verified upstream source — work owed to fork-LunaCompat at `Majestic95/LunaCompat` (sidecar codebase, separate from this fork) | ⏳ pending (separate session) |
| Slices implemented | S1 + S2 + S4 done; S5 + S6 pending in LunaCompat sidecar |
| Cross-cutting tests authored | ⏳ pending — S1+S2+S4 in-repo coverage at unit + projector level; end-to-end MockClientTest cross-cutting (couple-then-scan) deferred |
| Operator policy doc update for LunaCompat coordination | ⏳ pending (folded into S2 SCANsat operator policy) |
