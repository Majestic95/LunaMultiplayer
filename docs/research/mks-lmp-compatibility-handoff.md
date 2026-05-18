# MKS × Luna Multiplayer — full implementation handoff

**Document version:** 3.2 (external-codebase audit corrections applied, 2026-05-18)  
**Companion:** visual summary in Cursor — `canvases/mks-lmp-compatibility.canvas.tsx` (same machine; open beside this file).

**Scope:** MKS, USITools, Community Resource Pack (CRP) vs Luna Multiplayer on `feature/per-agency`. **Out of scope:** USI-LS and other USI suite mods until this track is green.

**Second-pass audit:** A 2026-05-18 external re-audit produced 12 corrections. They are **merged into the body of this document** (correct final plan only—no parallel “old vs new” narrative). The opening **Executive summary** names them in one paragraph.

---

## 1. Executive summary

Vanilla LMP + MKS + USITools + CRP **installs and runs**; it is **not playable** as MKS without fork work: Local Logistics mutates other players’ vessel resources without matching network authority; kolony research and shared scenario state race under LMP subspaces and 30-second scenario hash sync; WOLF and long-horizon converter catch-up remain nondeterministic across peers.

**Effort (revised after audit):** **Playable co-op** ≈ Phases 0–3 — **10–12 weeks** one strong LMP contributor. **Fully compatible** ≈ Phases 0–5 — **4–5 months**.

**Second-pass audit (12 items, single line each):** (1) R0 fix must filter **depot** vessels, not gate on consumer. (2) Orbital `ModuleLogisticsConsumer` calls are a **documented non-issue** (`!LandedOrSplashed`). (3) Phase 0 must use **MandatoryPlugins** + no optional plugin bypass or SHA never applies. (4) Treat **unclaimed Update lock** as skip in MP, not solo. (5) ShareKolony send path must respect **ScenarioSystem** main-thread save vs **TaskFactory** send. (6) Allocate **Lidgren channels** by grepping `DefaultChannel =>` literals across `LmpCommon/Message/Server/*SrvMsg.cs` / `Client/*CliMsg.cs` — **no central `MessageChannelType.cs` exists** (audit correction, v3.1). High-water at base SHA `8f609963`: server **22**, client **21**; `AgencyVisibilityMsgData` already shipped at this base (`AgencySrvMsg.cs:50`) so the original `eb4ef6e2` coordination flag is moot — only watch for further in-flight 5.18d slots. (7) **KolonizationManager** lifecycle unverified—read file before Phase 3. (8) **WOLF GetNewFlightNumber** implementation unverified—read `ScenarioPersister.cs` before Phase 4 / upstream PR. (9) Phase 1.5 acceptance must include **Update lock handoff** and `lastUT`/`lastCheck` interaction. (10) CRP **localization** is optional Phase 0 check. (11) Phase 3 effort **~2×** original (closer to contract/R&D routing complexity than trivial Share*). (12) **Rebase discipline** against moving `feature/per-agency` (audit tip `67b0e01c` vs later head e.g. `b50860e6`); branch from **current** `feature/per-agency` after pull.

---

## 2. Kickoff prompt (paste into a new agent)

You are implementing MKS + USITools + CRP compatibility on our **Luna Multiplayer fork** at `F:\luna-multiplayer`.

**Before writing code:**

1. Read `F:\luna-multiplayer\CLAUDE.md` (Strategy B / proto budget; Stage 5.17c scenario projection; 5.17d agency routing; `lmpAuthSubspace`; fork policy).
2. Read this file end-to-end once.
3. Open `mks-lmp-compatibility.canvas.tsx` in the IDE for the same material in layout form (optional refresher).

**Branching:** Work on **`feature/per-agency-mks`**, created from **`feature/per-agency` after `git pull`** (record the base SHA in the progress log). Do **not** target `master`. Do **not** edit `feature/per-agency` directly. Treat **`Server/System/Agency/`** as load-bearing—coordinate before structural changes.

**Critical engineering fact:** `ModuleLogisticsConsumer` writes to **other vessels’** `PartResource.amount`. The fix is **not** “only run FixedUpdate if I hold the consumer’s Update lock.” It **is** “only consider depots/power hubs the local player **Update-locks**,” via postfix on `GetResourceStockpiles()` and `GetPowerDistributors()` (and equivalents if refactored). In multiplayer, **do not** treat empty Update-lock owner as permission to pump.

**Phases:** Execute Phase 0 → 1 → 1.5 → 2 → 3 in order; Phase 4–5 per product goals. After each phase, run that phase’s acceptance checks and append **§15 Progress log**.

**Out of scope:** USI-LS — separate brief later.

---

## 3. Sources and pinning

| Artifact | Location / branch | Note |
|----------|-------------------|------|
| Artifact | Location / branch | Pinned SHA (audit 2026-05-18) |
|----------|-------------------|-------------------------------|
| LMP fork | `F:\luna-multiplayer-mks` (worktree, branch `feature/per-agency-mks`) | base: `8f609963` (Stage 5.18d slice g, parent moves — re-base before merge) |
| MKS (incl. WOLF) | `github.com/UmbraSpaceIndustries/MKS` (`main`) | `ed0f6aa6047a34e1d0b6bfd6e26c0aa58b363cec` |
| USITools | `github.com/UmbraSpaceIndustries/USITools` (`main`) | `4ad5cdd867689a2bd56b66b6a18bc89110651765` |
| CRP | `github.com/BobPalmer/CommunityResourcePack` (`master`) | `9e933150f455844e2e40fce9e7c64d29c763acfa` (data only) |

External clones live at `F:\tmp\mks-external\{MKS,USITools,CommunityResourcePack}` (research-only — not part of either luna worktree). Remote branch tips move; re-pin if any phase implementation lands more than a month after the audit date.

---

## 4. What LMP provides (single reference)

| Mechanism | Path | MKS relevance |
|-----------|------|----------------|
| **PartSync (ModuleStore)** | `LmpClient/ModuleStore/FieldModuleStore.cs`, `Patching/PartModulePatcher.cs` | XML lists `PartModule` fields/methods; Harmony patches **`OnUpdate` / `OnFixedUpdate` / `FixedUpdate` / `Update` / `LateUpdate` / `KSPAction` / `KSPEvent` (guiActive filter)** (`PartModulePatcher.cs:38-40` — audit correction v3.1, surface is wider than MD originally claimed). **Parent XML propagates to subclasses** (`AddParentsCustomizations` / `GetChildCustomizations` — `FieldModuleStore.cs:79-96` direct-child, `103-113` parent recursive). **GAP — child propagation is one level only:** `Squad/BaseConverter.xml` does NOT auto-cover `USI_Converter` because `USI_Converter` extends `ModuleResourceConverter` (a grand-child of `BaseConverter`), and no `ModuleResourceConverter.xml` bridge exists in tree. Phase 1 must author this bridge before any USI converter fields will sync. |
| **ScenarioSystem** | `LmpClient/Systems/Scenario/ScenarioSystem.cs`, `LmpCommon/IgnoredScenarios.cs` | ~30 s SHA delta on scenario text; **last-writer-wins** across peers. **`Save`/main thread vs send often offloaded** — see Phase 3. |
| **VesselResourceSystem** | `LmpClient/Systems/VesselResourceSys/` | ~2.5 s proto resource sync for **active** vessel; defines interaction with cross-vessel mutations (R0). |
| **VesselProtoSys** | `LmpClient/Systems/VesselProtoSys/` | Full vessel `ConfigNode` sync; thread/async concerns; budget for expensive reloads. |
| **Lock system** | `LmpClient/Systems/Lock/` | `LockSystem.LockQuery.GetUpdateLockOwner(vesselId)` — **authority for Phase 1.5 and stats writers**. |
| **Mod control** | `LmpClient/Systems/Mod/ModSystem.cs`, `ModFileHandler.cs` | `LMPModControl.xml`: parts, resources, DLL SHA. **Mandatory vs optional** matters — §7 Phase 0. |
| **Stage 5 agency** | `Server/System/Agency/*` | `AgencyScenarioProjector`, `AgencyContractRouter`, `AgencyState` — **pattern for Phase 3–4** (projection, routing, persistence under `Universe/Agencies/`). |

---

## 5. Mod side (condensed)

### 5.1 CRP

- **No DLLs, no C#, no PartModules** — `RESOURCE_DEFINITION`, biome configs for scanners, localization `.cfg`.
- **Compatibility:** mod list + resource allow list only.
- **Optional Phase 0 check:** two clients, two KSP locales, confirm CRP display names resolve (LMP does not hash localization keys).

### 5.2 USITools (load-bearing)

| Component | Role | LMP note |
|-----------|------|----------|
| `USI_Converter` | Extends `ModuleResourceConverter` → `BaseConverter` | Inherits `BaseConverter.xml`; add XML mainly for **swap / recipe selection** if not covered. |
| `ModuleLogisticsConsumer` | `FixedUpdate:54` → `CheckLogistics:128` → `FetchResources:225` / `PushResources:213` | **R0:** writes **remote** `PartResource.amount` verified at lines 326/416/495 (audit v3.2). **`CheckLogistics:131` returns early if `!this.vessel.LandedOrSplashed`** — gates on CONSUMER vessel only (orbital consumer never pumps; landed consumer can still reach other landed depots). `GetResourceStockpiles:234` and `GetPowerDistributors:283` are R0 fix anchors — **`public` but NOT `virtual`** (Harmony postfix works today but breaks silently on USI signature refactor — see §11). |
| `LogisticsTools.GetNearbyVessels` | Scans `FlightGlobals.Vessels` | Position list from LMP; watch pinned/immortal vessels. |
| `USI_GlobalBonuses` | In-memory singleton; **Verified:** no scenario persistence in inspected source | If `KolonizationScenario` is authoritative, clients can rebuild. |
| Warehouses / swap bays / power couplers | PartModules with toggles | PartSync XML in Phase 1 as needed. |

**Audited v3.2:** `Source/KolonyTools/Kolonization/KolonizationManager.cs` is a **write-through cache** — singleton lazy `GameObject` spawn (line 19), `_KolonizationInfo` hydrated from `KolonizationScenario.Instance.settings.GetStatusInfo()` on first read (line 37); every mutation funnels back through `KolonizationScenario.Instance.settings.SaveLogEntryNode` / `DeleteStatusNode` (lines 58, 113). `TrackLogEntry:90` IS the Phase 3 Harmony-postfix mutation hook MD originally hypothesised. **"Scenario-only is enough" claim CONFIRMED** for this subsystem.

### 5.3 MKS (where state lives)

| Subsystem | Persistence | Default LMP path | Playable without fork? |
|-----------|-------------|------------------|-------------------------|
| `KolonizationScenario` | Scenario | 30 s SHA | **No** — double-count / merge with R1 |
| `PlanetaryLogisticsScenario` | Scenario; `PlanetaryLogisticsManager` is write-through cache (audited v3.2) | 30 s SHA | **No** — concurrent writers (R2 scenario-side) |
| `ScenarioOrbitalLogistics` | Scenario **+ per-frame resource mutation** | 30 s SHA + direct part writes | **No** — `Update():150-165` fires `transfer.Deliver()` (`OrbitalLogisticsTransferRequest.cs:286`) when `transfer.GetArrivalTime() <= Planetarium.GetUniversalTime()`; each peer's UT divergence under LMP subspaces produces independent deliveries → double-spend/double-receive. Mutation site `vessel.ExchangeResources` (`OrbitalLogisticsExtensions.cs:288/292/302/306` loaded; `:237/241/252/256` unloaded protoVessel). **R2 expanded to Local-Logistics-class authority surface (audit v3.2)** — not just scenario sync. |
| `WOLF_ScenarioModule` / `ScenarioPersister` | Scenario | 30 s SHA | **No** — global registry (R4) |
| `MKSModule`, converters, drills | Part + `vessel.lastUT` | PartSync + proto | **Partial** — needs XML + R1/R3 |
| `ModuleOrbitalLogistics` (part) | Part + events | PartSync + calls | **Needs** KSPEvent/RPC coverage |
| Survey / lode | Part / spawn | Proto + PartSync | Race-sensitive |

**Audited v3.2:** `Source/WOLF/WOLF/ScenarioPersister.cs:23-32` holds 5 lists (`CrewRoutes`, `Depots`, `Hoppers`, `Routes`, `Terminals`). ID schemes (per follow-up audit): `Depot` composite key `(Body, Biome)` strong; `Route` composite key `(OriginBody, OriginBiome, DestBody, DestBiome)` strong; `Hopper.cs:18` `Guid.NewGuid().ToString()` strong; `Terminal.cs:15` `Guid.NewGuid().ToString("N")` strong; `CrewRoute.cs:90` has BOTH a strong `UniqueId` (Guid) AND a weak display-only `FlightNumber` from `GetNewFlightNumber:191-214` (3-char namespace, 10 retries, silently returns colliding value). **4 of 5 entities are safe by construction**; only `FlightNumber` is weak and display-only. Per-agency partition (Phase 4) can rely on existing strong IDs.

---

## 6. Risks and mitigations (final form)

### R0 — Cross-vessel Local Logistics (P0)

**Failure:** Consumer on client A mutates warehouse on client B; `VesselResourceSystem` reverts A's view every ~2.5 s → oscillation and duped/negative economy.

**End-to-end trace (verified v3.2):** A holds Update lock on A; B holds Update lock on B. A's `ModuleLogisticsConsumer.FixedUpdate` → `CheckLogistics` (LandedOrSplashed-on-A only) → `GetResourceStockpiles` returns ALL nearby vessels including B → `FetchResources` writes `pr.amount -= demand` on B's parts locally on A. B's 2.5s `VesselResourceMessageSender` pulse arrives at A → `VesselResourceMessageHandler:19-20` does NOT early-return (B is not A's controlled vessel) → queue applies → A's view of B reverts. A's next consumer FixedUpdate writes again → oscillation. **No per-part ownership filter exists in `VesselResourceSystem`** — fix MUST happen upstream in the stockpile enumeration.

**Wrong fix (superseded):** Harmony prefix on consumer `FixedUpdate` keyed only on **consumer** Update lock — **still mutates other players' depots**.

**Correct fix:** Postfix **`GetResourceStockpiles()`** and **`GetPowerDistributors()`** on `ModuleLogisticsConsumer`: return lists **filtered** to vessels where `GetUpdateLockOwner(v.id) == local player` **when** `MainSystem.NetworkState == ClientState.Running`. **Never** treat "no lock owner" as go-ahead in MP (transient after unload — audit finding). **Brittleness:** both anchors are `public` but NOT `virtual` (`ModuleLogisticsConsumer.cs:234,283`) — postfix works today but a USI signature refactor breaks it silently. Pursue the §11 upstream `virtual`/`protected` seam PR alongside the local fix.

**Orbital `ModuleLogisticsConsumer`:** `!LandedOrSplashed` early exit — out of scope for R0; document in tests. **Distinct from orbital LOGISTICS (R2 — `ScenarioOrbitalLogistics.Update` → `transfer.Deliver`)** which IS a resource-mutation surface — do not conflate.

### R1 — Subspace × time-based kolony (P0)

**Failure:** `Planetarium.GetUniversalTime()` / `lastCheck` diverge across `WarpSystem` subspaces; same kolony accrues twice; scenario sync merges badly.

**Mitigations:** Phase 2 kolony-radius **subspace join** (extend spectator-style warp); **and/or** single writer for stats (Update-lock holder); long-term server tick if needed.

### R2 — Shared scenario modules + orbital-logistics resource mutation (P0, expanded v3.2)

**Failure (scenario-sync side):** `PlanetaryLogistics`, kolony scenario, WOLF registry under **snapshot SHA** → lost updates, duplicate transfers, corrupt warehouses.

**Failure (resource-mutation side, audit v3.2):** `ScenarioOrbitalLogistics.Update():150-165` runs on every client every frame. Pending `OrbitalLogisticsTransferRequest` instances fire `Deliver()` (`:286`) when `transfer.GetArrivalTime() <= Planetarium.GetUniversalTime()` (`:189`). Under LMP subspaces every peer has its own UT, so every peer's `Update` runs `Deliver` independently → `vessel.ExchangeResources` writes `PartResource.amount` (loaded vessels: `OrbitalLogisticsExtensions.cs:288/292/302/306`) or `ProtoPartResourceSnapshot.amount` (unloaded: `:237/241/252/256`). Result: each client double-spends and double-receives; vessel resource state diverges across peers; ScenarioSystem's 30s SHA pass then compounds the drift.

**Mitigation:** Phase 3 **ShareKolony** for the scenario-sync side: add affected modules to `IgnoredScenarios`; **idempotent mutation messages**; server fan-out + persistence + per-scenario ingestion adapters (modelled on `ScenarioSystem.cs:228-303` per-module hooks, not "reconciliation" — audit v3.1). Effort comparable to contract/R&D routing + `AgencyResearchRouter`, not trivial Share*.

**Plus Phase 2 / Phase 3 orbital-logistics authority gate:** even on a common subspace, every peer's `Update` still fires `Deliver`. Must gate `Deliver()` itself — only the Update-lock holder of the destination vessel (or a server-elected coordinator) executes; others observe. Treat as R0-class fix on the `ProcessTransfers` / `Deliver` boundary. **The protoVessel write path (unloaded branch) is unique to this surface** — R0's `GetResourceStockpiles` filter does NOT cover unloaded vessels; the orbital fix must handle both.

### R3 — Unloaded converter catch-up (P1)

**Failure:** First loader runs `BaseConverter` catch-up; others see stale `vessel.lastUT` / resources. Tied to R1 and **lock handoff** (audit).

**Mitigation:** Phase 5 — proto update path / `UpdateProtoInPlace` / post-catch-up publish (see CLAUDE.md Strategy B). Design together with Phase 1.5 acceptance (lock transfer + `lastCheck`).

### R4 — WOLF global registry (P1, refined v3.2)

**Failure:** Concurrent depot/route creation under shared scenario without single writer.

**Audit v3.2 — ID concerns were overstated.** 4 of 5 entity types are collision-safe by construction: `Depot` (composite `(Body, Biome)`), `Route` (composite 4-tuple), `Hopper` (`Guid.NewGuid()`), `Terminal` (`Guid.NewGuid("N")`). Only `CrewRoute.FlightNumber` is weak (3-char namespace, silent collision fallback at `ScenarioPersister.cs:213`) **and it's display-only** — `CrewRoute.UniqueId` is also a Guid (`CrewRoute.cs:90`). Per-agency partition can use existing strong IDs as-is; do NOT need to design a wider ID scheme upfront.

**Mitigation:** Phase 4 — **per-agency** WOLF state (reuse Stage 5 `Universe/Agencies/...` patterns + the 4 band-1 routers as templates, see §10 item 7). The remaining real WOLF risk is **concurrent mutation of the shared `ScenarioPersister.CrewRoutes` / `Depots` / etc. lists** under the existing 30s scenario SHA — that's the same authority problem as R2 scenario-sync side, solve identically. Upstream `FlightNumber` PR is a tiny display-quality polish, not a correctness blocker.

### R5 — UI / lifecycle polish (P2)

**Failure:** Extra `OnGUI` or helpers draw during LMP spectate / wrong scene.

**Mitigation:** Harmony early-out on known GUI entry points (e.g. orbital logistics UI). `KolonizationManager` lifecycle confirmed (audit v3.2): lazy `GameObject` add via `_KolonizationInfo` first-read trigger (`KolonizationManager.cs:19,37`); no extra spawn complexity.

---

## 7. Phases, deliverables, acceptance

### Phase 0 — Install + enforce mod parity (~1 day)

**Do:**

1. Install MKS, USITools, CRP (+ FireSpitter etc. per MKS).
2. Generate `LMPModControl.xml` from client (**ModSystem**).
3. **Operator:** move USI / MKS / CRP (and required deps) from **`OptionalPlugins` to `MandatoryPlugins`**; set server so **non-listed / wrong SHA clients are rejected** (`AllowNonListedPlugins` off as appropriate). *Without this, the SHA story in research does not hold.*
4. Two clients connect; place a part with a converter; toggle once; scan logs for exceptions.

**Accept:** Handshake clean; resources like Substrate / MaterialKits present; no `KolonyTools`/`USITools` NRE spam.

**Optional:** Two locales — CRP strings resolve on both clients.

---

### Phase 1 — PartSync XML (~6–8 days, revised v3.1)

**Do:**

1. **Author `LmpClient/ModuleStore/XML/Squad/ModuleResourceConverter.xml`** (or equivalent per-USI-type XML) to bridge the `BaseConverter` → `ModuleResourceConverter` → `USI_Converter` inheritance gap. **Single-level propagation from `BaseConverter.xml` does NOT reach grand-children** (audit v3.1, see §4) — without this bridge, no converter fields sync at all. This was the load-bearing surprise from the v3.1 LMP-side audit; original MD assumed inheritance covered USI converters for free.
2. Add XML under `LmpClient/ModuleStore/XML/USI/` for **non-inherited** fields: `MKSModule` (`Governor`, etc.), swap selection, warehouses toggles, logistics `lastCheck` (if still useful after 1.5), orbital logistics events, lodes.

**Accept:** Remote player sees toggles / governor / recipe within ~1 s; log shows PartSync field events; **converter recipe and state changes propagate across clients** (this is the real validation that the bridge XML works — original "verify in log that USI_Converter receives BaseConverter patches" was checking for something that fails by default).

---

### Phase 1.5 — Depot list authority (~1.5 weeks)

**Do:** Implement **R0 correct fix** (postfix filter on stockpile/power lists; conservative unclaimed behavior in MP). Unit tests: owner pumps; non-owner does not; **lock handoff** during pump does not double-spend; document orbital non-path.

**Accept:** Two clients, one warehouse one consumer, **no resource oscillation** on warehouse; no cross-owner `FetchResources` errors attributed to pump.

---

### Phase 2 — Kolony subspace policy (~1 week)

**Do:** When active vessel enters kolony radius near another player’s controlled kolony, **sync subspace** similar to spectator-to-controller behavior (`WarpSystem`).

**Accept:** Second client’s UT/subspace matches kolony owner after approach; solo unaffected.

---

### Phase 3 — ShareKolony (~6–8 weeks, revised)

**Do:**

1. `IgnoredScenarios` for kolony / logistics / WOLF scenarios targeted by this phase (exact list = product decision: kolony + planetary + orbital minimum; WOLF may move to Phase 4 only—avoid double work).
2. Message types + client handler + **server** persistence + reconnect catch-up.
3. **Threading:** mutation hooks (Harmony postfixes on e.g. `TrackLogEntry`) run on **main thread**; **do not** call raw send from postfixed code if `ScenarioSystem` uses **async send** — use a **queue drained on a safe thread** with locking pattern analogous to **`VesselProtoMessageSender` / `VesselArraySyncLock`**.
4. **Channels:** **no central `MessageChannelType.cs` exists** (audit correction, v3.1) — grep `DefaultChannel =>` across `LmpCommon/Message/Server/*SrvMsg.cs` and `Client/*CliMsg.cs` (~22 files each direction) to enumerate allocations. At base SHA `8f609963` high-water marks are server **22** / client **21**; pick unused IDs above those and re-check against any in-flight 5.18d slots before allocation.

**Accept:** Concurrent orbital/planetary ops without 30 s clobber; reconnect loads consistent state; stress test 30+ minutes two-client.

---

### Phase 4 — WOLF per-agency (~2–3 weeks)

**Do:** Persist and route WOLF blobs per agency (mirror `AgencyContractRouter` / projector patterns). Read `ScenarioPersister.cs` before designing ID strategy.

**Accept:** Two agencies, same body — isolated WOLF graphs; gate off returns to Phase 3 shared behavior per Stage 5 dual-mode rules.

---

### Phase 5 — Deterministic catch-up (~3–4 weeks, optional product)

**Do:** Server- or agreed-authority path so **post-catch-up** resources/proto match across peers; coordinate with Strategy B proto budget.

**Accept:** Long warp + two loaders → **identical** resource totals on shared vessel proto.

---

## 8. Reuse from this fork (one paragraph)

Stage 5 **agency projection and routing** is the template for kolony scenario ownership — 7 templates total (see §10 item 7), including the 4 band-1 routers (`AgencyCurrencyRouter`/`Tech`/`Research`/`Progress`). **BUG-010 / vessel pin** contributes the *vessel-scoped registry + lock-acquire-clears-it* structural pattern (`VesselPinnedSystem.cs` `ConcurrentDictionary<Guid,string>` + `LockEvent.onLockAcquire` hook); the immortal-flip semantics do NOT transfer ("freeze, no writers" ≠ kolony's "one writer, others read") — audit v3.2 framing correction. **Strategy B / UpdateProtoInPlace** is the right hook surface for catch-up. Do not rebuild these patterns—extend them.

---

## 9. Decisions and escalations

| Topic | Recommendation |
|-------|----------------|
| **Branch** | `feature/per-agency-mks` from **latest pulled** `feature/per-agency`; weekly rebase while Stage 5.18 moves; freeze a SHA for soak milestone |
| **Playable vs full** | Phases 0–3 first; 4–5 if multi-agency WOLF and long offline matter |
| **Upstream** | Small PRs (persistent fields, safer WOLF IDs) worth asking USI; don’t block fork |
| **USI-LS** | Separate track after 0–3 green |

---

## 10. Required reading (ordered)

1. `CLAUDE.md`
2. `LmpClient/ModuleStore/FieldModuleStore.cs` + `ModuleStore/Patching/PartModulePatcher.cs`
3. `LmpCommon/IgnoredScenarios.cs` + `LmpClient/Systems/Scenario/ScenarioSystem.cs` — region 228-303 is **per-module ingestion adapters** (audit v3.2 framing fix — original MD called it "reconciliation"): inject server contracts into `ContractPreLoader`, migrate `CONTRACTS_FINISHED → CONTRACTS`, strip missing-parts contracts, prepare unavailability stubs. Each MKS scenario module (KolonizationScenario / PlanetaryLogistics / OrbitalLogistics) likely needs its own bespoke ingest adapter of similar shape (~25-30 lines each). Also note `TaskFactory.StartNew` send pattern at line 102 (offloaded send) vs main-thread `ParseModulesToConfigNodes` at line 153-156 (Lingoona-crash if off-thread).
4. `LmpClient/Systems/VesselResourceSys/` (sender + apply path)
5. `LmpClient/Systems/VesselProtoSys/VesselProtoMessageSender.cs` (locking / thread offload)
6. `LmpClient/Systems/Lock/*`
7. `Server/System/Agency/` — 7 templates total (skim for shape, not copy-paste): `AgencyScenarioProjector.cs`, `AgencyContractRouter.cs`, `AgencyState.cs`, plus the four Stage 5.17e band-1 routers (`AgencyCurrencyRouter.cs`, `AgencyTechRouter.cs`, `AgencyResearchRouter.cs`, `AgencyProgressRouter.cs`) — audit v3.1 surfaced the four band-1 routers as equally applicable Phase 3 templates.
8. **AUDITED v3.2** — USITools: `Source/USITools/Logistics/ModuleLogisticsConsumer.cs` (R0 root cause confirmed; non-virtual brittleness flagged). Pinned SHA `4ad5cdd8`.
9. **AUDITED v3.2** — MKS: `KolonizationManager.cs` (write-through cache, `TrackLogEntry:90` is Phase 3 hook), `KolonizationScenario.cs`, `MKSModule.cs` (`Governor:24-25` is PartSync target; R3 hit confirmed at `Update():76-95`), `ScenarioOrbitalLogistics.cs` (resource-mutation surface, R2 expanded), `PlanetaryLogisticsScenario.cs` (mirrors Kolonization shape), `PlanetaryLogisticsManager.cs` (write-through, follow-up audit). Pinned SHA `ed0f6aa6`.
10. **AUDITED v3.2** — WOLF: `ScenarioPersister.cs` (singleton DI, 5 entity lists, 4 of 5 IDs strong, `FlightNumber` weak-but-display-only), plus `Depot.cs` / `Route.cs` / `HopperMetadata.cs` / `TerminalMetadata.cs` / `CrewRoute.cs` ID schemes (follow-up audit). Pinned SHA `ed0f6aa6` (WOLF lives inside the MKS repo at `Source/WOLF/WOLF/`).
11. ~~`LmpCommon/Message/Types/MessageChannelType.cs`~~ — **file does not exist** (audit correction, v3.1). Channel IDs live as inline `DefaultChannel =>` literals in each `LmpCommon/Message/Server/*SrvMsg.cs` / `Client/*CliMsg.cs`. Grep across both directories to enumerate.

---

## 11. Optional upstream / small patches

- `[KSPField(isPersistant = true)]` on PartSync-needed fields if missing.
- **`virtual`/`protected` seam on `ModuleLogisticsConsumer.GetResourceStockpiles` and `GetPowerDistributors`** (`ModuleLogisticsConsumer.cs:234,283`) — currently `public` but NOT `virtual`. **Audit v3.2: this is JUSTIFIED, not optional.** Harmony postfix anchors the R0 fix on these methods; a USI signature refactor breaks the fix silently. Land the local postfix alongside an upstream PR; do not assume the seam will hold indefinitely.
- WOLF `FlightNumber` generation hardening (`ScenarioPersister.cs:191-214`) — **display-only polish**, low priority. The 3-char namespace silently returns colliding values after 10 retries (`:213`). `CrewRoute.UniqueId` is a Guid, so this is not a correctness blocker, just operator UX.

---

## 12. Explicitly not covered here

- USI-LS, Kerbalism, RemoteTech-style gameplay mods  
- Load testing at 20+ MKS parts × many clients  
- KSP version other than fork’s stated target (currently 1.12-class)  

---

## 13. Version / wire bump

Any new **message types or channel** usage: follow repo rules for **LmpVersioning** / protocol bump (Stage 5.18 may already be moving **0.30 → 0.31** — align with `feature/per-agency`).

---

## 15. Progress log _(append only)_

| Date | Phase | Base `feature/per-agency` SHA | Result / notes |
|------|-------|------------------------------|----------------|
| 2026-05-18 | Research | `67b0e01c` (doc); re-audit head ref `b50360e6` | Handoff v3.0 published |
| 2026-05-18 | Branch created | `8f609963` (Stage 5.18d slice g — `/deleteagency --confirm`) | `feature/per-agency-mks` created as label only (no checkout) off local `feature/per-agency`. Local-only — not pushed. Protocol at `v0.31.0-per-agency-private-1`. Stage 5.18d still in flight on parent branch; Phases 0/1/1.5/2 cleared to start; Phases 3/4 should wait for 5.18d to land or coordinate per §6 / §7 (wire enum slots, `IgnoredScenarios`, `Server/System/Agency/*` shape, `LmpVersioning`). |
| 2026-05-18 | Audit (LMP side) | `8f609963` | LMP-side §10 audit pass (items 2-7 + 11) by subagent against `F:\luna-multiplayer-mks`. **1 design correction** (USI_Converter inheritance gap → Phase 1 expanded from 3-5d to 6-8d, must author `ModuleResourceConverter.xml` bridge). **3 fact corrections** (phantom `MessageChannelType.cs` removed, `AgencyVisibilityMsgData` already shipped at this base, `PartModulePatcher` patches `FixedUpdate`/`Update`/`LateUpdate` too). **2 bonus discoveries** (4 additional Agency routers — `AgencyCurrencyRouter`/`Tech`/`Research`/`Progress` — as Phase 3 templates; `LockQuery.GetUpdateLockOwner` method body lives in `LmpCommon/Locks/LockQueryUpdate.cs:39`, not `LmpClient`). External clones (USITools / MKS / WOLF, §10 items 8-10) **still pending** — required prereq for R0 / Phase 3 / Phase 4 design validation. MD bumped 3.0 → 3.1. |
| 2026-05-18 | Audit (LMP secondary) | `8f609963` | Foreground reads on apply-side surfaces: `VesselResourceMessageHandler.cs:19-20` confirms no per-part ownership filter — R0 fix MUST happen upstream in `ModuleLogisticsConsumer.GetResourceStockpiles`. `ScenarioSystem.cs:228-303` is per-module ingestion adapters, not "reconciliation" — §10 item 3 framing fixed. `VesselPinnedSystem.cs` is "freeze, no writers" not "one writer, others read" — §8 BUG-010 mapping softened to "vessel-scoped registry + lock-acquire-clears-it pattern only." |
| 2026-05-18 | Audit (external) | external clones at SHAs above | External-codebase pass on §10 items 8/9/10 by subagent against MKS `ed0f6aa6` + USITools `4ad5cdd8` + CRP `9e933150f`. **3 design escalations:** (1) `ScenarioOrbitalLogistics` is per-frame resource-mutation surface, NOT just scenario-sync (`Update():150-165` → `transfer.Deliver()` → `vessel.ExchangeResources` writes `PartResource.amount`/`ProtoPartResourceSnapshot.amount`); R2 expanded to Local-Logistics class. (2) `ModuleLogisticsConsumer.GetResourceStockpiles/GetPowerDistributors` are `public` but NOT `virtual` — Harmony postfix brittle on signature refactor; §11 upstream PR justified, not optional. (3) WOLF ID concerns OVERSTATED — only `CrewRoute.FlightNumber` is weak (display-only), 4 of 5 entities have strong IDs by construction; Phase 4 partition can rely on existing IDs. **Confirmations:** R0 mechanism + 3 mutation paths verified; `KolonizationManager` and `PlanetaryLogisticsManager` both write-through caches (scenario-only sufficient); `TrackLogEntry` IS Phase 3 hook; `MKSModule` R3 hit confirmed; `ModuleOrbitalLogistics` is 1 persistent field + 1 UI event (Phase 1 XML is tiny); CRP 170 RESOURCE_DEFINITIONs, no DLLs/C#; `USI_Converter` 2-hop inheritance confirms v3.1 bridge requirement. MD bumped 3.1 → 3.2. **Still uncertain:** R5 GUI entry-point enumeration (Phase 5 polish, low priority). |
| | | | |

---

_End of handoff._
