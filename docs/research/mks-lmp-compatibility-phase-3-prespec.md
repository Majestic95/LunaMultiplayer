# Phase 3 — ShareKolony per-agency routers (R2) — pre-spec

**Status:** PRE-IMPLEMENTATION. Drafted 2026-05-18 (session 25) after the Phase 2 / MKS-R1 ship at `3a355618` and operator decision to proceed to Phase 3 over the operator-blocked two-client smoke backlog.

**Branch:** `feature/per-agency-mks` (worktree `F:\luna-multiplayer-mks`), tip `3a355618` at drafting time.

**Scope:** Pin concrete file:line anchors on both the MKS and LMP sides for the three per-agency routers Phase 3 introduces (`AgencyKolonyRouter` / `AgencyPlanetaryRouter` / `AgencyOrbitalRouter`), the projector splice for the three MKS scenarios, the wire surface for owner-only echoes, the `IgnoredScenarios` additions, the gate=off fallback to single-writer-per-scenario, and the pre-0.31-upgrade diagnostics. **No code changes in this commit** — same audit-via-pre-spec discipline that validated Phases 1 / 1.5 R0 / 2 (see [[feedback-audit-via-prespec]]).

**Source pins (do not re-clone if still current):**

- **MKS** `ed0f6aa6` at `F:\tmp\mks-external\MKS\`
- **USITools** `4ad5cdd8` at `F:\tmp\mks-external\USITools\`
- **LMP** `feature/per-agency-mks` tip `3a355618`
- **Per-agency parent** `feature/per-agency` tip is `c36d6f97` (server admin GUI MVP spec, docs-only). All Stage 5.18d slices landed. No Agency-shape drift in flight that would collide with Phase 3's `Server/System/Agency/*` additions.
- **Upstream coordination check:** AdmiralRadish has nothing in flight in `Server/System/Agency/` (it's fork-only) or in MKS-adjacent surfaces (KolonyTools / USITools are not vendored on upstream). Safe to land Phase 3 without coordination.

**Channel allocation verification (pickup §"Channel allocation" mandate, re-run at session start):**

- Server high-water mark: **22** (`AgencySrvMsg`, `LmpCommon/Message/Server/AgencySrvMsg.cs:55`).
- Client high-water mark: **21** (`AgencyCliMsg`, `LmpCommon/Message/Client/AgencyCliMsg.cs:53`).
- No new in-flight 5.18d slots since `d1822cdd`.
- **Phase 3 allocates no new channels.** All three new MsgData families ride existing `AgencySrvMsg` ch 22 / `AgencyCliMsg` ch 21 per the [[reference-agency-wire-extension]] convention.

**Wire enum allocation verification:**

- `AgencyMessageType` (`LmpCommon/Message/Types/AgencyMessageType.cs:10-18`) currently holds slots 0-5: `Handshake=0`, `CreateRequest=1`, `CreateReply=2`, `State=3`, `Contract=4`, `Visibility=5`.
- Phase 3 appends slots **6 / 7 / 8** for `KolonyState`, `PlanetaryState`, `OrbitalState` (naming per the [[feedback-user-facing-naming]] discipline — owner-only state-snapshot messages, mirroring `AgencyStateMsgData` shape not `ShareProgress*MsgData` per-mutation shape).
- `SubTypeDictionary` entries APPEND to both `AgencySrvMsg.cs:43-51` AND `AgencyCliMsg.cs:41-49` per the BUG-010 wire-symmetry rule.

---

## 1. The bugs Phase 3 fixes — verified end-to-end traces

### 1.a R1 residual (scenario-side) — `KolonizationScenario` cross-agency leak

Phase 2's MKS-R1 (`3a355618`) closes the time-base divergence (cross-vessel UT mismatch under LMP subspaces) by snapping the local subspace when entering kolony radius. **It does NOT close the scenario-side leak** — under the 30s SHA pass, Agency A's accumulated geology/botany/kolonization research is broadcast to every connected client, and Agency B's clients aggregate A's entries into their own `GetGeologyResearchBonus(bodyIndex)` totals.

**Verified trace under per-agency mode (`PerAgencyCareer=true`) without Phase 3:**

1. Alice (Agency A) lands an MKS base on Duna with a `MKSModule`-bearing converter part.
2. Alice's vessel `FixedUpdate` runs `MKSModule.UpdateGoals` → mutates a `KolonizationEntry`. `KolonizationManager.Instance.TrackLogEntry(entry)` ([KolonizationManager.cs:90-114](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationManager.cs#L90-L114)) writes the entry into `KolonizationScenario.Instance.settings` via `SaveLogEntryNode(newEntry)` at line 113.
3. LMP's 30s scenario SHA pass picks up the changed `KolonizationScenario` blob and the client ships it to the server via `ScenarioSystem.ParseReceivedScenarioData` ([Server/System/ScenarioSystem.cs:114-123](file:///F:/luna-multiplayer-mks/Server/System/ScenarioSystem.cs#L114-L123)) → `ScenarioDataUpdater.RawConfigNodeInsertOrUpdate`. The blob lands in `ScenarioStoreSystem.CurrentScenarios["KolonizationScenario"]`.
4. On Bob's (Agency B) next handshake or scene-load, the server's `SendScenarioModules` ([Server/System/ScenarioSystem.cs:70-111](file:///F:/luna-multiplayer-mks/Server/System/ScenarioSystem.cs#L70-L111)) ships every scenario in `CurrentScenarios` — including `KolonizationScenario` — to Bob. `AgencyScenarioProjector.ProjectForClient` (called at line 96) does NOT currently know about `KolonizationScenario`, so the blob passes through unchanged.
5. Bob's KSP applies the blob via `ProtoScenarioModule`, populating `KolonizationScenario.Instance.settings` with Alice's entries.
6. Bob's `KolonizationManager.Instance.KolonizationInfo` lazy-loads on next access ([KolonizationManager.cs:30-41](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationManager.cs#L30-L41)) from `KolonizationScenario.Instance.settings.GetStatusInfo()` — now containing Alice's entries.
7. When Bob's vessel calls `GetGeologyResearchBonus(dunaBodyIndex)` ([KolonizationManager.cs:116-119](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationManager.cs#L116-L119)), the static `LINQ` sum aggregates Alice's `GeologyResearch` into Bob's bonus — Bob "harvests" Alice's research without owning her vessels.

**Net effect under per-agency mode:** Agency separation is silently violated for kolony research. Privacy expectation (spec §10 Q1) is broken. Players who never visit Duna get bonuses from neighbours who do. Symmetric for botany / kolonization / boosters / Science / Funds / Rep on `KolonizationEntry`.

**Under shared-agency mode (`PerAgencyCareer=false`):** by definition all players are one agency, so the leak isn't a "leak" — it's the intended sharing model. Phase 3's gate=off fallback is single-writer-per-scenario (Update-lock holder of the kolony vessel = effective writer) to prevent multi-writer races on the SAME shared blob, but does NOT partition.

### 1.b R2 (planetary side) — `PlanetaryLogisticsScenario` shared-warehouse leak

**Verified trace under per-agency mode:**

1. Alice's warehouse vessel hosts a `ModulePlanetaryLogistics` part. `FixedUpdate` runs `LevelResources(rPart, "Hydrates", true)` ([ModulePlanetaryLogistics.cs:78-138](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/ModulePlanetaryLogistics.cs#L78-L138)).
2. The branch at line 78-98 pumps stored resources into the planetary store via `PlanetaryLogisticsManager.Instance.TrackLogEntry(logEntry)` at line 95. The entry is keyed by `(BodyIndex, ResourceName)` — `(dunaIndex, "Hydrates")`.
3. `PlanetaryLogisticsManager.TrackLogEntry` ([PlanetaryLogisticsManager.cs:77-93](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/PlanetaryLogisticsManager.cs#L77-L93)) writes `StoredQuantity` into `PlanetaryLogisticsScenario.Instance.settings._LogInfo` (the `PlanetaryLogisticsPersistance` cache).
4. 30s SHA pass picks up the changed `PlanetaryLogisticsScenario` blob, server stores it, Bob's clients receive it.
5. Bob's `ModulePlanetaryLogistics.LevelResources` runs the "draw from warehouse" branch at line 100-125. `FetchLogEntry(resource, body)` returns Alice's deposited quantity. Bob's warehouse fills from Alice's pool.

**Net effect:** Agency B's warehouses draw resources Agency A deposited. Cross-agency resource theft (or, more charitably, "shared warehouse pool" — but not what spec §10 Q1 promises).

### 1.c R2 (orbital side) — `ScenarioOrbitalLogistics` per-frame double-spend

**Verified trace (gate-state-independent — failure pre-exists per-agency mode):**

1. Alice creates an `OrbitalLogisticsTransferRequest` shipping Hydrates from her Mun station (`Origin`) to her Mun lander (`Destination`). `Launch()` ([OrbitalLogisticsTransferRequest.cs:236-281](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/OrbitalLogisticsTransferRequest.cs#L236-L281)) deducts the transfer's source resources from Origin (line 689 in `DoLaunchTasks` via `ExchangeResources(-amount)`), caches fuel/mass, sets `Status=Launched`, adds to `PendingTransfers`.
2. The transfer ConfigNode is persisted via `ScenarioOrbitalLogistics.OnSave` ([ScenarioOrbitalLogistics.cs:79-100](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/ScenarioOrbitalLogistics.cs#L79-L100)) and broadcast via the 30s SHA pass.
3. **Every connected client** loads the scenario, recreates the `OrbitalLogisticsTransferRequest`, and adds it to their local `PendingTransfers`. `ScenarioOrbitalLogistics.Update` ([ScenarioOrbitalLogistics.cs:150-165](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/ScenarioOrbitalLogistics.cs#L150-L165)) runs on every frame on every peer (subject to `TimeWarp.CurrentRate > 1` filter).
4. When `transfer.GetArrivalTime() <= Planetarium.GetUniversalTime()` — which happens at DIFFERENT wall-clock moments per peer because of LMP subspace divergence — `ProcessTransfers` ([ScenarioOrbitalLogistics.cs:170-203](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/ScenarioOrbitalLogistics.cs#L170-L203)) starts the `Deliver` coroutine at line 192.
5. `Deliver()` ([OrbitalLogisticsTransferRequest.cs:286-358](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/OrbitalLogisticsTransferRequest.cs#L286-L358)) calls `Destination.ExchangeResources(request.ResourceDefinition, request.TransferAmount)` at line 336 (delivery) or `Origin.ExchangeResources(...)` at line 331 (cancelled-return). `ExchangeResources` ([OrbitalLogisticsExtensions.cs:204-316](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/Helpers/OrbitalLogisticsExtensions.cs#L204-L316)) mutates `r.amount` on `Part.Resources` (loaded vessel, lines 288/292/302/306) or `ProtoPartResourceSnapshot.amount` (unloaded vessel, lines 237/241/252/256).
6. **Each peer's mutation propagates** through LMP's 2.5s `VesselResourceSystem` pulse, which under gate=on is rejected at the server's `VesselMsgReader.RejectIfCrossAgencyWrite` for cross-agency senders (5.17a write-path counterpart, soak Finding 2). **Under per-agency mode (1 player = 1 agency, Stage 5 design), there is no "same-agency double-execute" — only one player belongs to the destination's owning agency, so only one peer is structurally eligible to execute the delivery.** The hazard is purely cross-agency / cross-subspace peers firing independently.
7. Under gate=off (shared-agency), every peer's delivery propagates and every peer multi-writes — the spec-pre-Phase-3 baseline. The Deliver-prefix's Update-lock check (§2.d) is the gate=off authority; KSP enforces single-Control-per-vessel, so only one peer holds the destination's Update lock at a time.

**Net effect:** orbital transfers double-spend (origin deducted twice / destination receives twice) under multi-client conditions, even within a single agency. The destination's resource counter visibly jumps the moment N peers' deliveries fire. The R0 fix's `GetResourceStockpiles` filter does NOT cover this surface (the orbital fix MUST handle both loaded and unloaded branches, per handoff §R2 line 147).

---

## 2. The correct fix — three routers + projector splice + Deliver gate

**Per-spec invariant (CLAUDE.md, spec §10): `PerAgencyCareer=true` ⇒ 1 player = 1 agency.** Every Phase 3 decision below leans on this 1:1 mapping. There is no within-agency multi-player race because the gate=on partition's owning side has exactly one player. The Update-lock check that appears in the Deliver-prefix decision table (§2.d) is the gate=off authority + defensive code-uniformity affordance; it is logically redundant under the gate=on 1:1 invariant but harmless to evaluate uniformly. If a future product decision (post-Stage-5) opens N-players-per-agency, **revisit §2.d, §3.a, §3.d, §4.a, §4.b** — Phase 3's gate=on design is exact for 1:1 and would need re-derivation for N:1.

### 2.a Anchor map (verified)

| Surface | Path | Line(s) | Symbol | Phase 3 role |
|---------|------|---------|--------|--------------|
| KolonyTools/Kolonization | [KolonizationManager.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationManager.cs) | 11 | `class KolonizationManager : MonoBehaviour` | Hooked surface (NOT modified) |
| KolonyTools/Kolonization | [KolonizationManager.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationManager.cs) | 17-19 | `Instance` property | Lifetime: lazy `GameObject + AddComponent` on first access, no `Awake`/`OnDestroy`. Singleton persists until process exit. Survives all scene transitions — Harmony postfix is stable. |
| KolonyTools/Kolonization | [KolonizationManager.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationManager.cs) | 90-114 | `TrackLogEntry(KolonizationEntry)` | **Postfix anchor for `AgencyKolonyRouter`.** Mutation hook. Entry carries `VesselId` (string Guid). |
| KolonyTools/Kolonization | [KolonizationEntry.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationEntry.cs) | 1-18 | `class KolonizationEntry` | 13 fields including `VesselId` (string), `BodyIndex` (int), `LastUpdate`/`KolonyDate`/`GeologyResearch`/`BotanyResearch`/`KolonizationResearch`/`Science`/`Rep`/`Funds`/`RepBoosters`/`FundsBoosters`/`ScienceBoosters`. Wire payload format = serialise full record. |
| KolonyTools/Kolonization | [KolonizationManager.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationManager.cs) | 116-130 | `static Get{Geology,Botany,Kolonization}ResearchBonus(int bodyIndex)` | Aggregation read sites. **Not patched** — handled by server-side scenario projection (Q2 lock-in) so KSP-side bonus math is unchanged. |
| KolonyTools/Kolonization | [KolonizationScenario.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationScenario.cs) | — | `class KolonizationScenario : ScenarioModule` | Scenario-module name for projection + `IgnoredScenarios.IgnoreSend` addition. |
| KolonyTools/PlanetaryLogistics | [PlanetaryLogisticsManager.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/PlanetaryLogisticsManager.cs) | 11 | `class PlanetaryLogisticsManager : MonoBehaviour` | Same shape as KolonizationManager. Singleton lifetime identical. |
| KolonyTools/PlanetaryLogistics | [PlanetaryLogisticsManager.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/PlanetaryLogisticsManager.cs) | 77-93 | `TrackLogEntry(PlanetaryLogisticsEntry)` | Reference site for the mutation chain. **NOT the postfix anchor** — `PlanetaryLogisticsEntry` lacks vessel-id ([PlanetaryLogisticsEntry.cs:1-9](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/PlanetaryLogisticsEntry.cs#L1-L9)); router needs caller-vessel context. |
| KolonyTools/PlanetaryLogistics | [PlanetaryLogisticsEntry.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/PlanetaryLogisticsEntry.cs) | 1-9 | `class PlanetaryLogisticsEntry` | 3 fields: `BodyIndex` (int), `ResourceName` (string), `StoredQuantity` (double). **Body-resource-keyed, NOT vessel-keyed** — the per-agency partition derives ownership from the calling vessel. |
| KolonyTools/PlanetaryLogistics | [ModulePlanetaryLogistics.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/ModulePlanetaryLogistics.cs) | 91, 95 | `LevelResources` push branch | **Postfix anchor #1.** `vessel` is `this.vessel` (the warehouse vessel). |
| KolonyTools/PlanetaryLogistics | [ModulePlanetaryLogistics.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/ModulePlanetaryLogistics.cs) | 113, 124 | `LevelResources` pull branch | **Postfix anchor #2.** Same `vessel` context. |
| KolonyTools/PlanetaryLogistics | [ModulePlanetaryLogistics.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/ModulePlanetaryLogistics.cs) | 133, 136 | `LevelResources` overflow-store branch | **Postfix anchor #3.** Same `vessel` context. |
| KolonyTools/PlanetaryLogistics | [PlanetaryLogisticsScenario.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/PlanetaryLogisticsScenario.cs) | 8 | `class PlanetaryLogisticsScenario : ScenarioModule` | Scenario-module name for projection + `IgnoredScenarios.IgnoreSend` addition. |
| KolonyTools/OrbitalLogistics | [OrbitalLogisticsTransferRequest.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/OrbitalLogisticsTransferRequest.cs) | 286-358 | `IEnumerator Deliver()` | **Prefix anchor for `AgencyOrbitalRouter`.** Reads `Origin` / `Destination` properties at line 289-301. |
| KolonyTools/OrbitalLogistics | [OrbitalLogisticsTransferRequest.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/OrbitalLogisticsTransferRequest.cs) | 40, 43 | `_destinationId`, `_originId` | Persistent vessel-id strings (KSP `persistentId.ToString()` per [Destination setter line 149](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/OrbitalLogisticsTransferRequest.cs#L149) and [Origin setter line 117](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/OrbitalLogisticsTransferRequest.cs#L117)). Router reads these for ownership lookup. |
| KolonyTools/OrbitalLogistics | [ScenarioOrbitalLogistics.cs](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/ScenarioOrbitalLogistics.cs) | 13 | `class ScenarioOrbitalLogistics : ScenarioModule` | Scenario-module name for projection + `IgnoredScenarios.IgnoreSend` addition. |
| LMP / Server | [Server/System/ScenarioSystem.cs](file:///F:/luna-multiplayer-mks/Server/System/ScenarioSystem.cs) | 70-111 | `SendScenarioModules(ClientStructure client)` | Existing projection hook. Line 96 invokes `AgencyScenarioProjector.ProjectForClient` — Phase 3 adds switch cases inside the projector. |
| LMP / Server | [Server/System/Agency/AgencyScenarioProjector.cs](file:///F:/luna-multiplayer-mks/Server/System/Agency/AgencyScenarioProjector.cs) | 68-77 | `CareerScenarios` HashSet | **Add 3 entries:** `"KolonizationScenario"`, `"PlanetaryLogisticsScenario"`, `"ScenarioOrbitalLogistics"`. Fast-path skip needs them. |
| LMP / Server | [Server/System/Agency/AgencyScenarioProjector.cs](file:///F:/luna-multiplayer-mks/Server/System/Agency/AgencyScenarioProjector.cs) | 133-161 | `Project` switch | **Add 3 cases:** invoke `SpliceAgencyKolonyEntries` / `SpliceAgencyPlanetaryEntries` / `SpliceAgencyOrbitalTransfers`. |
| LMP / Server | [Server/System/Agency/AgencyState.cs](file:///F:/luna-multiplayer-mks/Server/System/Agency/AgencyState.cs) | 30-152 | dict/list fields | **Add 3 fields:** `Dictionary<string,AgencyKolonyEntry> KolonyEntries` keyed by `$"{vesselId:N}|{bodyIndex}"`, `Dictionary<string,AgencyPlanetaryEntry> PlanetaryEntries` keyed by `$"{bodyIndex}|{resourceName}"`, `Dictionary<Guid,AgencyOrbitalTransferEntry> OrbitalTransfers` keyed by transfer Guid. ConfigNode round-trip on each. |
| LMP / Server | [Server/System/LockSystem.cs](file:///F:/luna-multiplayer-mks/Server/System/LockSystem.cs) | 83-101 | 5.17a cross-agency guard | Reference template for the Deliver-gate trust posture (gate=on equates "destination's agency" with "Update-lock holder" structurally). |
| LMP / Common | [LmpCommon/IgnoredScenarios.cs](file:///F:/luna-multiplayer-mks/LmpCommon/IgnoredScenarios.cs) | 14-27 | `IgnoreSend` list | **Add 3 entries** for the 3 MKS scenarios so the 30s SHA pass doesn't fight the routers (same shape as `ContractSystem`/`Funding`/etc.). `IgnoreReceive` is NOT extended — clients still receive the projected scenario blobs from the server. |
| LMP / Common | [LmpCommon/Message/Types/AgencyMessageType.cs](file:///F:/luna-multiplayer-mks/LmpCommon/Message/Types/AgencyMessageType.cs) | 10-18 | `enum AgencyMessageType` | **Append 3 entries:** `KolonyState=6`, `PlanetaryState=7`, `OrbitalState=8`. Wire-protocol-relevant — never reorder. |

### 2.b Routers — three new files under `Server/System/Agency/`

All three follow the `AgencyContractRouter` (5.17d) structural template: `public static class` with a `TryRoute(ClientStructure client, ...)` entry point that returns `true` when the per-agency path handled the inbound (caller must NOT then run the shared-scenario relay/write) and `false` when the gate is off / client lacks agency / agency missing (caller continues unchanged). Internal helpers do per-entry exception isolation around classify / upsert / echo phases.

#### 2.b.i `AgencyKolonyRouter`

**File:** `Server/System/Agency/AgencyKolonyRouter.cs`.

**Entry point:**
```text
public static bool TryRoute(ClientStructure client, AgencyKolonyStateMsgData msg)
```

**Body shape (modelled on `AgencyContractRouter.cs:83-132`):**

1. `if (!AgencySystem.PerAgencyEnabled) return false;` — Career-mode + gate=on combined check (`AgencySystem.cs:58-60`).
2. `if (client == null || msg == null || string.IsNullOrEmpty(client.PlayerName)) return false;`.
3. `if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId)) return false;`.
4. `if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency)) return false;`.
5. **Per-entry classify-and-upsert under a SINGLE try/catch** (consumer-review finding #4 — isolation must cover the entire per-entry path, NOT just the `Upsert` call):
   ```text
   foreach (var entry in msg.Entries)
   {
       try {
           if (!Guid.TryParse(entry.VesselId, out var vesselGuid)) continue;       // malformed VesselId → skip
           if (!VesselStoreSystem.CurrentVessels.TryGetValue(vesselGuid, out var v)) continue;
           if (v.OwningAgencyId != Guid.Empty && v.OwningAgencyId != agencyId) {     // cross-agency: log + skip
               LunaLog.Debug($"[fix:MKS-R2] kolony entry for vessel {vesselGuid:N} rejected: cross-agency");
               continue;
           }
           Upsert(agency, entry);
           upsertedEntries.Add(entry);
       } catch (Exception ex) {
           LunaLog.Error($"[fix:MKS-R2] kolony entry skipped for {entry.VesselId}: {ex.GetType().Name}: {ex.Message}");
       }
   }
   ```
   Mirrors the `AgencyContractRouter.cs:111-122` shape (classify INSIDE the try, not outside it). The Unassigned-sentinel bypass is the `v.OwningAgencyId == Guid.Empty` allow.
6. After batch: `AgencySystem.SaveAgency(agencyId)` (single fsync per batch, same as `AgencyContractRouter.cs:161`).
7. **No shared-pool slot to free** — kolony entries don't have the `Offered` analogue that contracts do (no peer-acceptance race). The handoff §10 Q6 contract pattern simplifies here.
8. `AgencySystemSender.SendKolonyStateToOwner(client, agencyId, upsertedEntries)` — owner-only echo. Other agencies never see the entry.

**Catch-up on reconnect** (mirror of `AgencyContractRouter`'s `SendContractCatchupTo`): `AgencySystemSender.SendKolonyCatchupTo(client, agencyId)` wired into `HandshakeSystem` immediately after the existing `AgencyContractRouter` catch-up call. Returning player receives their full `KolonyEntries` dict before mid-session mutations arrive. Unconditional under gate=on (sends even an empty dict — the client mirror needs the empty state to know "no per-agency kolony, not unsynced").

**Gate=off behaviour** — under `AgencySystem.PerAgencyEnabled == false`, the client-side postfix on `KolonizationManager.TrackLogEntry` is a **no-op** (no per-agency wire message emitted) AND the `IgnoredScenarios.IgnoreSend` entry for `"KolonizationScenario"` is gate-conditional (suppressed-only-when-gate=on). The legacy 30s SHA pass operates unchanged under gate=off. The shared-mode product is "all players' kolony research accumulates on shared (VesselId, BodyIndex) keys"; this is the intended shared-mode behaviour — NOT a hazard to fix. Per the 1-player-per-agency invariant (§2 preamble), gate=on partitions cleanly by agency; gate=off keeps the legacy shared-accumulation path. **No "single-writer-per-Update-lock-holder" gate=off fallback exists for kolony** — that hazard framing was a misread of the shared-mode product (review-revision session 25, post the general-correctness finding #4 catch).

**Pure-helper extraction (per LMP testability convention):**

```text
public static (List<AgencyKolonyEntry> accept, List<string> rejectReasons)
ClassifyKolonyEntries(
    IEnumerable<AgencyKolonyEntry> incoming,
    Guid requesterAgencyId,
    Func<Guid, Guid?> getVesselOwningAgency)
```

Returns the partition. Pure, no side effects. Pinned in `ServerTest/AgencyKolonyRouterTest.cs` with ~10 cases (cross-agency reject, same-agency accept, Unassigned-sentinel bypass, malformed-VesselId-guid-parse-failure isolation, vessel-not-in-store skip, requester-lacks-agency-bypass, mixed-batch partial-acceptance, etc.).

#### 2.b.ii `AgencyPlanetaryRouter`

**File:** `Server/System/Agency/AgencyPlanetaryRouter.cs`.

**Entry point:**
```text
public static bool TryRoute(ClientStructure client, AgencyPlanetaryStateMsgData msg)
```

Same shape as kolony. Per-entry partition key in `AgencyState.PlanetaryEntries` is `$"{bodyIndex}|{resourceName}"`. The wire `AgencyPlanetaryEntry` (same class as the stored type per §2.e single-class default) carries `OwningVesselId` (Guid) — populated by the client-side `ModulePlanetaryLogistics.LevelResources` postfix using `this.vessel.id`. Server uses `OwningVesselId` to look up `OwningAgencyId` for the cross-agency partition decision. **Same single-try/catch isolation shape as §2.b.i step 5** — classify (Guid parse + vessel lookup + agency match) lives INSIDE the per-entry try block.

**Catch-up on reconnect:** `AgencySystemSender.SendPlanetaryCatchupTo(client, agencyId)` wired into `HandshakeSystem` immediately after `SendKolonyCatchupTo`. Same unconditional-under-gate=on contract.

**Gate=off behaviour:** the postfix on `ModulePlanetaryLogistics.LevelResources` is a no-op when `AgencyMembership.PerAgencyEnabled` is false. `IgnoredScenarios.IgnoreSend` entry for `"PlanetaryLogisticsScenario"` is gate-conditional. Legacy 30s SHA pass operates unchanged. **Known limitation under gate=off: the pre-existing MKS-multiplayer "two players pumping the same resource on the same body collide on `(BodyIndex, ResourceName)` key" hazard remains** — Phase 3 does NOT pretend to fix it under shared mode (it's an MKS-design hazard that exists pre-Phase-3 on `master` and would require a wider scope to address). Operators wanting strict planetary-warehouse correctness across multiple players need per-agency mode. Documented in §5 (out-of-scope).

#### 2.b.iii `AgencyOrbitalRouter`

**File:** `Server/System/Agency/AgencyOrbitalRouter.cs`.

**Two responsibilities** (the only Phase 3 router with this split):

1. **Per-agency transfer-list partition** (analogous to kolony / planetary). Per-agency `AgencyOrbitalTransferEntry` (same class as the §2.e wire entry) in `AgencyState.OrbitalTransfers` keyed by transfer Guid; persisted; owner-only echo via `AgencyOrbitalStateMsgData`. Transfers another agency creates never appear in this agency's `PendingTransfers` projection — Agency B's orbital-logistics UI doesn't show Agency A's pending fleets.
2. **Deliver-gate trust anchor** for the client-side `Harmony` prefix (§2.d below). The router doesn't perform delivery itself (delivery is per-frame, client-driven); the router persists the AUTHORITATIVE transfer state so reconnecting players see the correct pending-transfer list.

**Entry point** (mutation messages from client when transfer is Launched / Aborted / Delivered):
```text
public static bool TryRoute(ClientStructure client, AgencyOrbitalStateMsgData msg)
```

**C→S sibling message** (consumer-review finding #3): `AgencyOrbitalStateMsgData` is reused both directions per the §2.e single-class-per-slot default. C→S sends are emitted by a new `AgencyOrbitalSender.SendTransferStateChange` when transfer state changes locally (Launched / Aborted / Delivered status transitions intercepted via a postfix on the `OrbitalLogisticsTransferRequest.Launch` / `Abort` setters + a postfix on the Status setter — confirm exact anchor at slice-D impl). Server's `AgencyMsgReader` dispatches to `AgencyOrbitalRouter.TryRoute`, which ignores the wire-supplied `AgencyId` and derives it from the sender (§2.e trust posture).

**Vessel-id derivation** (Open Q3 resolved per general-review finding #6): `OrbitalLogisticsTransferRequest._destinationId` stores `vessel.persistentId.ToString()` (uint), per the **Destination** setter at [OrbitalLogisticsTransferRequest.cs:149](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/OrbitalLogisticsTransferRequest.cs#L149) (mirror of the Origin setter at line 117). The **Destination** fall-through accessor at lines [133-135](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/OrbitalLogisticsTransferRequest.cs#L133-L135) also accepts `vessel.id.ToString()` (Guid form). **Phase 3 derivation path: client-side postfix resolves the destination `Vessel` via `OrbitalLogisticsTransferRequest.Destination` (the property runs both the persistentId match AND the Guid match, returning a real `Vessel` or null), then reads `vessel.id` (the canonical Guid).** Pure helper `ResolveDestinationVesselGuid(transfer)` takes the transfer + the `FlightGlobals.Vessels` lookup as a `Func`; testable independently. Falls back to `_destinationModuleId` lookup via `ModuleOrbitalLogistics.FindVesselByOrbLogModuleId` ([line 141](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/OrbitalLogisticsTransferRequest.cs#L141)) for the vessel-id-reassignment-surgery edge case. **The wire `AgencyOrbitalTransferEntry.DestinationVesselId` always carries the canonical Guid form** — server never sees `persistentId` strings.

Per-entry partition derived from `entry.DestinationVesselId` (Guid). Same single-try/catch isolation shape as §2.b.i step 5.

**Catch-up on reconnect:** `AgencySystemSender.SendOrbitalCatchupTo(client, agencyId)` wired into `HandshakeSystem` immediately after `SendPlanetaryCatchupTo`. Same unconditional-under-gate=on contract. Returning player receives their full agency's pending + recent-expired transfers before the next per-frame `Update` cycle.

**Gate=off behaviour:** the C→S postfix is a no-op; `IgnoredScenarios.IgnoreSend` entry for `"ScenarioOrbitalLogistics"` is gate-conditional; legacy 30s SHA pass operates unchanged. **The Deliver-prefix (§2.d) STILL fires under gate=off** — it's gate-state-independent code (the decision table's gate=off rows handle the shared-mode authority via Update-lock check). Closes the per-frame double-spend even under gate=off — a strict improvement on the pre-Phase-3 baseline.

**The Deliver-gate authority is enforced client-side** (§2.d) — server-side this router is the persistence + reconnect-catch-up surface only.

### 2.c Projector splice — `AgencyScenarioProjector` extension

**File:** `Server/System/Agency/AgencyScenarioProjector.cs` (existing).

**Changes:**

1. Append 3 entries to `CareerScenarios` HashSet at line 68-77: `"KolonizationScenario"`, `"PlanetaryLogisticsScenario"`, `"ScenarioOrbitalLogistics"`.
2. Append 3 cases to the `Project` switch at line 133-161:
   - `case "KolonizationScenario": return SpliceAgencyKolonyEntries(serializedText, targetAgency);`
   - `case "PlanetaryLogisticsScenario": return SpliceAgencyPlanetaryEntries(serializedText, targetAgency);`
   - `case "ScenarioOrbitalLogistics": return SpliceAgencyOrbitalTransfers(serializedText, targetAgency);`
3. Three new private methods modelled on `SpliceAgencyStrategiesIntoScenario` (line 173-220) / `SpliceAgencyAchievementsIntoScenario` (line 230-303). Strip-then-splice via `ConfigNode` round-trip:
   - Parse the input text as a `ConfigNode`.
   - Strip ALL pre-existing child entries (matches the Stage 5.17e-6 Strategy/Achievement pattern, NOT the upsert pattern — see the pre-review-finding-A.2 note at `AgencyScenarioProjector.cs:245-262` that documents why strip-then-splice is correct under the per-agency contract).
   - Iterate `targetAgency.KolonyEntries.Values` / `.PlanetaryEntries.Values` / `.OrbitalTransfers.Values` under `AgencySystem.GetAgencyLock(agencyId)` (snapshot pattern from line 198-200).
   - Emit one child node per entry. KolonyEntry → `KOLONY_ENTRY { VesselId=... BodyIndex=... GeologyResearch=... ... }` mirroring the field set in [KolonizationEntry.cs:1-18](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationEntry.cs).
   - Per-entry exception isolation (drop the entry on parse failure, keep siblings).
   - Wrap whole-scenario parse failure in try/catch + return input unchanged + log (same fallback as `SpliceAgencyTechIntoResearchAndDevelopment` line 393-407).

**Critical: parse failure must NOT block handshake.** A malformed entry would otherwise hang scene-load. The fallback "return shared scenario text unchanged on whole-scenario parse failure" is identical to the existing pattern.

### 2.d Deliver gate — client-side Harmony prefix

**File:** `LmpClient/Harmony/OrbitalLogisticsTransferRequest_DeliverPrefix.cs` (new).

**Critical mechanism constraint (general-review finding #1).** A naive prefix that returns `false` to skip `Deliver`'s IEnumerator **would hang ProcessTransfers**. The caller at [ScenarioOrbitalLogistics.cs:194](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/ScenarioOrbitalLogistics.cs#L194) does `while (transfer.Status == DeliveryStatus.Launched || transfer.Status == DeliveryStatus.Returning) yield return null;` and only `Deliver`'s own body flips `Status`. Skipping the body without flipping `Status` leaves the `while` loop yielding forever; the outer `for` in [ScenarioOrbitalLogistics.cs:170-203](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/ScenarioOrbitalLogistics.cs#L170-L203) never advances, and the every-2s `Update` keeps starting new ProcessTransfers coroutines on top of the hung one — accumulating stack frames forever.

**Correct mechanism: the prefix mutates `__instance.Status` BEFORE returning false.** Setting `Status = DeliveryStatus.Failed` + a descriptive `StatusMessage` makes the inner `while` loop's predicate false on the next yield, so ProcessTransfers' first if-branch at line 181-186 moves the transfer to ExpiredTransfers on the skipping peer. Post-skip behaviour differs by gate:

- **Under gate=on (per-agency mode):** the per-agency projector ships the projection of the OWNING agency's `OrbitalTransfers` dict to each client. A skipping peer (different agency from destination's owner) does NOT receive the transfer in their projected `ScenarioOrbitalLogistics` blob — §2.b.iii's "Transfers another agency creates never appear in this agency's `PendingTransfers` projection." `OnLoad` therefore does not re-add the transfer; the skipping peer's local state is consistent immediately. No re-skip cycle.
- **Under gate=off (shared-agency mode):** the legacy 30s scenario SHA pass is unchanged and ships the OWNING peer's still-`Launched` blob to every other peer. `OnLoad` clears + rebuilds [ScenarioOrbitalLogistics.cs:52-65](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/ScenarioOrbitalLogistics.cs#L52-L65), the transfer reappears in PendingTransfers, the prefix re-skips, transfer moves to ExpiredTransfers again. Bounded cycle (one re-skip per ~30s per transfer per peer) until the owning peer executes delivery and broadcasts the resulting `Delivered` scenario state. The §3.f rate-limited log line ("at most one per (transferGuid, decision) per 60s") absorbs the noise.

**Shape:**

```text
[HarmonyPatch(typeof(OrbitalLogisticsTransferRequest), nameof(OrbitalLogisticsTransferRequest.Deliver))]
public static class OrbitalLogisticsTransferRequest_DeliverPrefix
{
    public static bool Prefix(OrbitalLogisticsTransferRequest __instance)
    {
        if (ShouldExecuteDelivery(__instance, ...))
            return true; // let the original IEnumerator run on the elected peer
        // Delegated peers: mark Failed + brief message + skip body. ProcessTransfers
        // sees the Status change on next yield and moves the transfer to
        // ExpiredTransfers (line 198). On next scenario sync the canonical
        // (still-Launched) state replaces ours; we re-skip; bounded cycle until
        // the elected peer actually delivers.
        __instance.Status = DeliveryStatus.Failed;
        __instance.StatusMessage = "[fix:MKS-R2] Delegated to owning-agency player";
        // Rate-limit the log: at most one entry per (transferGuid, decision) per
        // 60s so the bounded cycle doesn't fill KSP.log.
        RateLimitedDebugLog(__instance);
        return false;
    }
}
```

**Pure-helper extraction** (per LMP testability convention — same pattern as `FilterToLocallyOwned` / `FindMostAdvancedForeignKolonySubspace` / `ShouldSteadyStateRetry`):

```text
public static bool ShouldExecuteDelivery<TTransfer>(
    TTransfer transfer,
    bool perAgencyEnabled,
    string localPlayerName,
    Guid localAgencyId,
    Func<TTransfer, Vessel> getDestination,
    Func<Vessel, Guid> getOwningAgency,           // 5.18b AgencyMembership.TryGetOwningAgency
    Func<Guid, string> getUpdateLockOwner,         // LockSystem.LockQuery.GetUpdateLockOwner
    Func<TTransfer, DeliveryStatus> getStatus)
```

**Decision table (revised per the 1-player-per-agency invariant — see §2 preamble):**

| Gate | Destination's OwningAgencyId | Local agency known? | Update-lock owner | Result | Why |
|------|------------------------------|---------------------|-------------------|--------|-----|
| OFF | (irrelevant) | (irrelevant) | local player | **Execute** | Single-writer = Update-lock holder (gate=off authority; KSP enforces single-Control-per-vessel). |
| OFF | (irrelevant) | (irrelevant) | other / empty | **Skip** | Defer to the lock-holder peer. Empty owner is transient post-unload — never assume go-ahead in MP. |
| ON | == local agency | yes | local player | **Execute** | We are THE player for this agency (1:1 invariant); we own the destination's authority. Lock check redundant under 1:1 but evaluated uniformly. |
| ON | == local agency | yes | other / empty | **Skip** | Under 1:1, this case is structurally impossible for stamped vessels — `LockSystem.cs:83-101` 5.17a guard rejects cross-agency lock acquires, so any lock holder belongs to the destination's agency, which by 1:1 is us. The skip branch is defensive only (covers transient post-unload empty-owner + connect-race windows). |
| ON | != local agency, non-Empty | yes | (any) | **Skip** | Cross-agency — owning agency's player executes. |
| ON | Empty (Unassigned sentinel) | yes | local player | **Execute** | Sentinel vessel + local lock holder. Spec §10 Q3 — any agency may interact; single-Control-per-vessel breaks the tie. |
| ON | Empty (Unassigned sentinel) | yes | other / empty | **Skip** | Sentinel + other lock holder — defer to them. |
| ON | (any) | no | local player | **Execute** | Defensive bypass for connect-window timing (5.18a mirror not yet populated). Lock check still gates. Same shape as `LockSystem.cs:83-86`. |
| ON | (any) | no | other / empty | **Skip** | Defensive bypass without authority — no execution. |
| (any) | (any) | (any) | (any), with `Status != Launched && Status != Returning` | **Execute (passthrough)** | Non-active transfer — let stock failure paths log + handle. Don't gate non-active state. |

**Defense-in-depth note (revised per 1:1 invariant).** Under gate=on with the 1-player-per-agency rule, the agency check IS the primary authority and uniquely identifies the deliverer. The Update-lock check is retained as a second predicate for three reasons: (a) it provides the gate=off authority unchanged (same code path serves both modes); (b) it handles the spec §10 Q3 Unassigned-sentinel case where the agency check cannot decide; (c) it defends the early-boot connect-race window where the 5.18a client mirror has not yet received the agency handshake. If a future product decision opens N-players-per-agency, the lock check becomes load-bearing as a within-agency tie-breaker; today it's defensive.

**Cross-agency relay-write counterpart:** under gate=on, a malicious client that bypasses the prefix locally and runs `Deliver()` would mutate their `r.amount` and broadcast via `VesselResourceMsgData`. The server-side `VesselMsgReader.RejectIfCrossAgencyWrite` (5.17a write-path counterpart, soak Finding 2) drops that relay before peers see it.

**Why prefix-not-postfix:** prefix can mutate Status before the IEnumerator runs, preventing the inner `while` hang AND skipping the resource mutation. A postfix would let `Deliver` execute then "undo" — much harder to reason about, race conditions on `Status`.

**Why no server-side Deliver authority:** the mutation is per-frame, client-side, and entirely about preventing N-peer-simultaneous-execution. Server-mediation would require a brand new `OrbitalDeliveryReq`/`Reply` wire surface, server-side timer awareness, per-delivery round-trip latency, AND would diverge from Phase 3's other two routers (kolony / planetary postfix-driven) for no architectural benefit — operator confirmed this trade-off direction Q3 sign-off (session 25).

### 2.e Wire surface — 3 new MsgData families

**Naming default locked in (review-revision logic-pass finding #3):** one shared struct per enum slot, used both directions — mirrors Stage 5.17d `AgencyContractMsgData`'s pattern (single class type, single dictionary entry on both `AgencySrvMsg` and `AgencyCliMsg`). On inbound (C→S) the server IGNORES the wire-supplied `AgencyId` and derives the sender's agency from `AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, ...)` — same trust posture as 5.17d's `AgencyContractRouter.TryRoute`. This removes the "two-class-per-slot" ambiguity and matches the existing wire pattern an implementer is already familiar with.

**Sender naming clarification (review-revision logic-pass finding #4):**
- **Server-side outbound** (echo + catch-up) extends the existing `Server/System/Agency/AgencySystemSender.cs` (5.15c) with new methods `SendKolonyStateToOwner` / `SendPlanetaryStateToOwner` / `SendOrbitalStateToOwner` + the three `SendXxxCatchupTo` companions. No new server-side sender class.
- **Client-side outbound** (mutation emit from postfix) is three NEW classes — `LmpClient/Systems/Agency/AgencyKolonySender.cs`, `AgencyPlanetarySender.cs`, `AgencyOrbitalSender.cs` — each siblings of the existing `LmpClient/Systems/Agency/AgencyMessageSender.cs` (5.18a). Each ships a single `TaskFactory.StartNew + NetworkSender.QueueOutgoingMessage` per the §3.e pattern.

Per the [[reference-agency-wire-extension]] recipe steps applied to all three:

**Slot 6 / `AgencyKolonyStateMsgData`:**
- Both-directions shared class. Owner-only S→C echo for confirming a routed mutation + connect-catch-up payload; C→S per-mutation emit from postfix.
- Fields: `Guid AgencyId` (S→C populates; C→S server ignores) + `int EntryCount` + `AgencyKolonyEntry[] Entries`.
- `AgencyKolonyEntry`: `string VesselId` + `int BodyIndex` + 9 doubles (matching the [`KolonizationEntry` field set](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/Kolonization/KolonizationEntry.cs)) + 3 ints (boosters). **SAME class used both as the wire entry AND the `AgencyState.KolonyEntries` dict value** — no compression boundary in Phase 3 (unlike 5.17d's `ContractInfo` vs `AgencyContractEntry` split for QuickLZ payloads), so a single class minimises moving parts.
- `MaxEntryCount = 4096` DoS guard (same pattern as `AgencyContractMsgData.MaxContractCount = 4096`).
- **Arrival conditions documented on the type's XML** (recipe step 7): "(a) On connect/reconnect, immediately after AgencyContractMsgData catch-up. (b) On mid-session mutation, in response to client-side `KolonizationManager.TrackLogEntry` postfix relay (S→C echoes the upserted entries to the owning client only)."
- **Client write path (recipe step 7):** new `AgencyKolonySender.SendMutation` posts a per-entry message from the postfix.
- **Orthogonal concerns (recipe step 7):** entry-level reward routing (Science/Funds/Rep scalars on `KolonizationEntry`) is contained within the entry — there's no separate currency-router intercept for kolony-yield scalars in this pre-spec. If MKS' kolony rewards interact with `Funding.Instance` / `ResearchAndDevelopment.Instance` (verify at implementation per §11 Q2), defer that interaction to follow-up; first cut treats kolony entry scalars as opaque per-agency state.

**Slot 7 / `AgencyPlanetaryStateMsgData`:**
- Both-directions shared class. `AgencyPlanetaryEntry` (SAME class used as wire entry AND `AgencyState.PlanetaryEntries` value): `Guid OwningVesselId` (populated by client-side postfix from `this.vessel.id`) + `int BodyIndex` + `string ResourceName` + `double StoredQuantity`.
- `MaxEntryCount = 4096`.
- Arrival / Client-write / Orthogonal sections mirror kolony's structure (catch-up after Kolony in HandshakeSystem; postfix on `ModulePlanetaryLogistics.LevelResources` emits per-mutation via `AgencyPlanetarySender.SendMutation`; no separate currency-router interaction expected).

**Slot 8 / `AgencyOrbitalStateMsgData`:**
- Both-directions shared class. Carries a snapshot of the agency's pending + recently-completed orbital transfers.
- `AgencyOrbitalTransferEntry` (SAME class used as wire entry AND `AgencyState.OrbitalTransfers` value): `Guid TransferGuid` + `Guid OriginVesselId` + `Guid DestinationVesselId` + `DeliveryStatus Status` + `double StartTime` + `double Duration` + `byte[] PayloadBytes` (the original transfer's persistent serialization, opaque to LMP — passthrough to KSP when projector splices back).
- `MaxEntryCount = 1024` (orbital transfers are higher per-unit cost than kolony entries — bound tighter).
- **Emitted by** new `AgencyOrbitalSender.SendTransferStateChange` on transfer state-machine transitions. The intercept point is a Harmony postfix on `OrbitalLogisticsTransferRequest` instance methods — verify exact anchors at slice-D impl (candidates: postfix on `Launch` / `Abort` / a postfix on the `Status` field setter via `[HarmonyPatch("set_Status")]` if Status is a property, or postfix on `DoLaunchTasks` / `DoFinalLaunchTasks` for the launch path). The send carries the transfer's canonical `DestinationVesselId` Guid (resolved via §2.b.iii pure-helper `ResolveDestinationVesselGuid`).
- **Arrival conditions:** (a) on connect/reconnect immediately after `AgencyPlanetaryStateMsgData` catch-up via `SendOrbitalCatchupTo`; (b) on mid-session mutation in response to per-entry C→S routed via `AgencyOrbitalRouter`.
- **Orthogonal concerns:** Deliver-gate authority is the §2.d client-side prefix (separate from this state-snapshot wire). Resource mutations themselves propagate through standard `VesselResourceMsgData` (server-side `RejectIfCrossAgencyWrite` already blocks cross-agency relay). This message carries only the transfer's state-machine snapshot — not the resource amounts themselves.

All three families: per-channel ReliableOrdered (existing `AgencySrvMsg` / `AgencyCliMsg` constraint). Forward-compat via `lidgrenMsg.Position < lidgrenMsg.LengthBits` tail guard for future field additions (mirrors `VesselProtoMsgData.Reason` precedent).

### 2.f `AgencyState` field additions

**File:** `Server/System/Agency/AgencyState.cs` (existing).

**Append three properties** (modelled on lines 51-152):

```text
public Dictionary<string, AgencyKolonyEntry> KolonyEntries { get; } =
    new Dictionary<string, AgencyKolonyEntry>(StringComparer.Ordinal);

public Dictionary<string, AgencyPlanetaryEntry> PlanetaryEntries { get; } =
    new Dictionary<string, AgencyPlanetaryEntry>(StringComparer.Ordinal);

public Dictionary<Guid, AgencyOrbitalTransferEntry> OrbitalTransfers { get; } =
    new Dictionary<Guid, AgencyOrbitalTransferEntry>();
```

**Visibility (consumer-review finding #6):** all three properties are `public { get; }` matching the existing `TechNodes` / `ScienceSubjects` / `Strategies` / etc. shape at AgencyState.cs:78-152. The `public` modifier is load-bearing: the `AgencyScenarioProjector` splice methods + the `AgencySystemSender.SendXxxCatchupTo` paths + the ServerTest persistence cases all live OUTSIDE the `Server.System.Agency` namespace and need read-access. **The client-side mirror does NOT consume `AgencyState` directly** (Stage 5.18a established that the client mirrors agency state via the owner-only wire MsgData only, not via shared types) — `AgencyState` is a server-side data class and the `public` is for the projector + sender + tests, not for cross-process access.

**Concurrency contract:** mutations + reads MUST hold `AgencySystem.GetAgencyLock(agencyId)` (same rule as `TechNodes` XML at AgencyState.cs:60-78). The router holds the lock around the upsert batch; the projector splice acquires the lock around its `.Values.ToArray()` snapshot.

**ConfigNode round-trip** (extends the `ToConfigNode` / `FromConfigNode` methods at AgencyState.cs:183-632):

- `KOLONY_ENTRIES` child node containing `KOLONY` sub-nodes (12+1 values per entry — VesselId/BodyIndex + 12 numeric fields). Empty when `KolonyEntries.Count == 0` (matches the "only when non-empty" pattern at line 199).
- `PLANETARY_ENTRIES` child node containing `PLANETARY` sub-nodes (3 values per entry — OwningVesselId/BodyIndex+ResourceName/StoredQuantity).
- `ORBITAL_TRANSFERS` child node containing `TRANSFER` sub-nodes (TransferGuid + Origin/Dest + Status + 2 doubles + Base64(PayloadBytes)).

**Forward-compat:** missing child nodes load as empty dicts. Per-entry parse failures isolated (skip, keep siblings). Malformed Guid → skip. Malformed numeric → use `0d`. Same shape as the Stage 5.17e-4 TECHTREE forward-compat at line 458-497.

---

## 3. Defense-in-depth stack

### 3.a Per-entry exception isolation

Identical to `AgencyContractRouter`'s per-contract isolation (commit XML at line 98-101). Classification + upsert + echo each wrap individual entries in try/catch. A malformed entry's failure is logged with `[fix:per-agency-career]` prefix; the batch continues. Rationale: KSP's `KolonizationEntry` is operator-hand-edit-vulnerable (the disk file is plain-text ConfigNode); a single corrupt entry must not abort the per-agency apply for hundreds of valid siblings.

### 3.b Per-agency lock discipline

All mutations to `AgencyState.KolonyEntries` / `PlanetaryEntries` / `OrbitalTransfers` hold `AgencySystem.GetAgencyLock(agencyId)`. Reads that iterate `.Values` (projector, sender) also hold the lock for snapshot atomicity. Pattern is established by AgencyState.cs:60-78 (TechNodes) and used uniformly by the existing routers + projector splices. Without it, `SaveAgency`'s `Serialize` could observe a torn intermediate snapshot under concurrent mutation. Stage 5.17b precedent: the same per-agency lock contract.

### 3.c Defensive copy of mutable wire payloads

`AgencyOrbitalTransferEntry.PayloadBytes` is a `byte[]` from the wire. Storing the reference directly into `AgencyState.OrbitalTransfers` would let a subsequent re-arrival mutate (or operator hand-edit) the same buffer in place — corruption path AgencyContractRouter caught at lines 222-231. **Copy on store** (same `Buffer.BlockCopy` pattern).

### 3.d Cross-agency rejection at the router AND at the relay

**Two layers** in line with the 5.17a + write-path counterpart precedent:

1. **Router-level (this pre-spec):** `TryRoute` per-entry check drops cross-agency entries silently (log at Debug).
2. **Relay-level (existing 5.17a):** `Server/Message/VesselMsgReader.RejectIfCrossAgencyWrite` already rejects cross-agency `VesselResourceMsgData` — so even if a bypassed Deliver-prefix client runs delivery and broadcasts, the resource sync never propagates across agencies.

The two layers are complementary. The router stops the per-agency state from being polluted; the relay stops the cross-agency resource view from leaking. Both are needed.

### 3.e Threading model

Mutation hooks (Harmony postfix on `TrackLogEntry`, postfix on `LevelResources`, prefix on `Deliver`) run on KSP's main thread (Unity's `FixedUpdate` / `Update`). Wire send from the postfix follows the **established `*MessageSender` pattern used uniformly across the codebase**:

```csharp
TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(
    MessageFactory.CreateNew<AgencyCliMsg>(msg)));
```

Verified at [LmpClient/Systems/Agency/AgencyMessageSender.cs:32](file:///F:/luna-multiplayer-mks/LmpClient/Systems/Agency/AgencyMessageSender.cs#L32) (the existing 5.18a sender Phase 3's new families will sit alongside) and replicated identically in every other `*MessageSender.cs` (Admin, Chat, Craft, Facility, Flag, Group, Handshake, Kerbal, Lock, ModApi, Motd, PlayerColor, Scenario, SettingsSys, Share*). The pattern is two layers: `TaskFactory.StartNew` offloads message-object creation off the main thread; `NetworkSender.QueueOutgoingMessage` puts the result on a `ConcurrentQueue` that Lidgren's send thread drains.

**This addresses the handoff §Phase 3 item 5 concern correctly.** The handoff cautions against raw main-thread sends because `MessageBase.GetByteCount` traversal + Lidgren serialization can be non-trivial under high cadence — the StartNew offload prevents those calls from blocking KSP's `FixedUpdate` frame budget.

Phase 3's three new senders — `AgencyKolonySender`, `AgencyPlanetarySender`, `AgencyOrbitalSender` — each follow this exact two-line pattern, identical to `AgencyMessageSender.SendMessage` shipped in 5.18a. No new queue infrastructure to author.

If telemetry shows postfix cadence becomes a hotspot under heavy MKS load (e.g. >50 entries/sec across a megabase), reconsider via per-entry coalescing (collect entries on one tick, batch-send next tick). Defer the coalescing until measured.

### 3.f Operator notification

Each router emits one `[fix:MKS-R2]` log line per batch at Debug level (not per-entry — noise). Same convention as MKS-R0 / MKS-R1. The Deliver-prefix path emits at most one log line per skipped delivery at Debug, with rate-limit (one log per (transferGuid, decision) pair — repeat skips on subsequent frames silenced). KolonyEntry write-paths log at most one per minute per (agencyId, bodyIndex) — bounded operator visibility without log-spam.

`ForkBuildInfo.ActiveFixes` gets `"MKS-R2"` appended at boot.

### 3.g `IgnoredScenarios` — gate-conditional broadcast suppression

The existing `LmpCommon/IgnoredScenarios.IgnoreSend` is a static list; Phase 3 needs the 3 MKS scenarios suppressed-only-when-gate=on so shared-mode operators retain the legacy 30s SHA propagation. **Locked default (review-revision logic-pass finding #8): Option B — runtime check in the send-filter call site.** Don't extend `IgnoredScenarios.cs`'s static list; instead add a 3-name `HashSet<string>` + `SettingsServerStructure.PerAgencyCareerEnabled` gate check directly in `LmpClient/Systems/Scenario/ScenarioSystem`'s send-filter where `IgnoredScenarios.IgnoreSend.Contains(name)` is currently evaluated. Two reasons for Option B:

1. **No LmpCommon API change.** Option A (adding `IgnoreSendIfPerAgencyEnabled` to `IgnoredScenarios.cs`) expands the shared-library contract; Option B keeps the per-agency logic local to the consumer.
2. **PerAgencyCareerEnabled is a client-side runtime flag, not a wire-protocol constant.** The static `IgnoredScenarios.IgnoreSend` list represents a permanent design decision (Funding/etc. ALWAYS use the Share* wire); the Phase 3 MKS scenarios are dual-mode (gate=off keeps SHA, gate=on routes per-agency). Mixing static + dynamic semantics in one class is a smell; Option B keeps them separate.

`IgnoreReceive` is NOT extended either way — the server's projection still ships per-agency-filtered (under gate=on) or unfiltered (under gate=off) scenario blobs to every client; clients consume them via the standard scenario apply path. The gate-conditional filter is one-directional (broadcast suppression only).

**Implementation sketch:**
```text
// In LmpClient/Systems/Scenario/ScenarioSystem (or wherever IgnoreSend is consulted):
private static readonly HashSet<string> PerAgencyOnlyIgnoreSend = new HashSet<string>(StringComparer.Ordinal)
{
    "KolonizationScenario",       // Phase 3 — per-agency router handles under gate=on
    "PlanetaryLogisticsScenario", // Phase 3 — per-agency router handles under gate=on
    "ScenarioOrbitalLogistics",   // Phase 3 — per-agency router handles under gate=on
};

bool ShouldSuppressSend(string scenarioName) =>
    IgnoredScenarios.IgnoreSend.Contains(scenarioName)
    || (SettingsSystem.ServerSettings.PerAgencyCareerEnabled && PerAgencyOnlyIgnoreSend.Contains(scenarioName));
```

Slice A doesn't ship this — the filter is added in Slice B alongside the first per-agency router that needs the gate. Slices B/C/D each verify the matching scenario is suppressed-under-gate=on + propagates-under-gate=off in their MockClientTest coverage.

---

## 4. Per-agency dovetail — gate-on, gate-off, Sandbox/Science, upgrade-in-place

### 4.a Gate=on (`PerAgencyCareer=true` + GameMode=Career, the Stage 5 design)

- Routers fire on every kolony/planetary/orbital mutation.
- Projection splices per-agency state into outgoing scenarios.
- Deliver-prefix executes for the destination's owning-agency player only.
- Agency separation is enforced end-to-end: privacy (no cross-agency state leak), correctness (no multi-spend), persistence (`Universe/Agencies/{guid}.txt` carries per-agency MKS state).

### 4.b Gate=off (`PerAgencyCareer=false`, shared-scenario default)

**Postfix is a no-op; 30s SHA pass unchanged; only the orbital Deliver-prefix runs.**

- `AgencySystem.PerAgencyEnabled` returns false (combined check at `AgencySystem.cs:58-60`).
- Server-side routers' `TryRoute` returns false immediately; the projector's `Project` returns input text unchanged. No per-agency state is written / read / shipped.
- Client-side postfixes on `KolonizationManager.TrackLogEntry` + `ModulePlanetaryLogistics.LevelResources` early-return (no wire emit, no local mutation suppression). The `IgnoredScenarios.IgnoreSend` gate-conditional filter (§3.g) does NOT suppress the 3 MKS scenarios from the 30s SHA pass under gate=off. **Result: shared-mode kolony / planetary / orbital propagation is bit-identical to the pre-Phase-3 `master` baseline.**
- **Orbital Deliver-prefix is gate-state-independent** — it runs under both gates. Decision-table's gate=off rows (§2.d) apply: "Update-lock holder of destination is local player → execute; otherwise → skip." KSP's single-Control-per-vessel constraint guarantees only one peer executes per transfer. This is a strict improvement on the pre-Phase-3 per-frame double-spend baseline — shared-mode operators GAIN this correctness without changing anything else.

**Per-router under gate=off:**

| Surface | Pre-Phase-3 baseline | Gate=off behaviour with Phase 3 | Change |
|---------|----------------------|---------------------------------|--------|
| Kolony (`KolonizationScenario`) | 30s SHA broadcasts shared blob; all players accumulate research on `(VesselId, BodyIndex)` keys — keys don't collide between different vessels, aggregation across same body sums by design (the intended shared-mode product). | Identical. Postfix no-op. 30s SHA unchanged. | **None.** |
| Planetary (`PlanetaryLogisticsScenario`) | 30s SHA broadcasts shared blob; `(BodyIndex, ResourceName)` keyed entries — two players pumping the same resource on the same body collide last-write-wins. Pre-existing MKS-multiplayer hazard. | Identical. Postfix no-op. 30s SHA unchanged. Pre-existing hazard NOT fixed by Phase 3 under gate=off. | **None** (hazard documented as known limitation, §5). |
| Orbital (`ScenarioOrbitalLogistics`) | 30s SHA broadcasts shared pending list; per-frame `Update` fires `Deliver()` on every peer; every peer mutates resources independently → double-spend. | 30s SHA unchanged. Per-frame `Deliver()` is gated by the new prefix using the Update-lock check (§2.d gate=off rows). Single peer executes per transfer; resource sync handles propagation. | **Improvement.** Pre-frame double-spend closed. |

**Dual-mode silence preserved (per spec §11):** zero observable regression for shared-mode operators on any surface; one strict improvement on orbital (the double-spend fix). The pre-Phase-3 kolony shared-accumulation product is intentionally preserved — under gate=off, multiple players cooperating on the same body's research is the design.

### 4.c Sandbox / Science game mode

- `AgencySystem.PerAgencyEnabled` includes the `GameMode == Career` check; under Sandbox/Science it's always false even if `PerAgencyCareer=true` is misconfigured.
- Routers no-op; projection no-ops; client-side postfixes early-return (no per-agency wire emit); the orbital Deliver-prefix still fires (gate-state-independent) and uses its Update-lock check to single-execute each transfer.
- `KolonizationScenario` doesn't actually accrue under Sandbox (KSP-side gating), but `PlanetaryLogisticsScenario` and `ScenarioOrbitalLogistics` can still run — the gate=off path (§4.b) handles both: planetary continues with its pre-existing MKS-multiplayer hazard unchanged; orbital gets the Deliver-prefix single-executor improvement.

### 4.d Pre-0.31 upgrade-in-place — three boot-time diagnostics + hazard-gate wiring

Mirror of `AgencyScenarioProjector`'s `WarnAboutSavingsLossOnUpgrade` (5.17c) + `WarnAboutSharedContractsOnUpgrade` (5.17d). Each Phase 3 router contributes one boot-time warning AND must wire into the existing `RefuseStartupIfUpgradeHazardWithoutOverride` so the operator hazard-acknowledgement gate fires before any data loss.

**Hazard-gate wiring (upgrade-lens finding #1 — MUST FIX).** The existing pattern at [AgencySystem.cs:285](file:///F:/luna-multiplayer-mks/Server/System/Agency/AgencySystem.cs#L285) calls `RefuseStartupIfUpgradeHazardWithoutOverride()` after the `WarnAbout*` helpers run. That refusal helper evaluates a set of hazard predicates (currently savings/contracts/tech/research/progress-facility) and short-circuits server startup when `AllowEnablePerAgencyOnExistingUniverse=false`. Phase 3's 3 new warnings MUST register equivalent hazard predicates so the refusal fires when:

- Shared `KolonizationScenario` has any existing entries, OR
- Shared `PlanetaryLogisticsScenario` has any existing `LOGISTICS_ENTRY` child nodes with non-zero `StoredQuantity`, OR
- Shared `ScenarioOrbitalLogistics` has any pending or expired transfers.

Without this wiring, an operator who misses the WARN-level log lines boots cleanly, players connect, MKS state silently strips on first per-agency scene-load → uniform data loss with no operator opportunity to intervene. **Wire the 3 new predicates into the same boolean disjunction `RefuseStartupIfUpgradeHazardWithoutOverride` already evaluates.**

**Three boot-time warnings (all invoked in `AgencySystem.LoadExistingAgencies` immediately before `RefuseStartupIfUpgradeHazardWithoutOverride`):**

- **`WarnAboutSharedKolonyOnUpgrade`** — if `ScenarioStoreSystem.CurrentScenarios["KolonizationScenario"]` has any existing `STATUS_NODE` children AND `AgencySystem.PerAgencyEnabled` is true AND any registered agency has empty `KolonyEntries`:
  ```text
  WARNING [fix:MKS-R2] Pre-0.31 upgrade detected: shared KolonizationScenario has N existing
  entries (vessels: V1, V2, ...) that will be STRIPPED from the projected view on first per-agency
  client connect. Existing shared kolony research does NOT migrate to specific agencies.
  Operator options:
    (1) Reset the affected vessels' kolony progress via removing them from the
        canonical KolonizationScenario file before first per-agency connect (Universe/Scenarios/
        KolonizationScenario.txt). Players accept the loss.
    (2) Stamp each vessel's lmpOwningAgency to its intended owner BEFORE first per-agency
        connect (Universe/Vessels/{guid}.txt, add `lmpOwningAgency = {agency-guid:N}`). Then the
        first post-upgrade kolony mutation routes the entry into the right agency.
    (3) Stay on shared-agency mode (PerAgencyCareer=false) — no data loss; kolony continues
        as shared accumulation under the Phase 3 gate=off path.
  Set AllowEnablePerAgencyOnExistingUniverse=true in Settings/GameplaySettings.xml to acknowledge
  and proceed; otherwise the server refuses startup.
  ```
- **`WarnAboutSharedPlanetaryOnUpgrade`** — same shape, checks `PlanetaryLogisticsScenario`. Operator options similar:
  - (1) Drain warehouses to a known agency's vessel before flipping the gate.
  - (2) Document that planetary balances in the shared store stay frozen on disk under gate=on (the projector strips them from outgoing blobs, but the canonical scenario file retains them — flipping back to gate=off recovers visibility).
  - (3) Stay on shared mode (planetary's pre-existing multi-writer hazard remains but balances persist).
- **`WarnAboutSharedOrbitalOnUpgrade`** — checks `ScenarioOrbitalLogistics`. **In-flight transfer outcome (upgrade-lens finding #2 — MUST DOCUMENT):** any `OrbitalLogisticsTransferRequest` with `Status == Launched` or `Status == Returning` at upgrade time has its destination's `OwningAgencyId` evaluated on first per-agency connect via the §2.a stamp logic — if the destination vessel's `lmpOwningAgency` is unset (pre-0.31 vessels), the stamp branch (b) at `VesselDataUpdater.cs:136-139` assigns ownership to the first agency that proto-resends the destination. The in-flight transfer then becomes owned by that first-sender agency. Other peers' projections strip the transfer from their `PendingTransfers` view (privacy correct), but the first-sender agency's player executes the delivery normally on arrival.
  
  Operator advice in the warning text:
  ```text
  WARNING [fix:MKS-R2] Pre-0.31 upgrade detected: shared ScenarioOrbitalLogistics has N pending
  transfers and M recently-expired transfers. Pending transfers' delivery authority resolves to
  the first agency that proto-resends each transfer's destination vessel after the gate flips.
  Operator options:
    (1) Cancel all pending transfers via the MKS in-game UI BEFORE upgrade (each transfer's
        Origin gets its resources back via the cancellation path). Recommended for high-value
        transfers.
    (2) Stamp the destination vessels' lmpOwningAgency BEFORE first connect so delivery resolves
        to the intended agency.
    (3) Stay on shared mode — pending transfers continue under the Phase 3 gate=off Deliver-prefix
        path (single-executor via Update-lock; strict improvement on pre-Phase-3 double-spend).
  Set AllowEnablePerAgencyOnExistingUniverse=true to acknowledge and proceed.
  ```

**Flip-back-out behaviour (upgrade-lens finding #5).** If an operator enables Phase 3 + per-agency mode, plays a session, then flips `PerAgencyCareer=false`: per-agency `KolonyEntries` / `PlanetaryEntries` / `OrbitalTransfers` in `Universe/Agencies/{guid}.txt` files are FROZEN on disk (no router runs to update them under gate=off). The shared `KolonizationScenario` / etc. files continue accumulating from the legacy 30s SHA pass. On a future flip back to gate=on, the frozen per-agency entries reappear stale relative to the now-diverged shared state. Operators flipping the gate back should be aware that round-tripping is NOT lossless. **Add a startup diagnostic at gate transitions: when `AgencySystem.LoadExistingAgencies` detects any `KolonyEntries`/`PlanetaryEntries`/`OrbitalTransfers` content under gate=off, emit `INFO [fix:MKS-R2] Per-agency MKS state exists on disk but PerAgencyCareer=false; entries are frozen and will be stale if the gate flips back. Consider clearing Universe/Agencies/*.txt MKS child nodes before re-enabling.`**

**Operator-actionable recipe (upgrade-lens finding #6, addressed in the kolony warning text above):** the recipe is "stamp the destination vessel's `lmpOwningAgency` before first per-agency connect." Slice E ships a `setvesselagency vesselId agencyId` admin command (a thin wrapper around the existing 5.18d transferagency mechanics) so operators don't need to hand-edit `Universe/Vessels/{guid}.txt`. Currently in §10 (out-of-scope for routers, included in Slice E migration tooling).

### 4.e Admin command refusal under gate=on + `transferagency` MKS-migration contract

**Admin command refusal under gate=on:** same pattern as the 5.17c `setfunds` / `setscience` refusal. Phase 3 doesn't introduce new admin commands today (Slice E ships `setvesselagency` for upgrade-recipe support per §4.d).

**`transferagency` MKS-migration contract (consumer-review finding #1 + upgrade-lens finding #3 — MUST FIX).** Stage 5.18d `transferagency` shipped non-MKS-aware in commit `eb4ef6e2`; it moves vessel `OwningAgencyId` from A → B but does NOT touch `AgencyState[A].KolonyEntries` / `PlanetaryEntries` / `OrbitalTransfers`. Without an MKS-aware extension, post-Phase-3 a transferred vessel's accumulated MKS state becomes orphaned in source agency A (invisible to dest agency B; persists on disk indefinitely; appears in A's per-agency projection until A is also deleted). This is a real upgrade-cohort hazard the moment the first admin runs `transferagency` against an MKS-bearing vessel.

**Phase 3 ships the documentation contract; Slice E (or a follow-up commit on `feature/per-agency-mks`) ships the migration extension.** The contract:

**Lock ordering rule** (required to avoid AB-BA deadlock with concurrent transfers):

```text
Sort {source_agency_id, dest_agency_id} ordinally; acquire AgencySystem.GetAgencyLock(lower)
then GetAgencyLock(higher). Same rule as ScenarioDataUpdater.GetSemaphore's BUG-033 precedent.
A concurrent transferagency in the opposite direction will then serialize against this ordering
rather than deadlocking.
```

**Per-router migration rules:**

- **Kolony (`KolonyEntries`)** — vessel-keyed by `$"{vesselId:N}|{bodyIndex}"`. Migration: for each entry where the key prefix matches the transferred vessel's Guid, MOVE the entry from `AgencyState[A].KolonyEntries` to `AgencyState[B].KolonyEntries` (atomic dict-remove + dict-add under the dual lock). If `B` already has an entry for the same key (shouldn't happen by construction — vessel only belongs to one agency at a time — but defensively), prefer A's entry (more recent if A held the vessel until now). Persist both agencies via `SaveAgency(A)` + `SaveAgency(B)` after the migration. Wire echoes: `SendKolonyStateToOwner(A_client, A_agencyId, removedEntries)` (so A's mirror clears them) + `SendKolonyStateToOwner(B_client, B_agencyId, addedEntries)` (so B's mirror picks them up).

- **Planetary (`PlanetaryEntries`)** — body-keyed by `$"{bodyIndex}|{resourceName}"`, NOT vessel-keyed. Migration policy: **planetary entries do NOT migrate on `transferagency`**. The entries represent the resource quantity in a body's logistics pool, not the vessel that contributed to it. A transferred vessel's prior contributions stay in source agency A's per-agency planetary pool; B starts fresh on that body's planetary pool. Document this in the `transferagency` admin help text + emit a `[fix:MKS-R2] transferagency moved vessel V from A to B; A's planetary logistics pool on body X retains historical contributions from V` info log per moved vessel that previously contributed to planetary state. Operators wanting to migrate planetary balances must hand-edit `Universe/Agencies/{guid}.txt` (out-of-scope for Phase 3).

- **Orbital (`OrbitalTransfers`)** — transfer-keyed by Guid; each transfer carries `OriginVesselId` + `DestinationVesselId`. Migration rules:
  - Transfer where Destination is the moved vessel: MOVE the entry (destination's owning agency executes Deliver per §2.d; new destination owner is B).
  - Transfer where Origin is the moved vessel: COMPLEX — Origin's resources were already deducted at Launch time (in A's frame of reference). Move-or-keep is a product decision. **Default policy: KEEP in A.** The transfer continues to deliver to its destination; A loses the vessel but retains the in-flight obligation. Document in admin help.
  - Transfer where neither Origin nor Destination is the moved vessel: no migration.
  - For each moved orbital transfer: `SendOrbitalStateToOwner(A_client, ...)` + `SendOrbitalStateToOwner(B_client, ...)` as for kolony.

**Wire-echo ordering rule:**

```text
Mutate AgencyState first (under dual lock). Persist both agencies. Then send wire echoes
in order: A's owner-removal echo, B's owner-add echo. Echoes can race within Lidgren's
per-channel ordering; the per-channel-ReliableOrdered guarantee on AgencySrvMsg (channel 22)
ensures both sides arrive in send order. The 5.18d AgencyVisibilityMsgData broadcast (which
fires on the vessel-ownership change itself) is on the SAME channel and is sent BEFORE the
per-router echoes per slice-D-of-5.18d's ordering — Phase 3's echoes ride after the
Visibility broadcast so clients observe ownership change → router state catch-up in order.
```

**Transactionality:** the dual-lock-acquire + state mutation + persist is the atomic unit. The wire echoes are NOT part of the atomic unit (they happen after disk persist). If the server crashes between persist and echo, the next handshake catchup (§2.b.i/ii/iii catchup paragraphs) brings B's mirror current — no client-visible inconsistency.

**Race against concurrent kolony postfix:** if a postfix on A's vessel fires concurrently with the `transferagency` move, the per-entry router holds `AgencySystem.GetAgencyLock(A_agencyId)` while writing; the transferagency move holds it too. The lock serializes both. The postfix either runs BEFORE the move (entry lands in A; move then transfers it to B) or AFTER (entry would land in A but the vessel is now in B; the `RejectIfCrossAgencyWrite` check at server router level drops the entry because vessel's `OwningAgencyId` is now B, not A — A's player's mutation message arrives stale and is dropped silently). End-state correct: the entry only ever lands in one place.

**Slice E ship target.** Phase 3's Slice E adds: (a) the MKS-aware `transferagency` extension implementing the above contract; (b) the `setvesselagency` thin-wrapper command for upgrade-recipe use (§4.d kolony warning option 2); (c) integration tests covering migration + concurrent-mutation races; (d) admin help text updates documenting the per-router policies.

---

## 5. Out-of-scope items

| Surface | Why not Phase 3 | Where it lives |
|---------|-----------------|----------------|
| WOLF per-agency partition (depots / routes / hoppers / terminals / crew routes) | Different scenario module; different ID schemes; needs its own pre-spec. | Phase 4 (handoff §R4, 2-3 weeks). |
| Unloaded converter catch-up under per-agency | Tied to R3 catch-up; needs Strategy B integration. | Phase 5 (handoff §R3, optional product). |
| `ContractSystem` scenario projection | Already deferred to a future step; not MKS-specific. | Stage 5.18d-or-later per CLAUDE.md note. |
| Per-agency kolony-yield → Funds/Sci/Rep reward routing | Phase 3 stores yield scalars as opaque per-agency state. If MKS' kolony rewards interact with `Funding.Instance` / `ResearchAndDevelopment.Instance`, routing those scalars through the band-1 currency routers needs a verify-at-implementation pass. | Verify at implementation; if interaction exists, add to `AgencyCurrencyRouter` / `AgencyResearchRouter` extension. |
| MKS' `ModuleColonyRewards.cs:33` — `TrackLogEntry` call site #2 | Phase 3's postfix on `KolonizationManager.TrackLogEntry` is a hook on the MANAGER, so it SHOULD catch every entry source uniformly including `ModuleColonyRewards`. The call-site exists (grep verified at MKS SHA `ed0f6aa6`); end-to-end "postfix-sees-rewards-entry" is documented as **§11 Q1 verify-at-slice-B sanity check**, not pre-verified. | Verify at Slice B impl per §11 Q1. |
| WOLF and USI-LS gameplay mods | Separate brief (handoff §1, §12 explicit out-of-scope). | Out-of-scope, future track. |
| Server-side delivery authority for orbital transfers | Operator confirmed client-side prefix is the right approach (session 25 Q3 sign-off). | Out-of-scope by design. |
| Smooth UI for cross-agency kolony radius observation | If Bob is in physics range of Alice's kolony, Bob's UI shouldn't claim Alice's bonuses — that's covered. But what does Bob's *observation* of Alice's kolony LOOK LIKE in Bob's map view? Cosmetic Phase 5 polish. | Phase 5 / R5 UI polish. |
| Pre-spec validation of post-Phase-3 effort: WOLF + LS interaction | Handoff §R4 covers WOLF in isolation. Once Phase 3 ships, re-audit Phase 4 effort. | Phase 4 pre-spec re-audit. |
| Multi-player-per-agency support (N:1 instead of 1:1) | Phase 3's gate=on design is exact for the 1-player-per-agency Stage 5 product. The §2.d decision-table prose and the lack of within-agency multi-writer hazards both lean on the 1:1 invariant. | Re-derive Phase 3 (and probably Phases 4/5) if a future product decision opens N:1. |
| Gate=off shared-mode multi-writer fix for `PlanetaryLogisticsScenario` | The pre-existing MKS-multiplayer `(BodyIndex, ResourceName)` same-key collision is OUT-of-scope under Phase 3 (§4.b). A cross-vessel-same-key server-side rejection would expand the shared-mode surface beyond the per-agency goal. | Defer to a follow-up shared-mode-MKS-correctness slice (operator decision; §11 open Q10). |
| Pre-upgrade migration tooling beyond `setvesselagency` | The §4.d operator recipe is "stamp `lmpOwningAgency` before first per-agency connect"; Slice E ships `setvesselagency` as a thin wrapper. Bulk-migration (e.g. "stamp all vessels at body X to agency Y") is deferred. | Slice E ships single-vessel; bulk operations are admin-script territory. |

---

## 6. Brittleness — MKS internal namespace + signature surface

Phase 3's hooks against MKS' internal types share the same brittleness class as R0 + R1's MKS-version-mismatch surface:

1. **`KolonizationManager.TrackLogEntry`** — signature: `public void TrackLogEntry(KolonizationEntry logEntry)`. A future MKS refactor that renames the method, changes the parameter type, or splits the entry would silently break the postfix (Harmony patches on missing targets either throw at patch-load or silently skip). **Mitigation:** use `AccessTools.Method(AccessTools.TypeByName("KolonyTools.KolonizationManager"), "TrackLogEntry")` at module-load and emit `[fix:MKS-R2]` warning if resolution fails — self-disabling pattern matches MKS-R0 + MKS-R1.

2. **`ModulePlanetaryLogistics.LevelResources`** — private method (per the file's access modifier at [ModulePlanetaryLogistics.cs:78](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/PlanetaryLogistics/ModulePlanetaryLogistics.cs#L78) — `private void LevelResources`). Harmony patches on private methods work but are MORE brittle to signature change. **Mitigation:** same resolution-time `AccessTools` + self-disable. Implementation should also check `AccessTools.Method(typeof(ModulePlanetaryLogistics), "LevelResources")` returns non-null before patch + emit warning if not. **Consider** asking USI upstream to make `LevelResources` `protected virtual` — `[[reference-mks-external-clones]]` upstream PR candidate alongside the §11 R0 `virtual/protected` seam ask. Bundle the two PRs.

3. **`OrbitalLogisticsTransferRequest.Deliver`** — `public IEnumerator Deliver()`. Public surface; lower brittleness risk. Mitigation: same `TypeByName` self-disable.

4. **`KolonizationEntry` field rename / new field addition** — the 13-field wire shape (`AgencyKolonyEntry`) mirrors the MKS-side struct. A new MKS field would flow through as an opaque addition (forward-compat tail). A renamed field would break the postfix's read of `entry.GeologyResearch` etc. — same brittleness as the postfix anchor. **Mitigation:** reflective read of fields via `FieldInfo.GetValue` at impl time, OR explicit accept-list of known fields with a `LogWarning` on unknown additions (similar to BUG-013 sanitiser's whitelist approach). Verify decision at impl time.

5. **MKS module-rename detection at boot** — append `[fix:MKS-R2] KolonizationManager type resolved` (or `... not found; per-agency kolony routing disabled until MKS is installed`) to the existing MKS-R0 / MKS-R1 module-resolution log lines. Single source-of-truth for operator grep: `grep -E "\[fix:MKS-R[012]\]" KSP.log`.

---

## 7. Test plan

### 7.a Unit tests in `ServerTest` (net10.0, MSTest)

**`ServerTest/AgencyKolonyRouterTest.cs`** (~12 cases):

1. `GateOff_TryRouteReturnsFalse_NoMutation` — `PerAgencyCareer=false`; `TryRoute` returns false; `AgencyState.KolonyEntries` unchanged.
2. `Sandbox_TryRouteReturnsFalse_NoMutation` — Career not set; same as above.
3. `ClientWithoutAgency_TryRouteReturnsFalse` — `AgencyByPlayerName` lookup miss.
4. `SameAgencyVessel_EntryAccepted` — vessel's `OwningAgencyId == requesterAgencyId` → upsert + echo.
5. `CrossAgencyVessel_EntryDropped` — vessel's `OwningAgencyId != requesterAgencyId` → log + skip.
6. `UnassignedSentinelVessel_EntryAccepted` — vessel `OwningAgencyId == Guid.Empty` → bypass (spec §10 Q3).
7. `VesselNotInStore_EntryDropped` — `VesselStoreSystem.CurrentVessels.TryGetValue` miss → skip (defensive).
8. `MalformedVesselId_EntryDroppedBatchContinues` — entry's `VesselId` not Guid-parseable → log + skip + sibling entries upsert OK.
9. `BatchWithMixedAcceptReject_PartialUpsert` — 3 entries (1 accept / 1 cross-agency / 1 malformed) → only the accept lands in state.
10. `RepeatedUpsertSameKey_Replaces` — same `(VesselId, BodyIndex)` upserted twice → second value wins.
11. `ConcurrentUpsertSameKey_LastWriterWins_NoCorruption` — two threads upsert + serialize round-trip → stable end state.
12. `MultipleBodyIndexesSameVessel_DistinctEntries` — same vessel, body 5 and body 8 → two entries in dict.

**`ServerTest/AgencyPlanetaryRouterTest.cs`** (~10 cases): mirror of kolony with `OwningVesselId` (wire-supplied) replacing `entry.VesselId`. Tests bypass cases + cross-agency reject + Unassigned-sentinel + batch isolation.

**`ServerTest/AgencyOrbitalRouterTest.cs`** (~10 cases): mirror with `DestinationVesselId` as the partition key. Plus 2 transfer-status-transition cases (Launched → Delivered status update; cross-agency Status mutation rejected even if entry already exists).

**`ServerTest/AgencyKolonyProjectorTest.cs`** (~8 cases):
1. `GateOff_ReturnsInputUnchanged`.
2. `SandboxMode_ReturnsInputUnchanged`.
3. `ClientWithoutAgency_ReturnsInputUnchanged`.
4. `EmptyAgencyKolony_StripsAllSharedEntries` — agency has 0 entries → outgoing scenario has 0 `KolonizationEntry` children (the strip-then-splice pattern, matches Strategy/Achievement Stage 5.17e-6).
5. `AgencyWithEntries_SplicesOnlyOwn` — agency has 2 entries; shared scenario has 5 (3 from peers); output has exactly the 2 agency entries.
6. `MalformedAgencyEntry_DroppedBatchContinues` — 1 entry has parse-failing Data → output skips it but emits the other.
7. `WholeScenarioParseFailure_ReturnsInputUnchanged` — fall-through fallback fires + logs.
8. `LocaleInvariant_DoublesSerializedCorrectly` — `Funds=1234.56` round-trips through `de-DE` locale without comma swap.

`AgencyPlanetaryProjectorTest` + `AgencyOrbitalProjectorTest` mirror with their respective scenario shapes (~8 cases each).

**`ServerTest/AgencyKolonyStatePersistenceTest.cs`** (~6 cases):
1. `EmptyDict_OmittedFromConfigNodeOutput` — pristine agency files unchanged in shape.
2. `PopulatedDict_RoundTripsViaSerialize` — write + parse + compare.
3. `MissingChildNode_LoadsAsEmptyDict` — forward-compat: older AgencyState file with no `KOLONY_ENTRIES` node loads cleanly.
4. `MalformedEntry_IsolatedAndSkipped` — per-entry parse failure doesn't abort parent agency load.
5. `ConcurrentSaveDuringMutate_NoTornState` — Serialize under lock; concurrent router upsert; resulting bytes round-trip to the after-mutation state (not the half-applied intermediate).
6. `Base64PayloadIntegrity_OrbitalTransfer` — orbital transfer Base64-encoded `PayloadBytes` round-trip preserves byte sequence.

Bumps ServerTest 348 → ~390-400.

### 7.b Unit tests in `LmpClientTest` (net472, MSTest)

**`LmpClientTest/OrbitalDeliveryGateDecisionTest.cs`** (~12 cases on the pure-helper):

1. `GateOff_LockHolderLocal_Execute`.
2. `GateOff_LockHolderRemote_Skip`.
3. `GateOff_LockHolderEmpty_Skip` (transient post-unload — never go-ahead in MP).
4. `GateOn_SameAgency_Execute`.
5. `GateOn_DifferentAgency_Skip`.
6. `GateOn_UnassignedSentinel_LockHolderLocal_Execute`.
7. `GateOn_UnassignedSentinel_LockHolderRemote_Skip`.
8. `GateOn_LocalAgencyUnknown_LockHolderLocal_Execute` (5.18a mirror late-arriving defensive bypass).
9. `GateOn_LocalAgencyUnknown_LockHolderRemote_Skip`.
10. `Status_Cancelled_Passthrough` — non-Launched/Returning Status → return true (let stock handle).
11. `Status_PreLaunch_Passthrough` — pre-launch should never see Deliver; defensive passthrough.
12. `NullTransfer_DefensiveSkip`.

**`LmpClientTest/AgencyKolonyPostfixDecisionTest.cs`** (~8 cases on the postfix's "should I emit a mutation message?" pure helper):

1. `GateOff_LockHolderLocal_Emit_30sShaCovers` — but skip-emit in gate-off (the 30s pass already covers).
2. `GateOff_LockHolderRemote_Suppress` — lock-holder gate (single-writer-per-vessel under shared mode).
3. `GateOn_LocalAgencyOwnsVessel_Emit` — emit per-agency message.
4. `GateOn_LocalAgencyDoesNotOwn_Suppress` — wouldn't be authorised even if emitted; suppress at source.
5. `GateOn_UnassignedSentinelVessel_LockHolderLocal_Emit`.
6. `GateOn_LocalAgencyMirrorEmpty_Suppress` — handshake hasn't arrived yet.
7. `MissingVesselContext_DefensiveSkip` — `entry.VesselId` not present in `AgencyMembership.VesselOwnership`.
8. `MalformedVesselIdString_DefensiveSkip`.

**`LmpClientTest/AgencyPlanetaryPostfixDecisionTest.cs`** (~6 cases): mirror with `this.vessel.id` as the vessel-id source.

Bumps LmpClientTest 123 → ~150.

### 7.c MockClientTest (integration coverage, net10.0)

**`MockClientTest/AgencyKolonyRoutingTest.cs`** (~6 cases against the in-process server):

1. `SameAgencyKolonyMutation_RoutesToOwner_NoPeerLeak` — Alice mints a kolony entry; Bob (different agency) never sees the entry in his per-agency state or in his projected scenario.
2. `CrossAgencyKolonyMutation_Rejected` — Alice's wire send claiming Bob's vessel is dropped; Bob's state unchanged.
3. `KolonyCatchupOnReconnect` — Alice has 3 entries; Alice disconnects; reconnects; the connect-time catch-up reproduces all 3 before the next mutation.
4. `GateOffPassthrough_30sShaUnchanged` — under `PerAgencyCareer=false`, kolony scenario flows the legacy SHA path (positive control for dual-mode silence).
5. `KolonyEntriesPersistAcrossServerRestart` — write + restart server + verify entries reload from disk.
6. `MalformedKolonyEntryFromWire_DroppedBatchContinues` — Lidgren-level corrupt entry doesn't kill the batch.

**`MockClientTest/AgencyPlanetaryRoutingTest.cs`** (~4 cases): mirror.

**`MockClientTest/AgencyOrbitalRoutingTest.cs`** (~6 cases): mirror + 2 transfer-state-machine cases (Launched → Delivered status echo; cross-agency status mutation rejected).

Bumps MockClientTest 71 → ~95-100.

### 7.d Two-client smoke (operator-driven, ~60 min)

Bundled with the existing `[[project-mks-smoke-backlog]]` items. Adds a fifth scenario to the operator's KSPPATH2 single-session bundle.

**Phase 3 smoke (3 sub-tests):**

1. **Cross-agency kolony privacy:**
   - Two clients (Alice / Agency A; Bob / Agency B), both flying MKS vessels at the same body.
   - Alice's vessel mines kolony research for ~10 minutes (warp + auto-converters).
   - **Acceptance:** Bob's `KolonizationManager.Instance.KolonizationInfo` does NOT contain Alice's entries. Bob's `GetGeologyResearchBonus(bodyIndex)` shows ONLY Bob's own contribution.
2. **Cross-agency planetary warehouse privacy:**
   - Alice's warehouse pumps Hydrates into the planetary store; Bob's warehouse on the same body pumps Karbonite.
   - **Acceptance:** Alice's `PlanetaryLogisticsManager.PlanetaryLogisticsInfo` shows ONLY her Hydrates entry; Bob's shows ONLY his Karbonite entry. Neither sees the other's resource.
3. **Cross-agency orbital double-spend prevention:**
   - Alice creates an orbital transfer from her Mun station (Origin) to her Mun lander (Destination), 5 minutes flight time.
   - Bob is in physics range of Alice's lander, in a different subspace.
   - **Acceptance:** When `transfer.GetArrivalTime() <= UT`, ONLY Alice's client mutates the destination's resource amounts. Bob's `KSP.log` shows `[fix:MKS-R2] Deliver skipped: cross-agency destination` (or analogous). Destination's resource counter shows ONE delivery worth of additions, not two.

### 7.e Log signal to grep

```bash
grep -E "\[fix:MKS-R2\]" KSP.log
grep -E "\[fix:per-agency-career\].*(Kolony|Planetary|Orbital)" KSP.log
```

Expected post-fix in a two-client per-agency scenario:
- Module-load: one `[fix:MKS-R2] KolonizationManager / ModulePlanetaryLogistics / OrbitalLogisticsTransferRequest type resolved` line each at boot.
- Runtime: occasional Debug-level lines for cross-agency Deliver-skip + cross-agency mutation-route rejection.
- Zero warnings unless MKS-version-mismatch.

---

## 8. Acceptance criteria

**Phase 3 is NOT complete until Slice E ships** (review-revision logic-pass finding #10). Slices A-D close the per-router design but leave the `transferagency`-MKS migration contract (§4.e) unimplemented — running `transferagency` against an MKS-bearing vessel without Slice E orphans accumulated per-agency MKS state on the source agency (the MUST-FIX hazard from consumer-review #1 + upgrade-lens #3). Acceptance criteria below are the FULL Phase 3 ship list including Slice E; partial ships (A-D only, or A-D + smoke without E) are intermediate milestones, not "Phase 3 done."

- [ ] Three router files + projector splice extension + AgencyState 3-field addition + 3 wire-MsgData families + 3 enum slots + IgnoredScenarios gate-conditional filter (Option B per §3.g) all land.
- [ ] `dotnet build -c Release` clean on `Server.csproj` (no NEW warnings vs pre-existing 29-30).
- [ ] `dotnet build -c Release` clean on `LmpClient.csproj` (no NEW warnings vs pre-existing 7 + MKS-R0/R1 baseline).
- [ ] `dotnet test ServerTest` passes — 348 → ~390-400 (within ±5 of slice estimate).
- [ ] `dotnet test LmpClientTest` passes — 123 → ~150.
- [ ] `dotnet test MockClientTest` passes — 71 → ~95-100.
- [ ] Server boot banner shows `MKS-R2` in `[fork] ... fixes active: ...`.
- [ ] `/fork` JSON includes `"MKS-R2"` in `ActiveFixes`.
- [ ] KSP.log contains 3 module-resolution log lines at boot (one per anchor) when LMP + MKS are both loaded.
- [ ] Two-client smoke (§7.d): cross-agency privacy holds for all 3 surfaces; no double-spend on orbital deliveries.
- [ ] Gate=off regression smoke: with `PerAgencyCareer=false`, shared kolony / planetary / orbital scenario state propagates exactly as pre-Phase-3 (positive control for dual-mode silence).
- [ ] Pre-0.31 upgrade-in-place: 3 new `WarnAboutShared*OnUpgrade` boot diagnostics fire AND wire into `RefuseStartupIfUpgradeHazardWithoutOverride` (upgrade-lens finding #1 — server refuses startup when an existing universe has shared MKS scenario state and `AllowEnablePerAgencyOnExistingUniverse=false`; flipping the override unblocks startup with the warnings still emitted).
- [ ] Gate transition diagnostic: when `AgencySystem.LoadExistingAgencies` detects per-agency MKS state on disk while gate=off, an INFO log fires explaining the flip-back-out hazard (upgrade-lens finding #5).
- [ ] `transferagency` MKS-aware extension shipped in Slice E: kolony entries migrate vessel→destination-agency, planetary entries do NOT migrate (documented), orbital transfers move Destination but keep Origin in source-agency (§4.e per-router policies + dual-lock ordering enforced).
- [ ] `setvesselagency` admin command shipped in Slice E (thin wrapper around `transferagency` mechanics) for upgrade-recipe use per §4.d kolony warning option 2.
- [ ] No regression on Phase 2 (MKS-R1 still snaps subspaces correctly under per-agency mode — orthogonal surfaces).
- [ ] No regression on Phase 1.5 R0 (MKS-R0 depot-list filter still applies — orthogonal surfaces).

---

## 9. Effort estimate

Handoff §Phase 3 says 6-8 weeks. With this pre-spec in hand, slice as follows:

| Slice | Effort | Notes |
|-------|--------|-------|
| Pre-spec (THIS commit, docs-only) | ~1 day (session 25) | This document + multi-lens review pass. |
| **Slice A — `AgencyState` fields + ConfigNode round-trip + ServerTest persistence cases** | ~3 days | 3 new fields, ConfigNode emit + parse + per-entry isolation, ~6 persistence test cases. Foundation that the routers + projector all build on. |
| **Slice B — `AgencyKolonyRouter` + wire `KolonyState` (slot 6) + projector splice + IgnoredScenarios entry + boot diagnostic + ServerTest + MockClientTest** | ~2 weeks | Closest analogue to `AgencyContractRouter` shipped in 5.17d. One router, one wire family, one projector splice, one upgrade diagnostic. Multi-lens review per [[feedback-review-lens-framing]]. |
| **Slice C — `AgencyPlanetaryRouter` + wire `PlanetaryState` (slot 7) + projector splice + IgnoredScenarios entry + boot diagnostic + ServerTest + MockClientTest** | ~2 weeks | Same shape as B; planetary partition derives from `OwningVesselId` (wire-supplied by client postfix). |
| **Slice D — `AgencyOrbitalRouter` + wire `OrbitalState` (slot 8) + projector splice + IgnoredScenarios entry + boot diagnostic + ServerTest + MockClientTest + Deliver prefix + LmpClientTest decision-helper cases** | ~3 weeks | Largest slice — three responsibilities (transfer-list partition + Deliver-gate prefix + transfer-state-machine echo). Operator confirmed client-side Deliver gate (session 25 Q3); pure-helper + 12 unit tests per the convention. |
| **Slice E — Integration tests + cross-router MockClientTest scenarios + smoke prep + `transferagency` MKS-aware extension + `setvesselagency` admin command** | ~1.5 weeks | Cross-router interactions (kolony+planetary on the same vessel under per-agency); gate-off regression coverage; smoke checklist authored for operator. ALSO ships the §4.e `transferagency` MKS-aware extension (per-router migration rules + dual-lock ordering + wire echoes) AND the `setvesselagency` thin-wrapper command for upgrade-recipe use (§4.d warning option 2). Without these, post-Phase-3 admin operations leave orphaned MKS state on source agencies. |

**Slice ordering rationale (consumer-review finding #5):**

- Slice A → B is sequential (B's router needs A's `AgencyState.KolonyEntries` field).
- Slice B → C is INDEPENDENT after Slice A — C reuses the same router/projector/wire patterns from B but doesn't depend on B's runtime artifacts. Could be developed in parallel by two contributors if available.
- Slice D depends on Slice A only (its `AgencyOrbitalRouter` reads `AgencyState.OrbitalTransfers` from the A foundation; its Deliver-prefix decision-helper uses pre-existing `LockSystem.LockQuery.GetUpdateLockOwner` (Stage 5.17a) and `AgencyMembership.TryGetOwningAgency` (Stage 5.18b)). **Could ship before C if a contributor wants to land the most user-visible improvement (orbital double-spend fix) first.**
- Slice E depends on B + C + D. Bundles the `transferagency` MKS extension + cross-router tests.
| Multi-lens review per slice | ~30 min agent time + ~30-60 min applying findings, per slice | 4 sub-slices × 4 review rounds (general / consumer / upgrade / cross-slice convergence) = ~8 hours total agent + apply. |
| Two-client smoke (KSPPATH2 operator session) | ~60 min operator (bundled with backlog) | `[[project-mks-smoke-backlog]]` adds Phase 3 as item 5. |

**Total dev time: ~8-9 weeks implementation + review, consistent with the handoff's 6-8 week range with realistic per-slice review overhead.**

The hard remaining gate is the two-client smoke — bundled with the existing 4 backlogged items, total 5 smokes per operator session.

**Slice-per-router-per-commit** is the right granularity: B / C / D each land independently with their own multi-lens review pass. Slice A is the foundation that lands first; Slice E ships after C+D land + any cross-slice issues surface.

---

## 10. What this pre-spec deliberately does NOT design

- **Implementation code.** Per audit-via-pre-spec discipline; next session writes Slice A (`AgencyState` fields + ConfigNode round-trip + ServerTest persistence cases) against these anchors.
- **WOLF per-agency partition.** Phase 4 territory; separate pre-spec.
- **R3 unloaded-converter catch-up under per-agency.** Phase 5 territory; needs Strategy B integration.
- **`ContractSystem` per-agency scenario projection.** Already deferred outside Phase 3 in CLAUDE.md; not MKS-specific.
- **Per-agency kolony-yield → currency interaction.** Verify at implementation; if interaction exists, extend `AgencyCurrencyRouter` / `AgencyResearchRouter`.
- **Reverse `transferagency` MKS-aware extension** — when admin transfers a vessel A→B, do the matching `KolonyEntries` rows migrate? Phase 3 stores the data; the migration logic lives in the Stage 5.18d `transferagency` command, which needs an MKS-aware extension once Phase 3 ships. Document the contract here; defer the migration code.
- **`PlanetaryLogisticsScenario` shared-pool slot freeing analogous to AgencyContractRouter's `RemoveContractFromSharedOfferedPool`** — planetary entries don't have an Offered analogue (there's no peer-acceptance race for a planetary entry). The orbital `PendingTransfers` list has a similar concern but is handled by the per-agency partition itself (a transfer Alice owns never appears in Bob's projected pending list, so Bob can't "accept" or "deliver" it). No shared-pool slot to free.
- **`ScenarioOrbitalLogistics.AbortTransfer` / `ResumeTransfer` per-agency authority** — UI-driven mutations. Same prefix-gate pattern as Deliver but on the UI methods. Defer to slice D; verify whether they also need the prefix or if the existing Update-lock check on the destination vessel suffices.
- **MKS' `KolonizationSetup.Config` per-agency override** — Phase 3 partitions ENTRIES, not the kolony GAME-RULES config (efficiency multipliers, base bonuses). Game rules stay global per-server. If operators want per-agency tuning, that's a separate config surface.

---

## 11. Open questions (flag before implementation if any of these turn out wrong)

1. **Does MKS' `ModuleColonyRewards.cs:33` also need a postfix anchor?** The `KolonizationManager.TrackLogEntry` postfix should catch ALL entry mutations regardless of source (rewards module is just another caller). Verify at slice B that the `TrackLogEntry` postfix sees the rewards-driven entries (one-line sanity check at slice-B impl).

2. **`KolonizationEntry.Funds` / `Science` / `Rep` scalar fields — do these interact with `Funding.Instance` / `ResearchAndDevelopment.Instance` / `Reputation.Instance`?** The fields exist on the entry (line 12-14) — but it's unclear if MKS' cycle credits them to KSP's stock currency totals or treats them as MKS-internal accounting. **Verify at slice B impl** via `Find Usage` on those fields in MKS source. If they DO credit stock currency, the per-agency routing in band-1 `AgencyCurrencyRouter` / `AgencyResearchRouter` already handles the resulting KSP-event-driven mutations; no Phase 3 extra hook needed. If they DON'T (pure MKS accounting), this is no-op for Phase 3.

3. ~~**`OrbitalLogisticsTransferRequest._destinationId` is stored as `vessel.persistentId.ToString()` (uint), NOT `vessel.id.ToString()` (Guid).**~~ **RESOLVED (session 25, general-review finding #6):** see §2.b.iii "Vessel-id derivation" — Phase 3 derivation path uses `OrbitalLogisticsTransferRequest.Destination` accessor (which handles both forms), then reads `vessel.id` (canonical Guid). Pure helper `ResolveDestinationVesselGuid(transfer)` hides the choice from the test surface; falls back to `_destinationModuleId` for vessel-id-reassignment-surgery edge cases.

4. **Does `ScenarioOrbitalLogistics.PendingTransfers` get cleared on reconnect (client side)?** Verified by reading [ScenarioOrbitalLogistics.cs:52-53](file:///F:/tmp/mks-external/MKS/Source/KolonyTools/OrbitalLogistics/ScenarioOrbitalLogistics.cs#L52-L53) — `OnLoad` calls `PendingTransfers.Clear()` + `ExpiredTransfers.Clear()` BEFORE re-populating from the scenario node. Standard KSP `ScenarioModule.OnLoad` is invoked by `ProtoScenarioModule.Load` on scene-load + on scenario apply. The §2.d Deliver-prefix's bounded re-skip cycle (transfer reappears in PendingTransfers on next sync) relies on this OnLoad re-populate. **No client-side Harmony patch needed for the OnLoad path.** Still flag at slice D: verify the patched delivery state DOESN'T get re-broadcast as the owner's scenario before the actual owner-side delivery completes (a race that could re-apply the Failed-Delegated status to the owner's local state).

5. **`AgencyMembership.TryGetOwningAgency` (Stage 5.18b) returns `Guid` — distinguishes "not in registry" from "Empty (Unassigned sentinel)" via return-bool convention.** The Deliver-prefix's pure-helper signature needs to match — `Func<Vessel, Guid?>` (nullable to distinguish unknown from Empty). Verify against the actual 5.18b helper at slice D impl; adjust signature.

6. **MKS' kolony per-tick cadence under high warp / high vessel-count.** A megabase with 50 MKS converter parts can fire `TrackLogEntry` 50× per `FixedUpdate` × KSP's adjusted-fixed-cadence-during-warp. Per-postfix wire send may produce hundreds of messages/sec. **Soak this at slice B implementation** — if observed cadence > ~50 msg/sec sustained, add a per-batch coalescing layer to `AgencyKolonySender` (collect entries on one tick, send one batched message next tick). Defer the coalescing until measured to avoid premature optimization.

7. ~~**Should the Deliver-prefix's `getUpdateLockOwner` check happen even under gate=on?**~~ **RESOLVED (session 25, post-1:1-invariant reminder):** Per the 1-player-per-agency invariant (§2 preamble), under gate=on the agency check is the sole primary authority and the lock check is logically redundant. The lock check IS retained in the decision table (§2.d) for code-uniformity (same code path serves both gates), gate=off authority (sole gate=off predicate), and defensive handling of the Unassigned-sentinel + connect-race-window cases. Evaluated uniformly with negligible cost; revisit ONLY if a future product decision opens N-players-per-agency.

8. **NEW — Operator policy for `transferagency` of orbital transfer Origin.** §4.e proposes "default policy: KEEP in source agency" when the moved vessel is the Origin of an in-flight transfer. The operator may want a different rule (e.g., "move with the destination" — orbital transfers conceptually move resources; the destination's owner gets the delivery, but the source's transfer-list entry stays on the originator's "what did I ship out" side). **Operator decision before slice E ships.** Recommendation: surface the question via a single-select prompt during the slice E implementation session.

9. **NEW — Operator policy for planetary balance migration on `transferagency`.** §4.e proposes "planetary entries do NOT migrate." Operators may want a `--migrate-planetary` flag on `transferagency` that DOES move the entries (with the caveat that body-keyed entries can collide with B's existing pool). **Operator decision before slice E ships.**

10. **NEW — Gate=off planetary contention.** §4.b documents the pre-existing `(BodyIndex, ResourceName)` collision hazard as a known limitation. Operator may want Phase 3 to ALSO ship a server-side rejection-on-cross-vessel-same-key under gate=off (returning false from the postfix when another vessel just wrote the same key in the last N seconds). **Defer this decision** — it's outside the per-agency scope and would expand Phase 3's gate=off surface meaningfully. Documented for visibility; consider for a follow-up shared-mode-MKS-correctness slice.

---

## 12. Cross-references

- **Handoff doc** (`mks-lmp-compatibility-handoff.md` v3.3) — architectural framing (§R1, §R2, §Phase 3, §1 v3.3 per-agency framing).
- **Phase 2 pre-spec** (`mks-lmp-compatibility-phase-2-prespec.md`, commit `e75e5d9a`) — 12-section template this doc mirrors.
- **Phase 1.5 R0 pre-spec** (`mks-lmp-compatibility-phase-1.5-prespec.md`, commit `890288c0`) — earlier pre-spec instance.
- **Phase 1.5 R0 implementation** (commit `d1822cdd`) + **Phase 2 implementation** (commit `3a355618`) — three-reviewer-lens (general + consumer + upgrade) template to mirror per slice here.
- **`AgencyContractRouter`** ([Server/System/Agency/AgencyContractRouter.cs](file:///F:/luna-multiplayer-mks/Server/System/Agency/AgencyContractRouter.cs)) — closest structural template (Stage 5.17d). Router shape + per-contract exception isolation + shared-pool slot freeing + connect-catchup-via-HandshakeSystem pattern.
- **`AgencyScenarioProjector`** ([Server/System/Agency/AgencyScenarioProjector.cs](file:///F:/luna-multiplayer-mks/Server/System/Agency/AgencyScenarioProjector.cs)) — projection pattern. Phase 3 adds 3 switch cases + 3 splice methods.
- **`AgencyState`** ([Server/System/Agency/AgencyState.cs](file:///F:/luna-multiplayer-mks/Server/System/Agency/AgencyState.cs)) — data class with ConfigNode round-trip. Phase 3 adds 3 fields + 3 child-node emit/parse blocks.
- **`LockSystem`** ([Server/System/LockSystem.cs](file:///F:/luna-multiplayer-mks/Server/System/LockSystem.cs)) — 5.17a cross-agency guard template (lines 83-101). Phase 3 reuses the trust-posture rationale for the Deliver-prefix decision table.
- **`VesselDataUpdater`** ([Server/System/Vessel/VesselDataUpdater.cs](file:///F:/luna-multiplayer-mks/Server/System/Vessel/VesselDataUpdater.cs)) — 5.16b `OwningAgencyId` stamp pattern (lines 130-144). Phase 3 reads `vessel.OwningAgencyId` for partition decisions; depends on this stamp being authoritative.
- **`AgencySystem.PerAgencyEnabled`** ([Server/System/Agency/AgencySystem.cs](file:///F:/luna-multiplayer-mks/Server/System/Agency/AgencySystem.cs#L58-L60)) — combined gate (PerAgencyCareer && GameMode==Career). Every Phase 3 path checks this property, never the raw setting.
- **`ScenarioSystem.SendScenarioModules`** ([Server/System/ScenarioSystem.cs:70-111](file:///F:/luna-multiplayer-mks/Server/System/ScenarioSystem.cs#L70-L111)) — existing projection invocation point (line 96). Phase 3 needs zero edits here; the projector itself gains new switch cases.
- **`IgnoredScenarios`** ([LmpCommon/IgnoredScenarios.cs](file:///F:/luna-multiplayer-mks/LmpCommon/IgnoredScenarios.cs)) — `IgnoreSend` extension point.
- **`AgencyMessageType` enum** ([LmpCommon/Message/Types/AgencyMessageType.cs](file:///F:/luna-multiplayer-mks/LmpCommon/Message/Types/AgencyMessageType.cs)) — slot append.
- **`AgencySrvMsg` + `AgencyCliMsg`** — `SubTypeDictionary` lockstep additions.
- **`AgencyContractMsgData`** ([LmpCommon/Message/Data/Agency/AgencyContractMsgData.cs](file:///F:/luna-multiplayer-mks/LmpCommon/Message/Data/Agency/AgencyContractMsgData.cs)) — wire-message structural precedent (owner-only echo + MaxCount DoS guard + caller-contract non-null array slice).
- **`AgencyVisibilityMsgData`** ([LmpCommon/Message/Data/Agency/AgencyVisibilityMsgData.cs](file:///F:/luna-multiplayer-mks/LmpCommon/Message/Data/Agency/AgencyVisibilityMsgData.cs)) — broadcast-vs-owner-only XML doc convention.
- **MKS source pins** (`F:\tmp\mks-external\MKS\` at `ed0f6aa6`) — all MKS-side line references verified at this SHA. Re-pin if Phase 3 implementation lands more than ~30 days after audit date (per [[reference-mks-external-clones]]).
- **`feedback_audit_via_prespec.md`** — pre-spec discipline; this doc is the Phase 3 instance.
- **`feedback_review_lens_framing.md`** — multi-lens review per slice. Consumer-lens here = "the operator running per-agency mode whose MKS bases produce double-counted research"; upgrade-lens = "the operator with an existing pre-Phase-3 universe whose shared kolony state will be stripped on first per-agency connect"; client-harmony-lens = "the KSP modder whose Harmony patch on KolonizationManager interacts with ours."
- **`feedback_user_facing_naming.md`** — wire enum slot names chosen by lifecycle + intent (`KolonyState` / `PlanetaryState` / `OrbitalState` — state snapshots, owner-only); not by per-mutation event shape.
- **`reference_agency_wire_extension.md`** — 6-step recipe for each of the 3 new MsgData families; recipe step 7 (arrival-conditions + client-write-path + orthogonal-concerns XML doc) added per Stage 5.17d consumer-lens finding.
- **`project_mks_smoke_backlog.md`** — bundle Phase 3 smoke as item 5 with the existing 4 backlogged validations.
- **`project_mks_compat_branch.md`** — parent project memory; updates when Phase 3 ships.
- **`reference_mks_external_clones.md`** — external clone SHAs (re-pin if implementation lands more than 30 days after audit).
- **CLAUDE.md Stack Notes** — "Relayed vessel-proto bytes are advisory, not authoritative" (Stage 5.18b) — Phase 3 reads `vessel.OwningAgencyId` from the AUTHORITATIVE server-side `VesselStoreSystem.CurrentVessels` store, NEVER from relayed proto bytes. Same precedent as 5.17a + write-path counterpart.
- **CLAUDE.md Stack Notes** — "Server-relay path needs an explicit cross-agency write-side guard" (5.17a write-path counterpart, soak Finding 2) — Phase 3's Deliver-prefix trust posture relies on this existing guard to prevent cross-agency resource-state propagation when a bypassed prefix client tries to broadcast.
- **CLAUDE.md Stack Notes** — "Per-agency contract routing must free the shared Offered slot on Accept" (Stage 5.17d upgrade-lens finding) — Phase 3 documents the NON-applicability of this pattern (no Offered analogue for kolony/planetary; orbital partition itself handles the equivalent).
