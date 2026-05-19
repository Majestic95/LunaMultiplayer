# Post-MKS rebase addendum — to apply to `implementation-spec.md`

**Status.** Decisions D1–D3 RATIFIED 2026-05-18; mechanical rebases M1–M11 are pre-cleared and ready to apply once `feature/per-agency-mks` merges into `feature/per-agency`.

**Trigger.** Apply this addendum to [implementation-spec.md](implementation-spec.md) immediately after the MKS merge completes. After applying, re-ratify the parent spec at its new date.

**Why this addendum exists.** The parent spec was ratified at fork commit `c36d6f97` (2026-05-18 morning). `feature/per-agency-mks` introduces three architectural patterns the parent spec does not reflect: (a) the canonical strip-then-splice projector entry shape has moved from `SpliceAgencyTechIntoResearchAndDevelopment` to `SpliceAgencyKolonyEntries`; (b) the per-entry isolation logging convention now uses `[fix:<slice-tag>]` operator-grep prefixes with a Warning-vs-Debug split; (c) the new persistence convention (BUG-013) pins invariant culture on every double. None of these are architectural conflicts — they are spec-text rebases.

The addendum also captures **three design decisions the parent spec leaves implicit** that the MKS work brought into sharp focus.

---

## Operating rule (supersedes any prior framing)

**1 player ↔ 1 agency under `PerAgencyCareer=true` (gate=on). Permanent design.**

- Each player owns exactly one agency. Each agency has exactly one player.
- The only "multiple players share state" mode is gate=off (`PerAgencyCareer=false`), which is the legacy LMP shared-scenario behavior.
- Multi-player-per-agency under gate=on is **NOT** being implemented and is **NOT** a roadmap item.
- Comments in older prespec docs ([05a-plaguenz-audit.md:100](../research/05a-plaguenz-audit.md), [mks-lmp-compatibility-phase-2-prespec.md:258](../research/mks-lmp-compatibility-phase-2-prespec.md), [phase-3-prespec.md:183](../research/mks-lmp-compatibility-phase-3-prespec.md)) that allude to "multiple players per agency" are **stale/speculative** and do NOT override this rule.

**Implications**:

- **Latest-wins upsert semantics are correct** for every per-agency dictionary (Coverage bitmaps, factory inventory, asteroid science, anomaly records, etc.). Only one client ever writes to a given `AgencyState[X]` slot. **No OR-merge / max-merge / CRDT-style convergence is needed.**
- **Cross-agency isolation is structurally enforced** by sender authority: `AgencySystem.AgencyByPlayerName[client.PlayerName]` is the server-derived agency id; the client's message content does not carry a trusted agency id ([AgencyKolonyRouter.cs:25-28](../../Server/System/Agency/AgencyKolonyRouter.cs#L25-L28)).
- **Reconnect "catch-up" is owner-singular**. There are no teammates awaiting fan-out.

---

## Verification grounding

Files read in full during preparation of this addendum (so a future Claude session can pick up cold):

- [implementation-spec.md](implementation-spec.md) (316 lines, ratified 2026-05-18)
- [SCANsat.md](SCANsat.md), [near-future-and-far-future.md](near-future-and-far-future.md), [dmagic-orbital-science.md](dmagic-orbital-science.md)
- Post-MKS on the `feature/per-agency-mks` branch:
  - [Server/System/Agency/AgencyState.cs](../../Server/System/Agency/AgencyState.cs) (full)
  - [Server/System/Agency/AgencyKolonyRouter.cs](../../Server/System/Agency/AgencyKolonyRouter.cs) (full)
  - [Server/System/Agency/AgencyScenarioProjector.cs](../../Server/System/Agency/AgencyScenarioProjector.cs) (full)
  - [Server/System/Agency/AgencySystemSender.cs](../../Server/System/Agency/AgencySystemSender.cs) (full)
  - [LmpClient/Harmony/KolonizationManager_TrackLogEntryPostfix.cs](../../LmpClient/Harmony/KolonizationManager_TrackLogEntryPostfix.cs) (full)
  - [LmpCommon/IgnoredScenarios.cs](../../LmpCommon/IgnoredScenarios.cs) (full)
  - [Server/Message/VesselMsgReader.cs](../../Server/Message/VesselMsgReader.cs) diff (per-agency vs MKS)
  - [Server/System/Agency/AgencyContractRouter.cs](../../Server/System/Agency/AgencyContractRouter.cs) lines 62-72, 114, 163-178

Verified facts (not inferences):

- ✓ No existing fork code routes `SCANcontroller`, `FarFutureTechnologyPersistence`, or `DMScienceScenario` today. Grep across all .cs files in `feature/per-agency-mks` returns only `BodyContextKeys` references for SCANsat-related strings.
- ✓ [IgnoredScenarios.IgnoreSend](../../LmpCommon/IgnoredScenarios.cs) already contains 8 career-related scenarios annotated *"This scenario has its own handling system"* — i.e., the fork's established per-agency architecture is what this doc calls Path A (see D1).
- ✓ Neither `KolonizationScenario` nor `PlanetaryLogisticsScenario` is yet present in `IgnoredScenarios.IgnoreSend` in the MKS branch state (those are deferred to Phase 3 Slice B item 10 / Slice C item 13 per doc-comments).
- ✓ `AgencyContractRouter.SharedScenarioStates = {"Offered", "Generated"}`, every other state routes per-agency.
- ✓ S1's planned hook point (`OnVesselDock` / `HandleVesselProto`) is untouched by MKS. The MKS diff in [VesselMsgReader.cs](../../Server/Message/VesselMsgReader.cs) is a revert of Stage 5.18d slice (i) inside `HandleVesselsSync`, not a couple/dock hook.

---

## Design decisions (RATIFIED 2026-05-18)

### D1 — Path B architecture for S2/S3/S4 ✅ RATIFIED

**Context.** The fork's established per-agency career architecture (call it **Path A**) has seven components:

1. Client-side Harmony postfix on KSP/mod mutation method (e.g., [KolonizationManager_TrackLogEntryPostfix](../../LmpClient/Harmony/KolonizationManager_TrackLogEntryPostfix.cs))
2. Dedicated wire path (`Agency*MsgData`)
3. Server-side router (`Agency*Router`)
4. Server-side projector splice in [AgencyScenarioProjector.CareerScenarios](../../Server/System/Agency/AgencyScenarioProjector.cs)
5. `IgnoredScenarios.IgnoreSend` entry to suppress the SHA broadcast
6. Owner-only echo on each router success ([SendKolonyStateToOwner](../../Server/System/Agency/AgencySystemSender.cs#L308))
7. Connect-time catch-up ([SendKolonyCatchupTo](../../Server/System/Agency/AgencySystemSender.cs#L355))

Every per-agency career system in the fork uses Path A: contracts (Stage 5.17d), tech/science/parts (Stage 5.17e-4/5), strategies/achievements/facilities (Stage 5.17e-6), kolony (MKS Phase 3 Slice B), planetary (MKS Phase 3 Slice C).

The parent spec proposes a **different** architecture for S2/S3/S4 (call it **Path B**), simpler:

1. ~~No client-side Harmony~~ (relies on existing 30s SHA broadcast for `SCANcontroller` / `FarFutureTechnologyPersistence` / `DMScienceScenario`)
2. ~~No dedicated wire~~
3. Server-side router intercepting `RawConfigNodeInsertOrUpdate` (instead of dedicated `MsgReader`)
4. Server-side projector splice (same as Path A)
5. ~~No `IgnoredScenarios` entry~~ (broadcast still fires; router suppresses shared-store write inside the ingest)
6. ~~No owner echo~~
7. ~~No catch-up~~ (owner sees state on first `SendScenarioModules` tick post-connect, ≤30s)

**Decision.** Adopt Path B for S2/S3/S4. Document the rationale explicitly in the parent spec.

**Rationale to add to spec:**

> Path B is chosen for S2/S3/S4 because (a) SCANsat's pixel-scan mutation is internal `SCAN_Data` state without a clean public API to Harmony-postfix — the [SCANsat.md audit](SCANsat.md) explicitly punts mutation-side hookability to Luna Compat; (b) FFT antimatter factory and DMagic asteroid/anomaly mutations are infrequent enough that the SHA-broadcast bandwidth overhead is negligible; (c) the existing `RawConfigNodeInsertOrUpdate` ingress is mod-agnostic — it works for any third-party `ScenarioModule` without requiring a per-mod Harmony patch surface. Path A remains the canonical choice for stock-career systems and for mods that already expose a usable mutation event (kolony, planetary).

**No correctness concerns** under the 1:1 player↔agency rule: there is no concurrent-writer race within an agency, so latest-wins on `RawConfigNodeInsertOrUpdate` is correct semantics.

**Ratified outcome**: Path B for all three slices. Single-slice Path A migration available as a follow-up if telemetry warrants.

---

### D2 — Connect-time catch-up: add Path-A-style synchronous delivery ✅ RATIFIED

**Context.** Under Path B, an owner reconnecting to their agency receives per-agency state only on the first `SendScenarioModules` tick post-connect (≤30s under the standard SHA pass cadence). Before that tick, the owner's client briefly observes the shared-scenario baseline (typically empty for a fresh server, or the operator-supplied seed under upgrade-in-place).

**Comparison**: Path A's catch-up ([SendKolonyCatchupTo](../../Server/System/Agency/AgencySystemSender.cs#L355)) fires from `HandshakeSystem.HandleHandshakeRequest` immediately after handshake — owner has persisted state before any scene-load.

**Decision.** Add synchronous connect-time catch-up for S2/S3/S4. Owner receives projected per-agency state at handshake-complete, before any scene-load. No ≤30s window of shared-baseline observation.

**Rationale**: Cleaner reconnect UX, no edge cases around "what if the player jumps straight to Tracking Station within 30s of reconnect." Implementation cost is low because we don't need new wire surfaces — the projector already knows how to emit a per-agency-projected scenario; we just trigger that emission immediately on handshake-complete instead of waiting for the 30s SHA tick.

**Implementation shape (single approach, ratified):**

Reuse the existing scenario-send mechanism rather than adding dedicated `Send*CatchupTo` methods per slice. Add to [ScenarioSystem](../../Server/System/ScenarioSystem.cs) a one-shot helper:

```csharp
// Server-side. Sends the named scenarios — projected per-recipient — to a single client
// outside the normal SHA broadcast cadence.
internal static void SendScenariosToClient(ClientStructure client, params string[] scenarioNames)
{
    foreach (var name in scenarioNames)
    {
        if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue(name, out var raw))
            continue;
        // ProjectForClient is a no-op when PerAgencyEnabled=false (preserves gate=off behavior).
        var projected = AgencyScenarioProjector.ProjectForClient(name, raw, client);
        // Emit via the existing scenario-data wire path (same as SendScenarioModules per-scenario loop).
        // Concrete wiring TBD at implementation time against ScenarioSystem.SendScenarioModules' internals.
    }
}
```

Call from [HandshakeSystem.HandleHandshakeRequest](../../Server/System/HandshakeSystem.cs) immediately after the existing `SendKolonyCatchupTo` / `SendPlanetaryCatchupTo` / `SendContractCatchupTo` block:

```csharp
ScenarioSystem.SendScenariosToClient(client,
    "SCANcontroller",                  // S2
    "FarFutureTechnologyPersistence",  // S3
    "DMScienceScenario");              // S4
```

**Why not per-slice dedicated catch-up methods?** Under Path B (D1), there's no per-slice wire surface to ship a `Send*CatchupTo` over. The data already flows through the scenario channel; reusing it for catch-up is one helper, not three. If a slice later migrates to Path A independently, that slice gains its own dedicated catch-up at that time.

**Gate behavior**: `SendScenariosToClient` is safe to call unconditionally — `AgencyScenarioProjector.ProjectForClient` early-returns under gate=off ([AgencyScenarioProjector.cs:119-128](../../Server/System/Agency/AgencyScenarioProjector.cs#L119-L128)), so under gate=off the helper sends the unmodified shared scenario, which is the legitimate gate=off behavior anyway.

**Ratified outcome**: synchronous catch-up via the shared helper. See **M11** below for the mechanical work item.

---

### D3 — `transferagency` migration policy for S2 Scanners ✅ RATIFIED

**Context.** S2's `Scanners` dict is `Dictionary<Guid VesselId, AgencyScannerEntry>` — vessel-keyed. When an admin runs `transferagency` to move a vessel from agency A to agency B (5.18d slice (e)), MKS' precedent for vessel-keyed entries ([AgencyKolonyRouter.cs:193-204](../../Server/System/Agency/AgencyKolonyRouter.cs#L193-L204)) is migrate-with-vessel: value-field-scan source agency's dict for entries with matching `VesselId`, move to destination agency's dict.

The parent spec is silent on this for S2. S3 (single-record FFT factory) and S4 (asteroid-name-keyed + body+name-keyed) do not have vessel-keyed entries and do not need migration logic.

**Decision options:**

| Option | Behavior |
|---|---|
| **Option A — Migrate with vessel** (recommended) | Vessel V transferred A→B: V's scanner records move from `AgencyState[A].Scanners` to `AgencyState[B].Scanners`. Matches MKS Slice E precedent for vessel-keyed entries. |
| Option B — Drop on transfer | Vessel V transferred A→B: V's scanner records are deleted from `AgencyState[A].Scanners`, NOT added to `AgencyState[B].Scanners`. B's scanner state for V is empty; V's scanning state is effectively reset. Easier to implement, breaks scanning continuity. |
| Option C — No migration (leave orphaned) | Vessel V transferred A→B: V's scanner records remain in `AgencyState[A].Scanners` indefinitely. Wire bloat over time; A could "see" V's scanner activity even after losing ownership. Wrong on principle. |

**Recommended**: Option A. Matches MKS' established precedent and preserves the contract that "an agency's scanner records reflect the agency's currently-owned vessels."

**Spec text to add to §S2 after the existing "transferagency migration policy" paragraph (currently absent)**:

> **transferagency migration**: vessel V transferred from agency A to agency B → value-field-scan `AgencyState[A].Scanners` for entries with `VesselId == V`, move to `AgencyState[B].Scanners` (overwrite any pre-existing entry there). Per-body `Coverage` does NOT migrate (coverage is body-keyed, agency-scoped — A's discoveries of Eve stay A's; B retains B's). Slice E implementation: extend the existing kolony migration scan loop with a parallel pass on `Scanners`.

**Ratified outcome**: Option A — migrate with vessel.

---

## Mechanical rebases (CLOSED — apply post-merge)

These items have no design content; they are textual edits to align the parent spec with post-MKS conventions. Apply in order.

### M1 — §S2 canonical splice template citation

**Location**: [implementation-spec.md:117-119](implementation-spec.md#L117-L119) (and parallel citations in §S3/§S4).

**Old**: *"Wrap in the same try/catch fallback as SpliceAgencyTechIntoResearchAndDevelopment"*

**New**: *"Wrap in the same try/catch fallback as `SpliceAgencyKolonyEntries` ([AgencyScenarioProjector.cs:613](../../Server/System/Agency/AgencyScenarioProjector.cs#L613)) — the post-MKS canonical splice pattern. Strip-then-splice + per-entry isolation + whole-scenario parse-failure fallback that logs at Error level."*

**Why**: MKS Phase 3 Slice B shipped a cleaner splice template; the older 5.17e-4 splice is no longer the canonical reference.

---

### M2 — §S2/§S3/§S4 find-or-create container pattern

**Location**: §S2 around the "Strip ALL existing `Progress` children" sentence.

**Add**: *"Use the find-or-create container pattern from [AgencyScenarioProjector.cs:627-640](../../Server/System/Agency/AgencyScenarioProjector.cs#L627-L640):*
```csharp
var container = node.GetNode("CONTAINER_NAME")?.Value;
if (container == null)
{
    container = new ConfigNode("") { Name = "CONTAINER_NAME" };
    node.AddNode(container);
}
else
{
    foreach (var existing in container.GetNodes("CHILD_NAME").ToArray())
        container.RemoveNode(existing.Value);
}
// then splice per-agency children
```
*This handles the case where the inbound shared scenario blob has no container at all (fresh server, empty modlist) as well as the case where it has one with shared children to strip."*

**Why**: Parent spec's strip-only instruction doesn't address the missing-container edge case.

---

### M3 — §S2/§S3/§S4 router shape

**Location**: each slice's "New router" subsection.

**Add reference**: *"Router shape mirrors [AgencyKolonyRouter.TryRoute](../../Server/System/Agency/AgencyKolonyRouter.cs#L85-L169) (post-MKS canonical): single try/catch per entry wrapping classify + lookup + cross-agency check + upsert; `accepted` list collected outside the catch; `AgencySystem.SaveAgency(agencyId)` + (no echo for Path B — see D2) once the batch is complete. The per-agency lock is acquired once for the entire batch loop, not per-entry."*

**Why**: Establishes the canonical reference for future reviewers; ensures the new routers adopt the post-MKS isolation pattern rather than the older 5.17d two-step shape.

---

### M4 — §S2 logging convention

**Location**: §S2 router pseudocode and test assertions.

**Add**: Adopt `[fix:<slice-tag>]` tag prefix on every `LunaLog` line, per [AgencyKolonyRouter](../../Server/System/Agency/AgencyKolonyRouter.cs)'s `[fix:MKS-R2]` convention. Tags by slice:

- S2 → `[fix:S2-SCANsat]`
- S3 → `[fix:S3-FFT]`
- S4 → `[fix:S4-DMagic]`

Also adopt the Warning-vs-Debug split:

- **`Warning`**: cross-agency-claim rejection (a sender's blob references a vessel owned by a different agency). Operator-visible.
- **`Debug`**: race-window drops (malformed Guid, vessel-not-in-store). Hot-path noise; operator-invisible by default.

**Why**: Operator-grep visibility. Mirrors the 5.17a soak Finding-2 precedent.

---

### M5 — §S2/§S3/§S4 per-agency lock contract on new `AgencyState` fields

**Location**: each slice's "Add to AgencyState" snippet.

**Add doc-comment requirement**: every new dict field must carry the *"reads also need the lock"* contract paragraph, modeled on [AgencyState.KolonyEntries](../../Server/System/Agency/AgencyState.cs#L156-L173):

> **Concurrency contract** (same shape as `TechNodes`): mutations AND reads MUST hold `AgencySystem.GetAgencyLock`. Dictionary's non-concurrent enumerator throws (or worse) on a mid-iteration mutation; the per-agency lock is the only safe enumeration path.

**Why**: Forward-compat — future projector readers won't otherwise know the contract.

---

### M6 — §S2/§S3/§S4 Serialize/Parse forward-compat

**Location**: each slice's "Add Serialize / Parse paths mirroring how `TechNodes` and `ScienceSubjects` round-trip" sentence.

**Update reference**: point at the MKS-canonical shape ([AgencyState.cs:417-447](../../Server/System/Agency/AgencyState.cs#L417-L447) for serialize, [AgencyState.cs:765-825](../../Server/System/Agency/AgencyState.cs#L765-L825) for parse with per-entry isolation + `LunaLog.Warning` on skips per `[fix:MKS-R2]` review-finding-#3).

**Why**: Older 5.17e-4 parse paths silently skip malformed entries; post-MKS standard logs.

---

### M7 — §S2/§S3/§S4 invariant culture + BUG-013 test

**Location**: each slice's "Tests" subsection.

**Add**: For any double-valued field on a new entry type, include a test mirroring `AgencyStateTest.Serialize_UsesInvariantCultureForDoubles` to pin the invariant-culture contract. Reference: [AgencyState.cs:418-422](../../Server/System/Agency/AgencyState.cs#L418-L422) doc-comment names BUG-013 as the precedent.

**Why**: A comma-decimal server locale would otherwise corrupt the on-disk and on-wire formats silently.

---

### M8 — §S2 missing-container fallback (specific to inbound blob)

**Location**: §S2 router parse logic.

**Add**: If the inbound `SCANcontroller` blob has no `Progress` or `Scanners` container (operator running with an empty SCANsat install state, or pre-first-scan client), the router treats it as a no-op for the missing containers (upsert nothing for that container's category), NOT as a parse failure. Whole-scenario parse failure (malformed blob) still falls through to the input-unchanged path per the parent spec's whole-scenario fallback contract.

**Why**: Clarifies the empty-blob path that the parent spec didn't address.

---

### M9 — §S2 SCANsat-specific empty-container retention

**Location**: §S2 splice logic.

**Add**: After stripping shared `Progress` / `Scanners` children, emit an empty `Progress { }` and empty `Scanners { }` container if the agency has no entries (rather than omitting the containers entirely). Matches stock SCANsat's OnLoad which expects the containers to exist even when empty.

**Why**: Prevents a NRE in stock SCANsat's `SCANcontroller.OnLoad` if the projected scenario omits the containers. Verifiable post-implementation against [SCANsat upstream](https://github.com/KSPModStewards/SCANsat) but recorded here as a defensive default.

---

### M10 — Cross-cutting integration test §3 ordering

**Location**: [implementation-spec.md:294](implementation-spec.md#L294) (S1 + S2 couple-then-isolate test).

**Update**: Reference the post-MKS [AgencyVesselCoupleReconciler] pattern (still to be authored by S1; S1's hook placement is unchanged by the MKS merge per the verification grounding above). Test sequence remains as written; only the citation reference shifts.

**Why**: Keep cross-reference accurate.

---

### M11 — Synchronous connect-time catch-up for S2/S3/S4 (D2 implementation)

**Location**: new helper in [Server/System/ScenarioSystem.cs](../../Server/System/ScenarioSystem.cs); call site in [Server/System/HandshakeSystem.cs](../../Server/System/HandshakeSystem.cs).

**Add**:

1. New helper `ScenarioSystem.SendScenariosToClient(ClientStructure client, params string[] scenarioNames)` — emits the named scenarios (projected per-recipient via the existing `AgencyScenarioProjector.ProjectForClient` path) to a single client outside the normal 30s SHA cadence. Wiring details follow the same wire path `SendScenarioModules` uses for its per-scenario loop; verify the concrete emission method against [Server/System/ScenarioSystem.cs](../../Server/System/ScenarioSystem.cs) at implementation time.

2. Call from `HandshakeSystem.HandleHandshakeRequest` immediately after the existing catch-up block (`SendContractCatchupTo` / `SendKolonyCatchupTo` / `SendPlanetaryCatchupTo`):
   ```csharp
   ScenarioSystem.SendScenariosToClient(client,
       "SCANcontroller",                  // S2
       "FarFutureTechnologyPersistence",  // S3
       "DMScienceScenario");              // S4
   ```

3. Test (in `ServerTest`): pin that `SendScenariosToClient` invokes the projector and emits the projected blob via the standard scenario wire path. Verify under gate=off the projector early-returns and the client receives the shared scenario unchanged.

**Why**: Implements D2. Provides synchronous per-agency state delivery on reconnect with zero new wire surfaces — leverages the existing projector + scenario channel.

**Per-slice spec text addition** (each of §S2/§S3/§S4 gains):

> **Connect-time delivery.** Synchronous catch-up via `ScenarioSystem.SendScenariosToClient` from `HandshakeSystem.HandleHandshakeRequest` ([M11](#m11--synchronous-connect-time-catch-up-for-s2s3s4-d2-implementation)). Owner receives projected per-agency state at handshake-complete; no 30s window of shared-baseline observation.

---

## Slice-by-slice impact summary

| Slice | Design decisions | Mechanical rebases | Spec gaps (real) |
|---|---|---|---|
| S1 | None (hook point unchanged) | M10 | None |
| S2 | D1 (Path B), D2 (sync catch-up), D3 (migrate with vessel) | M1–M9, M11 | D3 |
| S3 | D1, D2 | M1–M7, M11 | None |
| S4 | D1, D2 | M1–M7, M11 | None |
| S5 | None (sidecar) | None | None |
| S6 | None (sidecar) | None | None |

---

## Application protocol

When `feature/per-agency-mks` merges into `feature/per-agency`:

1. **D1, D2, D3 are pre-ratified.** Apply their text additions inline.
2. **Apply M1–M11** as a single doc-only commit to [implementation-spec.md](implementation-spec.md). No code changes in this commit (M11 is a *plan* for code; the code itself lands as part of S2/S3/S4 implementation).
3. **Re-ratify the parent spec** at its new date in a closing block. Update [implementation-spec.md:3](implementation-spec.md#L3) (the "Status" line) and [implementation-spec.md:308-316](implementation-spec.md#L308-L316) (the Tracking table).
4. **Delete this addendum** OR move it to `docs/mod-compat/archive/` for historical record. Once applied, the addendum has no ongoing value.

---

## Tracking

| Item | Status |
|---|---|
| Verification grounding complete | ✅ 2026-05-18 |
| D1 (Path B for all three slices) | ✅ ratified 2026-05-18 |
| D2 (Synchronous connect-time catch-up) | ✅ ratified 2026-05-18 |
| D3 (transferagency migration for S2 Scanners — Option A migrate-with-vessel) | ✅ ratified 2026-05-18 |
| M1–M11 (mechanical rebases + M11 catch-up plan) | ⏳ ready to apply post-MKS-merge |
| MKS merge into feature/per-agency | ⏳ pending (per `feature/per-agency-mks` Phase 3 completion) |

**Out of scope for this addendum**:
- S5 (`[x]` Science) and S6 (KIS) sidecar Harmony work — fully independent of the MKS merge; ship anytime.
- LunaCompat sidecar source verification — not present on the local dev machine; requires either a clone or WebFetch authorization.
- Multi-player-per-agency design considerations — explicitly out of scope per the Operating Rule.
