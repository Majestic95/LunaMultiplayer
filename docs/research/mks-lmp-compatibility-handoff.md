# MKS × Luna Multiplayer — full implementation handoff

**Document version:** 3.1 (LMP-side §10 audit corrections applied, 2026-05-18)  
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
| LMP fork | `F:\luna-multiplayer` | Pin **your** `feature/per-agency` SHA when you start |
| MKS | `github.com/UmbraSpaceIndustries/MKS` (`main`) | Pin SHA for implementation |
| USITools | `github.com/UmbraSpaceIndustries/USITools` (`main`) | Same |
| CRP | `github.com/BobPalmer/CommunityResourcePack` (`master`) | Data only |

Remote branch tips move; **record SHAs in §15** when you begin implementation.

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
| `ModuleLogisticsConsumer` | `FixedUpdate` → `CheckLogistics` → `FetchResources` / `PushResources` | **R0:** writes **remote** `PartResource.amount`. **`CheckLogistics` returns early if `!vessel.LandedOrSplashed`** — orbital pattern does not hit R0; not a miss, explain in tests. |
| `LogisticsTools.GetNearbyVessels` | Scans `FlightGlobals.Vessels` | Position list from LMP; watch pinned/immortal vessels. |
| `USI_GlobalBonuses` | In-memory singleton; **Verified:** no scenario persistence in inspected source | If `KolonizationScenario` is authoritative, clients can rebuild. |
| Warehouses / swap bays / power couplers | PartModules with toggles | PartSync XML in Phase 1 as needed. |

**Prerequisite read (unverified):** `Source/KolonyTools/Kolonization/KolonizationManager.cs` — confirm whether **any state** exists outside `KolonizationScenario` that Phase 3 must sync. Until then, do not assume “scenario-only is enough.”

### 5.3 MKS (where state lives)

| Subsystem | Persistence | Default LMP path | Playable without fork? |
|-----------|-------------|------------------|-------------------------|
| `KolonizationScenario` | Scenario | 30 s SHA | **No** — double-count / merge with R1 |
| `PlanetaryLogisticsScenario` | Scenario | 30 s SHA | **No** — concurrent writers (R2) |
| `ScenarioOrbitalLogistics` | Scenario | 30 s SHA | **No** — time + subspace (R2) |
| `WOLF_ScenarioModule` / `ScenarioPersister` | Scenario | 30 s SHA | **No** — global registry (R4) |
| `MKSModule`, converters, drills | Part + `vessel.lastUT` | PartSync + proto | **Partial** — needs XML + R1/R3 |
| `ModuleOrbitalLogistics` (part) | Part + events | PartSync + calls | **Needs** KSPEvent/RPC coverage |
| Survey / lode | Part / spawn | Proto + PartSync | Race-sensitive |

**Prerequisite read (unverified):** `Source/WOLF/WOLF/ScenarioPersister.cs` — confirm ID generation; optional upstream GUID PR only if still weak after read.

---

## 6. Risks and mitigations (final form)

### R0 — Cross-vessel Local Logistics (P0)

**Failure:** Consumer on client A mutates warehouse on client B; `VesselResourceSystem` reverts A’s view every ~2.5 s → oscillation and duped/negative economy.

**Wrong fix (superseded):** Harmony prefix on consumer `FixedUpdate` keyed only on **consumer** Update lock — **still mutates other players’ depots**.

**Correct fix:** Postfix **`GetResourceStockpiles()`** and **`GetPowerDistributors()`** on `ModuleLogisticsConsumer`: return lists **filtered** to vessels where `GetUpdateLockOwner(v.id) == local player` **when** `MainSystem.NetworkState == ClientState.Running`. **Never** treat “no lock owner” as go-ahead in MP (transient after unload — audit finding).

**Orbital:** `!LandedOrSplashed` early exit — **out of scope for R0**; document in tests so it is not confused with a regression.

### R1 — Subspace × time-based kolony (P0)

**Failure:** `Planetarium.GetUniversalTime()` / `lastCheck` diverge across `WarpSystem` subspaces; same kolony accrues twice; scenario sync merges badly.

**Mitigations:** Phase 2 kolony-radius **subspace join** (extend spectator-style warp); **and/or** single writer for stats (Update-lock holder); long-term server tick if needed.

### R2 — Shared scenario modules (P0)

**Failure:** `PlanetaryLogistics`, `OrbitalLogistics`, kolony scenario under **snapshot SHA** → lost updates, duplicate transfers, corrupt warehouses.

**Mitigation:** Phase 3 **ShareKolony**: add modules to `IgnoredScenarios`; **idempotent mutation messages**; server fan-out + persistence; model complexity comparable to **contract/R&D routing** in `ScenarioSystem` (~228–303) + `AgencyResearchRouter`, not trivial Share*.

### R3 — Unloaded converter catch-up (P1)

**Failure:** First loader runs `BaseConverter` catch-up; others see stale `vessel.lastUT` / resources. Tied to R1 and **lock handoff** (audit).

**Mitigation:** Phase 5 — proto update path / `UpdateProtoInPlace` / post-catch-up publish (see CLAUDE.md Strategy B). Design together with Phase 1.5 acceptance (lock transfer + `lastCheck`).

### R4 — WOLF global registry (P1)

**Failure:** Concurrent depot creation / routing without single writer.

**Mitigation:** Phase 4 — **per-agency** WOLF state (reuse Stage 5 `Universe/Agencies/...` patterns). **Do not** rely on unverified claims about `GetNewFlightNumber` — read source first; upstream ID hardening optional.

### R5 — UI / lifecycle polish (P2)

**Failure:** Extra `OnGUI` or helpers draw during LMP spectate / wrong scene.

**Mitigation:** Harmony early-out on known GUI entry points (e.g. orbital logistics UI). **Confirm** `KolonizationManager` spawn/lifecycle when reading file for audit #7—do not duplicate stale canvas wording.

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

Stage 5 **agency projection and routing** is the template for kolony scenario ownership. **BUG-010 / vessel pin** model maps to “one writer per kolony.” **Strategy B / UpdateProtoInPlace** is the right hook surface for catch-up. Do not rebuild these patterns—extend them.

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
3. `LmpCommon/IgnoredScenarios.cs` + `LmpClient/Systems/Scenario/ScenarioSystem.cs` (especially **ContractSystem** / scenario reconciliation region ~228–303 and any `TaskFactory` send pattern)
4. `LmpClient/Systems/VesselResourceSys/` (sender + apply path)
5. `LmpClient/Systems/VesselProtoSys/VesselProtoMessageSender.cs` (locking / thread offload)
6. `LmpClient/Systems/Lock/*`
7. `Server/System/Agency/` — 7 templates total (skim for shape, not copy-paste): `AgencyScenarioProjector.cs`, `AgencyContractRouter.cs`, `AgencyState.cs`, plus the four Stage 5.17e band-1 routers (`AgencyCurrencyRouter.cs`, `AgencyTechRouter.cs`, `AgencyResearchRouter.cs`, `AgencyProgressRouter.cs`) — audit v3.1 surfaced the four band-1 routers as equally applicable Phase 3 templates.
8. USITools: `Source/USITools/Logistics/ModuleLogisticsConsumer.cs`
9. MKS: `KolonizationManager.cs` (**prerequisite**), `KolonizationScenario.cs`, `MKSModule.cs`, `ScenarioOrbitalLogistics` / `PlanetaryLogisticsScenario` as needed
10. WOLF: `ScenarioPersister.cs` (**prerequisite** before Phase 4)
11. ~~`LmpCommon/Message/Types/MessageChannelType.cs`~~ — **file does not exist** (audit correction, v3.1). Channel IDs live as inline `DefaultChannel =>` literals in each `LmpCommon/Message/Server/*SrvMsg.cs` / `Client/*CliMsg.cs`. Grep across both directories to enumerate.

---

## 11. Optional upstream / small patches

- `[KSPField(isPersistant = true)]` on PartSync-needed fields if missing  
- WOLF ID generation hardening **only after** reading `ScenarioPersister.cs`  
- `virtual`/`protected` seam on logistics list builders if Harmony postfixes get brittle  

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
| | | | |

---

_End of handoff._
