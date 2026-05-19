# Phase 4 — WOLF per-agency partition (R4) — pre-spec

**Status:** PRE-IMPLEMENTATION. Drafted 2026-05-19 (session 39) after the Stage 5.18g untrusted-cohort hardening ship at `0d99a81a` and operator decision to proceed to Phase 4 alongside post-v3 mod-compat slices + 5.18g hardening for the v4 release.

**Branch:** `feature/per-agency` (the unified branch — see `[[project-mks-merge-to-per-agency]]`; the historical `feature/per-agency-mks` branch + worktree were deleted 2026-05-19 after Phase 3 merged to `feature/per-agency` at `d4ff0511`).

**Tip at drafting:** `0d99a81a` (Stage 5.18g — untrusted-cohort hardening, shipped session 38).

**Scope:** Pin concrete file:line anchors on both the MKS/WOLF and LMP sides for the five per-agency routers Phase 4 introduces (`AgencyWolfDepotRouter` / `AgencyWolfRouteRouter` / `AgencyWolfHopperRouter` / `AgencyWolfTerminalRouter` / `AgencyWolfCrewRouter`), the projector splice for the `WOLF_ScenarioModule` scenario, the wire surface for owner-only echoes (5 new MsgData types on slots 9-13), the `IgnoredScenarios` gate-conditional addition, the gate=off behaviour, the cross-agency CrewRoute kerbal authority gate (the distinctive Phase 4 surface — user-confirmed s38), and the pre-0.31-upgrade diagnostics. **No code changes in this commit** — same audit-via-pre-spec discipline that validated Phases 1 / 1.5 R0 / 2 / 3 (see `[[feedback-audit-via-prespec]]`).

**Source pins (re-walked 2026-05-19; do not re-clone if still current):**

- **MKS / WOLF** `ed0f6aa6` at `F:\tmp\mks-external\MKS\Source\WOLF\WOLF\`
- **USITools** `4ad5cdd8` at `F:\tmp\mks-external\USITools\` (transitive ref only — WOLF uses `USITools.ServiceManager` for DI)
- **LMP** `feature/per-agency` tip `0d99a81a`
- **Upstream coordination check:** AdmiralRadish has nothing in flight in `Server/System/Agency/` (fork-only) or in WOLF-adjacent surfaces (WOLF is not vendored on upstream). Safe to land Phase 4 without coordination.

**Phase 4 prerequisites — v4 ships these first:**

- **v4 `VesselProto` cross-agency write guard** ([v4-vessel-proto-cross-agency-write-guard.md](v4-vessel-proto-cross-agency-write-guard.md)) — closes the underlying proto-write hole that the s39 Phase 4 lens reviews uncovered. **Required prerequisite for Phase 4's cross-agency CrewRoute kerbal authority design** (the §8.e re-derivation assumes the proto path is gated; without the v4 fix that assumption fails). This is a 1-line guard addition + tests, scheduled for v4 release ahead of Phase 4. Independent of Phase 4 — closes vessel-state-write exploits broadly, not just for WOLF.

**Re-walk corrections vs 2026-05-18 handoff §R4 audit + 2026-05-19 pickup memory:**

| Claim in audit/pickup | Re-walked truth | Impact |
|---|---|---|
| "`Hopper.cs` line 18 `Guid.NewGuid().ToString()`" | File is `HopperMetadata.cs:18` (not `Hopper.cs`) | Filename typo; ID scheme correct |
| "`Terminal.cs` line 15 `Guid.NewGuid().ToString("N")`" | File is `TerminalMetadata.cs:15` (not `Terminal.cs`) | Filename typo; ID scheme correct |
| Pickup memory "slots 10-14" | High-water at fork tip is `OrbitalState=8`; Phase 4 appends **slots 9-13** | Wire enum allocation |
| Pickup memory: "K1 grief guard, Stage 5.17e-7/8/9" | Exists as `KerbalSystem.CanRemoveKerbalUnderK1` at lines 103-136, attributed to Stage 5.17e-8 only | Reference precision |
| Handoff §10 item 10 + pickup: "kerbals carry agency tag for cross-agency check" | **No kerbal-level agency stamp exists.** K1 guard uses vessel-proxy authority: scans `VesselStoreSystem.CurrentVessels` for `"crew = {name}"` + reads vessel's `OwningAgencyId` | Cross-agency CrewRoute reject uses same vessel-proxy pattern — no net-new kerbal-ownership infrastructure |
| Pickup memory: "ScenarioPersister lists are public" | Lists are `protected` with public accessors (`GetDepots()` / `GetHoppers()` / `GetTerminals()` / `GetRoutes()` + `GetCrewRoutes(double)` / `GetCrewRoute(string)`) | Reflection or accessor calls for read access; OK as patches use `__instance` |
| Pickup memory: "WOLF scenario name = WOLF_ScenarioModule" | Confirmed. Scenario name on wire is the class name `WOLF_ScenarioModule`; child node names inside are `CREWROUTES` / `DEPOTS` / `HOPPERS` / `ROUTES` / `TERMINALS` (`ScenarioPersister.cs:9-13` — note `CREWROUTES` is one word with no underscore) | Projector switch case `"WOLF_ScenarioModule"` strips + splices 5 child nodes |
| Handoff: "verify WOLF_ScenarioModule is in compiled DLL" | **Confirmed** — `WOLF.csproj` line 137 `<Compile Include="Modules\WOLF_ScenarioModule.cs" />`. Phase 4 has a real ScenarioModule to partition (NOT an FFT-S3-style orphan) | Phase 4 has real shared state to partition; not retired |

**Channel allocation verification (re-run at session start):**

- Server high-water mark: **22** (`AgencySrvMsg`, `LmpCommon/Message/Server/AgencySrvMsg.cs:61`).
- Client high-water mark: **21** (`AgencyCliMsg`, `LmpCommon/Message/Client/AgencyCliMsg.cs:59`).
- **Phase 4 allocates no new channels.** All five new MsgData families ride existing `AgencySrvMsg` ch 22 / `AgencyCliMsg` ch 21 per the [[reference-agency-wire-extension]] convention. Same as Phase 3.

**Wire enum allocation verification:**

- `AgencyMessageType` (`LmpCommon/Message/Types/AgencyMessageType.cs:10-41`) currently holds slots 0-8: `Handshake=0`, `CreateRequest=1`, `CreateReply=2`, `State=3`, `Contract=4`, `Visibility=5`, `KolonyState=6`, `PlanetaryState=7`, `OrbitalState=8`.
- Phase 4 appends slots **9 / 10 / 11 / 12 / 13** for `WolfDepotState`, `WolfRouteState`, `WolfHopperState`, `WolfTerminalState`, `WolfCrewRouteState` (naming per the [[feedback-user-facing-naming]] discipline — owner-only state-snapshot messages, mirroring `AgencyStateMsgData` / `AgencyKolonyStateMsgData` shape).
- `SubTypeDictionary` entries APPEND to both `AgencySrvMsg.cs:43-57` AND `AgencyCliMsg.cs:41-55` per the BUG-010 wire-symmetry rule.

**v5 bumps protocol to 0.32.0** (or whichever Major.Minor pairing forces a cohort split per `LmpVersioning.IsCompatibleWithPeer` at [LmpCommon/LmpVersioning.cs:59-76](../../LmpCommon/LmpVersioning.cs#L59-L76)). **Required for safe v4 → v5 migration.** Without the bump, the cross-compat check `peer.Major == local.Major && peer.Minor == local.Minor` passes for `v4 (0.31.0)` against `v5 (0.31.0)` peers — and the BUG-010 wire-symmetry rule then silently drops `WolfDepotState=9` (etc.) at the v4 server because its `AgencyCliMsg.SubTypeDictionary` lacks the new slots. The v5 client would see Lidgren-ack of the message but the server would never persist the WOLF mutation — asymmetric soak desync. Bumping forces mixed-cohort impossibility: v4 and v5 cohorts must run separate servers, which is the established fork-upgrade contract since the `0.30.0` bump (commit `d64acf66`). Per `[[feedback-wire-msgdata-chunking-caps]]`, the 5 new MsgData types MUST also have symmetric send-side caps + catchup chunking — orthogonal to the protocol bump but required for shipping.

---

## 1. The bugs Phase 4 fixes — verified end-to-end traces

### 1.a Cross-agency depot/route/hopper/terminal visibility leak under per-agency mode

**Verified trace under `PerAgencyCareer=true` without Phase 4:**

1. Alice (Agency A) lands a `WOLF_DepotModule` part on Duna's Lowlands biome. The KSP UI fires `ScenarioPersister.CreateDepot("Duna", "Lowlands")` ([ScenarioPersister.cs:76-87](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs#L76-L87)); a new `Depot` lands in `ScenarioPersister.Depots` (line 25).
2. LMP's 30s scenario SHA pass picks up the changed `WOLF_ScenarioModule` blob (the persister's OnSave at `ScenarioPersister.cs:358-430` writes all 5 child node families to the parent scenario node). The client ships it to the server via `ScenarioSystem.ParseReceivedScenarioData` → `ScenarioDataUpdater.RawConfigNodeInsertOrUpdate`. The blob lands in `ScenarioStoreSystem.CurrentScenarios["WOLF_ScenarioModule"]`.
3. On Bob's (Agency B) next handshake or scene-load, the server's `SendScenarioModules` ships every scenario in `CurrentScenarios` — including `WOLF_ScenarioModule` — to Bob. `AgencyScenarioProjector.ProjectForClient` does NOT currently know about `WOLF_ScenarioModule`, so the blob passes through unchanged.
4. Bob's KSP applies the blob via `ProtoScenarioModule`. WOLF's `ScenarioPersister.OnLoad` at `ScenarioPersister.cs:284-356` populates his local `Depots` / `CrewRoutes` / `Hoppers` / `Routes` / `Terminals` lists with Alice's entries.
5. Bob's WOLF UI (`WOLF_PlanningMonitor` / `WOLF_RouteMonitor` / `WOLF_ScenarioMonitor`) renders Alice's depots in his planning view. Bob can now `CreateRoute` to Alice's Duna depot from his own depot — Bob's resources transit through Alice's logistics graph, with no consent or visibility for Alice.

**Net effect under per-agency mode:** Agency separation is silently violated for WOLF logistics. Privacy expectation (spec §10 Q1) is broken. Bob's logistics network can fork-feed off Alice's depots, route capacity, and hopper recipes. Symmetric across all 5 entity types.

**Under shared-agency mode (`PerAgencyCareer=false`):** by definition all players are one agency, so the leak isn't a "leak" — it's the intended sharing model. Phase 4's gate=off behaviour is **no-op** (the postfixes early-return; the 30s SHA pass operates unchanged; both players see the shared WOLF graph). Same dual-mode silence as Phase 3.

### 1.b Cross-agency CrewRoute kerbal seizure under per-agency mode

**Verified trace (the distinctive Phase 4 surface):**

1. Alice has Kerbal "Jeb Kerman" assigned to a vessel `V_A` she owns (`V_A.OwningAgencyId == AgencyA`).
2. Bob (Agency B) opens the WOLF Crew Transfer UI (`WOLF_CrewTransferScenario`) at one of Bob's terminals on the same body. The UI lists all kerbals on nearby vessels via `LogisticsTools.GetNearbyVessels(TERMINAL_RANGE, true, activeVessel, landedOnly)` ([WOLF_CrewTransferScenario.cs:566-567](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Modules/WOLF_CrewTransferScenario.cs#L566-L567)).
3. Bob selects Jeb. Bob clicks Launch. `WOLF_CrewTransferScenario.Launch()` at line 557 fires.
4. Inside Launch (`WOLF_CrewTransferScenario.cs:567-598`):
   - Iterates `vessels = LogisticsTools.GetNearbyVessels(...)` (line 566) — this DOES include `V_A` (Alice's vessel, but in physics range).
   - For each `kerbal` in `vessel.GetVesselCrew()`, if `passengers.Contains(kerbal.name)`, calls `_selectedFlight.Embark(new Passenger(kerbal))` at line 576.
   - On successful Embark, calls `vessel.CrewListSetDirty()` (line 578), iterates parts, calls `part.RemoveCrewmember(kerbal)` (line 584), and sets `kerbal.rosterStatus = ProtoCrewMember.RosterStatus.Missing` + `kerbal.SetTimeForRespawn(double.MaxValue)`.
5. Bob has now removed Jeb from Alice's vessel `V_A`. The CrewRoute persists into Bob's `ScenarioPersister.CrewRoutes`. The 30s SHA pass broadcasts the modified `WOLF_ScenarioModule` AND the modified vessel proto (Jeb gone from V_A) to all clients.
6. Alice's KSP applies the proto update. Jeb is gone from V_A.

**Net effect:** Bob has seized Alice's kerbal via the WOLF UI. Same shape as the BUG-010 family of "remote-mutates-other-vessel" bugs that LMP fork has been closing. The Stage 5.17a write-path counterpart (`VesselMsgReader.RejectIfCrossAgencyWrite`) does NOT close this gap on its own because the seizure happens via the vessel proto path (`vessel.RemoveCrewmember` → `BackupVessel` → proto broadcast), and `HandleVesselProto` ([VesselMsgReader.cs:262-318](../../Server/Message/VesselMsgReader.cs#L262-L318)) was NOT extended with the cross-agency guard when the 11 relayed message types + Remove + Couple were gated in session 19. The exclusion was rationalized by the [[5.18b relay-vs-store note]]'s "relayed proto bytes are advisory" framing — but that framing was about peer-client interpretation of the relay, not about the server's authoritative store, which DOES get overwritten by the proto bytes.

**The v4 "VesselProto cross-agency write guard" fix ([v4-vessel-proto-cross-agency-write-guard.md](v4-vessel-proto-cross-agency-write-guard.md)) is the prerequisite that closes this underlying proto-write hole broadly** (not just for WOLF; for ANY cross-agency vessel-state mutation via crafted proto). It ships in v4 ahead of Phase 4 as a focused 1-line guard addition + tests. WOLF Phase 4 then layers the privacy partition (per-agency depots / routes / etc.) and the legitimate-client UX preflight (preflight on `WOLF_CrewTransferScenario.Launch`) on top.

**Under shared-agency mode:** by definition no agency separation; cross-agency-kerbal-seizure isn't conceptually possible (all kerbals belong to all players). Pre-Phase-4 stock LMP behaviour preserved.

### 1.c FlightNumber collision under high CrewRoute volume

**Verified trace** (low-priority cosmetic issue, deferred to upstream PR per handoff §11):

1. WOLF's `GetNewFlightNumber` at `ScenarioPersister.cs:191-214` generates a 3-character flight number from a 32-char alphabet × 10 numeric range = `10 × 32 × 32 = 10240` distinct possibilities. After 10 retries, it silently returns a colliding value (line 213).
2. Under per-agency mode with multiple active CrewRoutes, the probability of collision rises. But `CrewRoute.UniqueId` (`CrewRoute.cs:90`) is a Guid `ToString("N")` — strong by construction. FlightNumber is **display-only** in the UI (`WOLF_CrewTransferScenario.cs:610`).

**Phase 4 stance:** Out-of-scope. Per handoff §R4 + §11, FlightNumber is a cosmetic concern fixable via upstream PR to USI/MKS, not a fork-side correctness blocker. Tracked separately.

---

## 2. The correct fix — five routers + projector splice + cross-agency kerbal gate

**Per-spec invariant (CLAUDE.md, spec §10): `PerAgencyCareer=true` ⇒ 1 player = 1 agency.** Every Phase 4 decision below leans on this 1:1 mapping. Same invariant Phase 3 inherits from. If a future product decision (post-Stage-5) opens N-players-per-agency, **revisit §2.b.v (cross-agency CrewRoute kerbal authority) — Phase 4's gate=on design is exact for 1:1 and would need re-derivation for N:1.**

### 2.a Anchor map (verified)

| Surface | Path | Line(s) | Symbol | Phase 4 role |
|---------|------|---------|--------|--------------|
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 7 | `class ScenarioPersister : IRegistryCollection` | Hooked surface (NOT modified) |
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 9-13 | `CREW_ROUTES_NODE_NAME / DEPOTS_NODE_NAME / HOPPERS_NODE_NAME / ROUTES_NODE_NAME / TERMINALS_NODE_NAME` | Const-strings used by the projector splice (mirror values: `"CREWROUTES"`, `"DEPOTS"`, `"HOPPERS"`, `"ROUTES"`, `"TERMINALS"`) |
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 23-32 | `protected List<I*> CrewRoutes/Depots/Hoppers/Routes/Terminals` | 5 entity lists; partition target. **Protected access** — patches use `__instance` so visibility isn't an issue. |
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 45-74 | `CreateCrewRoute(originBody, originBiome, destinationBody, destinationBiome, economyBerths, luxuryBerths, duration)` | **Postfix anchor #1** — mutation hook for CrewRoute creation. `__instance.CrewRoutes` newly contains the route. |
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 76-87 | `CreateDepot(body, biome)` | **Postfix anchor #2** — Depot creation. |
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 95-101 | `CreateHopper(IDepot depot, IRecipe recipe)` | **Postfix anchor #3** — Hopper creation. |
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 103-138 | `CreateRoute(originBody, originBiome, destinationBody, destinationBiome, payload)` | **Postfix anchor #4** — Route creation (note: existing-route returns + IncreasePayload at lines 116-125; postfix must inspect to distinguish create vs. payload-bump). |
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 145-151 | `CreateTerminal(IDepot depot)` | **Postfix anchor #5** — Terminal creation. |
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 432-440 | `RemoveHopper(string id)` | **Postfix anchor #6** — Hopper removal (drives removed-key wire echo). |
| WOLF | [ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) | 442-449 | `RemoveTerminal(string id)` | **Postfix anchor #7** — Terminal removal. |
| WOLF | [CrewRoute.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/CrewRoute.cs) | 141-181 | `Embark(IPassenger passenger)` | **Postfix anchor #8** — Passenger boarded (per-agency wire emit of updated route + Passengers list). |
| WOLF | [CrewRoute.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/CrewRoute.cs) | 123-139 | `Disembark(IPassenger passenger)` | **Postfix anchor #9** — Passenger disembarked. |
| WOLF | [CrewRoute.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/CrewRoute.cs) | 183-196 | `Launch(double now)` | **Postfix anchor #10** — Flight transitions Boarding→Enroute. |
| WOLF | [CrewRoute.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/CrewRoute.cs) | 105-121 | `CheckArrived(double time)` | **Postfix anchor #11** — Flight transitions Enroute→Arrived. |
| WOLF | [WOLF_CrewTransferScenario.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Modules/WOLF_CrewTransferScenario.cs) | 557-617 | `Launch()` (UI handler) | **Prefix anchor (cross-agency kerbal gate)** — pre-validates the kerbal selection BEFORE the Embark + RemoveCrewmember chain runs. The actual server-side enforcement happens via the `AgencyWolfCrewRouter` cross-agency check on the resulting wire message; the prefix is UX-only (instant feedback) per the layered-defense convention. |
| WOLF | [Depot.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Depot.cs) | 38-43 | `Establish()` | **Postfix anchor #12** — Depot transitions to `IsEstablished=true`. |
| WOLF | [Depot.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Depot.cs) | 266-271 | `Survey()` | **Postfix anchor #13** — Depot transitions to `IsSurveyed=true`. |
| WOLF | [Depot.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Depot.cs) | 131-181, 183-217 | `NegotiateProvider/NegotiateConsumer` | **Postfix anchor #14** — Resource-stream mutation (Incoming/Outgoing on a depot's `IResourceStream`). Drives per-depot state wire emit. |
| WOLF | [Route.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Route.cs) | 84-116 | `AddResource(resourceName, quantity)` | **Postfix anchor #15** — Route resource allocation. |
| WOLF | [Route.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Route.cs) | 130-133 | `IncreasePayload(amount)` | **Postfix anchor #16** — Route payload increase (from the existing-route branch at `ScenarioPersister.cs:122`). |
| WOLF | [Route.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Route.cs) | 135-162 | `RemoveResource(resourceName, quantity)` | **Postfix anchor #17** — Route resource removal. |
| WOLF | [Modules/WOLF_ScenarioModule.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Modules/WOLF_ScenarioModule.cs) | 7 | `class WOLF_ScenarioModule : ScenarioModule` | Scenario-module name for projection + `IgnoredScenarios.IgnoreSend` addition. `[KSPScenario(AddToAllGames, EDITOR/FLIGHT/SPACECENTER/TRACKSTATION)]` — auto-created on every game type (Career / Science / Sandbox). |
| WOLF | [Modules/WOLF_ScenarioModule.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Modules/WOLF_ScenarioModule.cs) | 60-66 | `OnAwake() { services.AddSingletonService<IRegistryCollection, ScenarioPersister>(); }` | Persister is a DI singleton resolved via `ServiceManager.GetService<IRegistryCollection>()` (line 73, 81). The Harmony postfix on `ScenarioPersister.Create*` runs against `__instance` which IS the singleton. |
| LMP / Server | [Server/System/ScenarioSystem.cs](Server/System/ScenarioSystem.cs) | 70-111 | `SendScenarioModules(ClientStructure client)` | Existing projection hook. Phase 4 adds switch cases inside `AgencyScenarioProjector`. |
| LMP / Server | [Server/System/Agency/AgencyScenarioProjector.cs](Server/System/Agency/AgencyScenarioProjector.cs) | `CareerScenarios` HashSet | TBD line | **Add 1 entry:** `"WOLF_ScenarioModule"`. Fast-path skip needs it. |
| LMP / Server | [Server/System/Agency/AgencyScenarioProjector.cs](Server/System/Agency/AgencyScenarioProjector.cs) | `Project` switch | TBD line | **Add 1 case:** `case "WOLF_ScenarioModule": return SpliceAgencyWolfState(serializedText, targetAgency);`. The splice method strips ALL 5 child-node families and re-emits per-agency entries from `targetAgency.WolfDepots / .WolfRoutes / .WolfHoppers / .WolfTerminals / .WolfCrewRoutes`. |
| LMP / Server | [Server/System/Agency/AgencyState.cs](Server/System/Agency/AgencyState.cs) | extends 200-300 | dict fields | **Add 5 fields:** `Dictionary<string,AgencyWolfDepotEntry> WolfDepots` keyed by `$"{body}|{biome}"`, `Dictionary<string,AgencyWolfRouteEntry> WolfRoutes` keyed by `$"{originBody}|{originBiome}|{destBody}|{destBiome}"`, `Dictionary<string,AgencyWolfHopperEntry> WolfHoppers` keyed by `HopperMetadata.Id` (full Guid `ToString()` form with hyphens — match WOLF source convention), `Dictionary<string,AgencyWolfTerminalEntry> WolfTerminals` keyed by `TerminalMetadata.Id` (Guid `ToString("N")` form), `Dictionary<string,AgencyWolfCrewRouteEntry> WolfCrewRoutes` keyed by `CrewRoute.UniqueId` (Guid `ToString("N")`). ConfigNode round-trip on each. |
| LMP / Server | [Server/System/KerbalSystem.cs](Server/System/KerbalSystem.cs) | 103-136 | `CanRemoveKerbalUnderK1(client, kerbalName)` | **Direct template** for the WOLF cross-agency CrewRoute kerbal gate. Phase 4 extracts a shared helper `KerbalAgencyResolver.GetOwningAgencyForKerbal(kerbalName)` returning `Guid?` (null = unassigned), and uses it in both the K1 path and the new `AgencyWolfCrewRouter` path. The K1 helper's current scan cost (O(N vessels × vessel-size)) is unchanged; CrewRoute creation is rare (handful per session), same cadence profile as KerbalRemove. |
| LMP / Client | [LmpClient/Systems/Scenario/ScenarioSystem.cs](../../LmpClient/Systems/Scenario/ScenarioSystem.cs) | ~174-193 | `PerAgencyOnlyIgnoreSend` HashSet | Phase 3 Slice B's gate-conditional filter (locked Option B per Phase 3 §3.g). Phase 4 appends `"WOLF_ScenarioModule"` to the same HashSet. Gate-conditional broadcast suppression — under gate=on the postfix routes per-agency AND blocks the 30s SHA pass from re-sending the shared WOLF blob; under gate=off neither path mutates. **The static `LmpCommon/IgnoredScenarios.cs:14-27` list is NOT extended** — that's the permanent-design list (Funding/etc.); Phase 4's gate-conditional addition belongs with the runtime flag check. |
| LMP / Common | [LmpCommon/Message/Types/AgencyMessageType.cs](LmpCommon/Message/Types/AgencyMessageType.cs) | 10-41 | `enum AgencyMessageType` | **Append 5 entries:** `WolfDepotState=9`, `WolfRouteState=10`, `WolfHopperState=11`, `WolfTerminalState=12`, `WolfCrewRouteState=13`. Wire-protocol-relevant — never reorder. |
| LMP / Common | [LmpCommon/Message/Server/AgencySrvMsg.cs](LmpCommon/Message/Server/AgencySrvMsg.cs) | 43-57 | `SubTypeDictionary` | Append 5 entries mapping the new slots → 5 new MsgData classes. |
| LMP / Common | [LmpCommon/Message/Client/AgencyCliMsg.cs](LmpCommon/Message/Client/AgencyCliMsg.cs) | 41-55 | `SubTypeDictionary` | Mirror — same 5 appends. |
| LMP / Server | [Server/Message/AgencyMsgReader.cs](Server/Message/AgencyMsgReader.cs) | 69-122 | `HandleMessage` switch | Append 5 case branches dispatching to the new routers' `TryRoute`. |

### 2.b Routers — five new files under `Server/System/Agency/`

All five follow the `AgencyKolonyRouter` (Phase 3 Slice B) structural template: `public static class` with a `TryRoute(ClientStructure client, ...)` entry point that returns `true` when the per-agency path handled the inbound and `false` when the gate is off / client lacks agency / agency missing. Internal helpers do per-entry exception isolation (single try/catch per entry covering classify + upsert).

Per pre-spec §2.e (single-class wire-type contract), each MsgData is reused both directions: C→S mutation + S→C owner echo + S→C reconnect catch-up. Server IGNORES wire-supplied `AgencyId` on inbound and derives it from `client.PlayerName` via `AgencySystem.AgencyByPlayerName`.

**Mandatory router-body shape** (omitted from the per-router pseudocode below to avoid duplication; all five MUST include): the per-entry for-loop is wrapped in `lock (AgencySystem.GetAgencyLock(agencyId)) { ... }`. Reads also acquire the lock for snapshot atomicity per §3.b. Matches `AgencyKolonyRouter.cs:100` ([Server/System/Agency/AgencyKolonyRouter.cs](../../Server/System/Agency/AgencyKolonyRouter.cs#L100)) — the explicit Phase 3 template. Without the wrap, a concurrent `SaveAgency` from another router or admin command could observe a torn intermediate snapshot.

#### 2.b.i `AgencyWolfDepotRouter`

**File:** `Server/System/Agency/AgencyWolfDepotRouter.cs`.

**Entry point:** `public static bool TryRoute(ClientStructure client, AgencyWolfDepotStateMsgData msg)`.

**Body shape:**

1. `if (!AgencySystem.PerAgencyEnabled) return false;` — gate + Career-only.
2. `if (client == null || msg == null || string.IsNullOrEmpty(client.PlayerName)) return false;`.
3. `if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId)) return false;`.
4. `if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency)) return false;`.
5. Per-entry classify-and-upsert under a SINGLE try/catch (`[[feedback-negative-assertions-lock-in-bugs]]` shape — covers the entire pipeline including any unexpected throws in `Upsert`):
   ```text
   foreach (var entry in msg.Entries) {
     try {
       if (string.IsNullOrEmpty(entry.Body) || string.IsNullOrEmpty(entry.Biome)) {
         LunaLog.Debug($"[fix:WOLF-R4] depot entry skipped: empty Body/Biome (agency {agencyId:N})");
         continue;
       }
       Upsert(agency, entry);   // dict key $"{Body}|{Biome}"
       accepted.Add(entry);
     } catch (Exception ex) {
       LunaLog.Error($"[fix:WOLF-R4] depot entry skipped for {entry.Body}/{entry.Biome}: {ex.GetType().Name}: {ex.Message}");
     }
   }
   ```
6. **No cross-agency vessel-proxy check needed for Depot** — depots are body+biome-keyed and don't carry a vessel id. The mere act of registering a depot at `(Body, Biome)` doesn't conflict with another agency's depot at the same `(Body, Biome)` because they're stored in DIFFERENT per-agency `WolfDepots` dictionaries. Both agencies legitimately own a depot at `(Mun, Highlands)` if both have built one. (This is a meaningful semantic shift from the shared-mode product where `(Mun, Highlands)` was a global slot.)
7. After batch: `AgencySystem.SaveAgency(agencyId)` (single fsync per batch).
8. `AgencySystemSender.SendWolfDepotStateToOwner(client, agencyId, accepted)` — owner-only echo.

**Catch-up on reconnect:** `AgencySystemSender.SendWolfDepotCatchupTo(client, agencyId)` wired into `HandshakeSystem` immediately after the existing Phase 3 catch-up calls. Returning owner receives their full `WolfDepots` dict before mid-session mutations arrive.

**Gate=off behaviour:** postfix is no-op; the gate-conditional `IgnoredScenarios` filter doesn't suppress `WOLF_ScenarioModule`; legacy 30s SHA pass operates unchanged. Bit-identical to pre-Phase-4 baseline.

**Pure-helper extraction (per LMP testability convention):**

```text
public static (List<AgencyWolfDepotEntry> accept, List<string> rejectReasons)
ClassifyDepotEntries(IEnumerable<AgencyWolfDepotEntry> incoming)
```

Returns the partition. Pure, no side effects. Pinned in `ServerTest/AgencyWolfDepotRouterTest.cs` with ~8 cases.

#### 2.b.ii `AgencyWolfRouteRouter`

**File:** `Server/System/Agency/AgencyWolfRouteRouter.cs`.

Same shape as Depot. Partition key is `$"{originBody}|{originBiome}|{destBody}|{destBiome}"`. Validation: all 4 strings non-empty, plus `Payload >= 1` (matches WOLF's `RouteInsufficientPayloadException` at `Route.cs:67-69`). The wire entry carries the resource allocation table (`_resources` dict) as a serialized side-payload; defensive copy on store (mirrors `AgencyContractRouter.cs` precedent — pre-spec §3.c).

**No cross-agency check** — routes are body-tuple-keyed, not vessel-keyed.

**Edge case (re-walk finding):** `ScenarioPersister.CreateRoute` (`ScenarioPersister.cs:115-125`) returns the existing route + `IncreasePayload` if a matching `(OriginBody, OriginBiome, DestBody, DestBiome)` quadruple already exists. The client-side postfix must distinguish create vs. payload-bump and emit accordingly — the upsert on the server is the same either way (dict key matches → replace) but the wire echo carries the new `Payload` value either way. Documented in §2.c implementation note.

#### 2.b.iii `AgencyWolfHopperRouter`

**File:** `Server/System/Agency/AgencyWolfHopperRouter.cs`.

Same shape as Depot. Partition key is `HopperMetadata.Id` (Guid `ToString()` — with hyphens, NOT `"N"` form). The wire entry carries the hopper's `IRecipe` (the ingredient list) — flattened via `HopperMetadata.OnSave`-style "resource,qty,resource,qty,..." string format (`HopperMetadata.cs:44-48`). Validation: non-empty `Id`, parseable recipe string.

**No cross-agency check** — hoppers are Guid-keyed and don't carry vessel context. The hopper belongs to a depot, and the depot's body+biome ownership is by-construction per-agency.

**Removed-keys path (Hopper-specific):** WOLF has `RemoveHopper(id)` (`ScenarioPersister.cs:432-440`). The wire MsgData has a `RemovedKeys[]` parallel field carrying ids to delete from the agency's `WolfHoppers` dict. Same shape as Phase 3 Slice E-1's `KolonyMigrationResult.RemovedKeys` pattern.

#### 2.b.iv `AgencyWolfTerminalRouter`

**File:** `Server/System/Agency/AgencyWolfTerminalRouter.cs`.

Mirror of Hopper. Partition key is `TerminalMetadata.Id` (Guid `ToString("N")` — note different format than Hopper). Wire entry carries `Body`, `Biome`, `Id`. RemoveTerminal at `ScenarioPersister.cs:442-449` drives removed-keys echoes.

**No cross-agency check** — terminals are Guid-keyed.

#### 2.b.v `AgencyWolfCrewRouter` (the distinctive Phase 4 router)

**File:** `Server/System/Agency/AgencyWolfCrewRouter.cs`.

**Entry point:** `public static bool TryRoute(ClientStructure client, AgencyWolfCrewRouteStateMsgData msg)`.

**Two responsibilities** (the only Phase 4 router with this split):

1. **Per-agency CrewRoute partition** (analogous to Depot/Route/Hopper/Terminal). Partition key is `CrewRoute.UniqueId` (Guid `ToString("N")`). Wire entry carries the full CrewRoute shape from `CrewRoute.cs:51-65` (OriginBody/OriginBiome/DestBody/DestBiome + EconomyBerths/LuxuryBerths + Duration + ArrivalTime + FlightStatus + UniqueId + Passengers list).
2. **Cross-agency kerbal authority gate** (per user-confirmed scope, s38). REJECT the entire CrewRoute mutation if any Passenger's kerbal is currently aboard a vessel owned by a different non-Empty agency.

**Body shape:**

1-4. Same gate/agency/client lookup as §2.b.i steps 1-4.
5. **Cross-agency kerbal pre-check** (the new logic — silent server-side drop per §8.e locked decision; no snap-back wire echo because client-side prefix is the UX path + modified-client desync is structurally acceptable per §8.f):
   ```text
   foreach (var entry in msg.Entries) {
     try {
       if (string.IsNullOrEmpty(entry.UniqueId)) continue;
       
       // Validate Passenger list against the requester's agency via vessel-proxy authority.
       // Uses the K1 guard template at KerbalSystem.cs:103-136. Cross-agency reject is a
       // silent drop + Warning log per §8.e locked decision — modified clients that bypass
       // the client-side prefix get no snap-back echo (desync is their problem per §8.f).
       bool crossAgencyDetected = false;
       foreach (var passenger in entry.Passengers ?? new List<AgencyWolfPassengerEntry>()) {
         if (string.IsNullOrEmpty(passenger.Name)) continue;
         
         var owning = KerbalAgencyResolver.GetOwningAgencyForKerbal(passenger.Name);
         if (owning == null) continue;                          // unassigned kerbal — allow (spec §10 Q3)
         if (owning == Guid.Empty) continue;                     // Unassigned-sentinel vessel — allow
         if (owning == agencyId) continue;                       // same agency — allow
         
         // Cross-agency kerbal — silently drop the entire CrewRoute mutation.
         LunaLog.Warning(
           $"[fix:WOLF-R4] CrewRoute silently dropped: kerbal '{passenger.Name}' belongs to a different agency " +
           $"(requester {client.PlayerName}, agency {agencyId:N})");
         crossAgencyDetected = true;
         break;
       }
       
       if (crossAgencyDetected) continue;     // skip this entry; no wire echo
       
       Upsert(agency, entry);
       accepted.Add(entry);
     } catch (Exception ex) {
       LunaLog.Error($"[fix:WOLF-R4] crew route entry skipped for '{entry.UniqueId}': {ex.GetType().Name}: {ex.Message}");
     }
   }
   ```
6. After batch: `AgencySystem.SaveAgency(agencyId)` + owner-only echo on the accepted entries via `AgencySystemSender.SendWolfCrewRouteStateToOwner` (success-only echo; rejected entries are silently absent from the echo).

**Vessel-proxy authority via `KerbalAgencyResolver` (new helper):**

```text
// File: Server/System/Agency/KerbalAgencyResolver.cs
public static class KerbalAgencyResolver {
  /// <summary>
  /// Returns the OwningAgencyId of the vessel the named kerbal is currently
  /// aboard. Returns null if the kerbal is not aboard any vessel (unassigned —
  /// AC pool, KIA-cleared, EVA-rescue completion). Returns Guid.Empty if the
  /// vessel is unassigned-sentinel (pre-0.31 vessels — spec §10 Q3).
  /// 
  /// Hot path is shared with KerbalSystem.CanRemoveKerbalUnderK1 (Stage
  /// 5.17e-8). Phase 4 extracts the scan into this helper so both call sites
  /// share one cache + invalidation surface. Cache invalidation is "rebuild
  /// on every call" today (matches K1's current cost profile); a future
  /// indexed cache keyed on vessel.persistentId → crew[] would optimize if
  /// profiling shows the scan dominating.
  /// </summary>
  public static Guid? GetOwningAgencyForKerbal(string kerbalName) {
    if (string.IsNullOrEmpty(kerbalName)) return null;
    // Upgrade-lens MUST FIX #3: word-boundary match, not substring.
    // "crew = Bob" must NOT collide with "crew = Bobak". Use a regex pinned to
    // line-anchored `crew = NAME\r?\n` or split on lines and check exact match
    // on the value side. Avoid Regex.IsMatch construction cost per call —
    // pre-compile static Regex with Singleline option or use IndexOf + char
    // boundary check.
    var prefix = "crew = " + kerbalName;
    foreach (var kvp in VesselStoreSystem.CurrentVessels) {
      string text;
      try { text = VesselStoreSystem.GetVesselInConfigNodeFormat(kvp.Key); }
      catch (Exception) { continue; }
      if (string.IsNullOrEmpty(text)) continue;
      // Look for prefix followed by line-terminator (or end-of-string). Reject
      // "crew = Jeb Kerman" when scanning for "crew = Jeb" because the next
      // char would be ' ' (space), not '\r' or '\n' or end. Kerbal names can
      // contain spaces (e.g. "Jeb Kerman") so we must NOT use word-boundary
      // \b regex — that would split on the inner space and produce false
      // negatives. Line-boundary is the right match.
      var idx = text.IndexOf(prefix, StringComparison.Ordinal);
      while (idx >= 0) {
        var endIdx = idx + prefix.Length;
        if (endIdx == text.Length || text[endIdx] == '\r' || text[endIdx] == '\n') {
          return kvp.Value.OwningAgencyId;
        }
        idx = text.IndexOf(prefix, endIdx, StringComparison.Ordinal);
      }
    }
    return null;
  }
}
```

**Boot-ordering invariant (upgrade-lens MUST FIX #3 follow-up).** `LoadExistingVessels` in `MainServer.Main` runs BEFORE the Lidgren listener starts accepting connections — verified by the analogous note for `RejectIfCrossAgencyWrite` at [VesselMsgReader.cs:181-185](../../Server/Message/VesselMsgReader.cs#L181-L185). So `VesselStoreSystem.CurrentVessels` is fully populated before any handshake fires `OnPlayerAuthenticated` (which triggers the first agency lookup). **No boot-window where the resolver would see an empty store and falsely report "kerbal unassigned" for legitimate vessels.** The same invariant Phase 3 + 5.17a leans on.

**Future cache (deferred):** if profiling shows `GetOwningAgencyForKerbal` scan cost dominating, indexed cache keyed on `(kerbalName → vesselId)` rebuilt on vessel-proto mutation. Stage 6 / future optimization. The K1 guard has lived with the per-call scan since 5.17e-8 without operator complaint at v2/v3 cohort sizes.

`KerbalSystem.CanRemoveKerbalUnderK1` is **refactored to call this helper** (commit-internal — preserves K1 behavior exactly):

```text
private static bool CanRemoveKerbalUnderK1(ClientStructure client, string kerbalName) {
  if (string.IsNullOrEmpty(kerbalName)) return true;
  if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var requesterAgency)) return true;
  
  var owning = KerbalAgencyResolver.GetOwningAgencyForKerbal(kerbalName);
  if (owning == null) return true;                  // unassigned → allow
  if (owning == Guid.Empty) return true;            // Unassigned-sentinel vessel → allow (spec §10 Q3)
  if (owning == requesterAgency) return true;       // requester owns the vessel → allow
  return false;                                     // different agency → refuse
}
```

**Tests pin both call sites + the shared helper** — `ServerTest/KerbalAgencyResolverTest.cs` (~8 cases: empty/null name; kerbal-not-found; kerbal-on-unassigned-vessel; kerbal-on-own-agency-vessel; kerbal-on-cross-agency-vessel; vessel-serialize-failure-isolation; multiple-vessels-same-needle (defensive); kerbal-not-aboard-any-vessel).

**No snap-back echo on cross-agency kerbal reject** (locked decision per §8.e — Option C below).

The reject path's design was debated during pre-spec authoring; the §8.e re-derivation lands on silent server-side drop. Three options were considered:

- **Option A: dedicated rejection MsgData** — new wire enum slot for `WolfCrewRouteRejection`. Increases wire surface; one more slot to maintain.
- **Option B: reuse `AgencyWolfCrewRouteStateMsgData` with a `RejectionReason` field** — server sends back the entry with the field set; client treats `RejectionReason != null` as "remove this UniqueId from local CrewRoutes". Adds wire field + client-side `_ignoreNextRouteRemoval` rollback-loop suppression bracket.
- **Option C (LOCKED, §8.e):** silent server-side drop + Warning log. No wire echo. The client-side prefix on `WOLF_CrewTransferScenario.Launch` is the legitimate-client UX path (rejects BEFORE any local state mutation). A modified client that bypasses the prefix has explicitly opted out of LMP's authority model — its local desync is its own concern per §8.f's modified-client-desync acceptance. The v4 proto-write guard ([v4-vessel-proto-cross-agency-write-guard.md](v4-vessel-proto-cross-agency-write-guard.md)) prevents the modified client from actually mutating Alice's vessel anyway; the kerbal-removal phantom on Bob's local view is recoverable via Bob's next `VesselSync` reply (which carries Alice's authoritative vessel state, kerbal intact).

**Rationale for Option C:** simplest wire surface (no `RejectionReason` field on `AgencyWolfCrewRouteStateMsgData`), no client-side rollback-loop suppression complexity, and structurally consistent with the §8.f modified-client-desync framing. The §8.e re-derivation arrived at this after walking through Option B's `_ignoreNextRouteRemoval` bracketing concern and finding it added complexity for a case (modified-client rollback) we already accept as "the cheater's problem".

**Catch-up on reconnect:** `AgencySystemSender.SendWolfCrewRouteCatchupTo(client, agencyId)` wired into `HandshakeSystem` after the other Phase 4 catchup calls. Sends the full `WolfCrewRoutes` dict — including the in-flight Passengers list, so a returning player sees their fleets mid-flight.

**Gate=off behaviour:** postfix is no-op; legacy 30s SHA pass unchanged; cross-agency kerbal seizure remains possible under shared-mode (pre-Phase-4 baseline). Under shared-mode the seizure isn't a "leak" — by definition all players share kerbals. Pre-Phase-4 stock LMP behaviour preserved.

### 2.c Projector splice — `AgencyScenarioProjector` extension

**File:** `Server/System/Agency/AgencyScenarioProjector.cs` (existing).

**Changes:**

1. Append 1 entry to `CareerScenarios` HashSet: `"WOLF_ScenarioModule"`.
2. Append 1 case to the `Project` switch:
   ```text
   case "WOLF_ScenarioModule":
     return SpliceAgencyWolfState(serializedText, targetAgency);
   ```
3. One new private method `SpliceAgencyWolfState(string scenarioText, AgencyState agency)` modelled on Phase 3's `SpliceAgencyKolonyEntries` (commit `d4ff0511`). Strip-then-splice via `ConfigNode` round-trip:
   - Parse the input text as a `ConfigNode`.
   - **Strip ALL 5 pre-existing child node families** (`CREWROUTES`, `DEPOTS`, `HOPPERS`, `ROUTES`, `TERMINALS`).
   - Iterate `agency.WolfDepots.Values` / `.WolfRoutes` / `.WolfHoppers` / `.WolfTerminals` / `.WolfCrewRoutes` under `AgencySystem.GetAgencyLock(agencyId)` (snapshot pattern from Phase 3).
   - **Emit in WOLF's required order: DEPOTS FIRST**, then CREWROUTES / HOPPERS / ROUTES / TERMINALS. The ordering is mandatory because WOLF's `ScenarioPersister.OnLoad` at `ScenarioPersister.cs:288-302` has a comment `// Depots need to be loaded first!` and the other types call `Depots.FirstOrDefault(...)` during OnLoad to resolve their `OriginDepot` / `DestinationDepot` references. If the projector emits in a different order (e.g. CREWROUTES before DEPOTS), WOLF's OnLoad throws `DepotDoesNotExistException` on the unresolvable depot lookup at line 184 — the entire scenario load fails, scene transition hangs, soak-blocker.
   - Per-entry exception isolation (drop entry on parse failure, keep siblings).
   - Wrap whole-scenario parse failure in try/catch + return input unchanged + log. Same fallback as Phase 3.

**Critical: parse failure must NOT block handshake.** Same constraint as Phase 3 — a malformed entry would otherwise hang scene-load.

**Empty-agency case:** If `agency.WolfDepots / .WolfRoutes / etc.` are ALL empty (newly-registered agency, never touched WOLF), the splice produces 5 empty `CREWROUTES { } DEPOTS { } HOPPERS { } ROUTES { } TERMINALS { }` child nodes. WOLF's `ScenarioPersister.OnLoad` handles missing-or-empty cleanly (the `HasNode` guards at lines 289, 303, 314, 332, 343). Verified by re-walking OnLoad.

**Critical — depot foreign-key integrity sweep (general-lens MUST FIX #2):** `CrewRoute.OnLoad` at [CrewRoute.cs:249-250](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/CrewRoute.cs#L249-L250) calls `_registry.GetDepot(OriginBody, OriginBiome)` + `GetDepot(DestinationBody, DestinationBiome)`, both of which **throw `DepotDoesNotExistException`** at `ScenarioPersister.cs:185` when the referenced depot isn't in the persister's `Depots` list. The same hazard exists for `Route.OnLoad` at `Route.cs:172-173`. If the projector emits a CrewRoute or Route whose Origin/Destination `(Body, Biome)` is NOT in the agency's `WolfDepots` dict (e.g. stale entry from a prior failed migration, operator hand-edit, or deleteagency-partial-state), WOLF's OnLoad throws and the entire scenario load fails → scene transition hangs → **soak blocker.** Hopper has a defensive `if (depot != null)` guard at `ScenarioPersister.cs:324` and degrades to silently dropping orphaned hoppers (less destructive but still operator-meaningful).

**Mitigation in the splice path:** before emitting `CREWROUTES` and `ROUTES` children, drop entries whose Origin or Destination `(Body, Biome)` is not present in the agency's `WolfDepots` snapshot. Emit a `Warning` log per dropped entry so operators get a grep-target for "my CrewRoute vanished" soak reports. Hoppers can use the same sweep (defensive — WOLF tolerates the orphan but our projector should not propagate it):

```text
// Inside SpliceAgencyWolfState, AFTER DEPOTS emission, BEFORE CREWROUTES/ROUTES emission:
var depotKeys = new HashSet<string>(StringComparer.Ordinal);
foreach (var depot in agency.WolfDepots.Values)
  depotKeys.Add($"{depot.Body}|{depot.Biome}");

foreach (var route in agency.WolfCrewRoutes.Values) {
  var originKey = $"{route.OriginBody}|{route.OriginBiome}";
  var destKey = $"{route.DestinationBody}|{route.DestinationBiome}";
  if (!depotKeys.Contains(originKey) || !depotKeys.Contains(destKey)) {
    LunaLog.Warning(
      $"[fix:WOLF-R4] CrewRoute {route.UniqueId} dropped from agency {agency.AgencyId:N} projection: " +
      $"references depot ({originKey}) or ({destKey}) not in agency.WolfDepots");
    continue;
  }
  EmitCrewRouteChildNode(crewRoutesNode, route);
}
// Mirror loop for agency.WolfRoutes.Values, agency.WolfHoppers.Values (hopper depot-by-Id).
```

**Where does the orphan state come from?** Three scenarios: (a) operator-hand-edit of `Universe/Agencies/*.txt`, (b) prior failed Slice E-class migration that mutated WolfDepots but not WolfCrewRoutes consistently, (c) a future bulk-migration tool that operates on partial sets. The sweep makes the projector defensive against all three without trying to repair the underlying state — repair is operator-side.

### 2.d Client-side Harmony postfixes — 7+ patches

**Files** in new `LmpClient/Harmony/Wolf*` directory (mirror of `LmpClient/Harmony/ModuleLogisticsConsumer_*` / `KolonizationManager_*` / `OrbitalLogisticsTransferRequest_*` from Phase 3):

1. `LmpClient/Harmony/Wolf/ScenarioPersister_CreateDepotPostfix.cs` — Harmony postfix on `ScenarioPersister.CreateDepot`. Reads `__instance.Depots[^1]` (the newly-added depot) + emits `AgencyWolfDepotStateMsgData` via `AgencyMessageSender`-shape thread offload.
2. `LmpClient/Harmony/Wolf/ScenarioPersister_CreateRoutePostfix.cs` — postfix on `ScenarioPersister.CreateRoute`. **Distinguishes create vs. payload-bump** by inspecting `__instance.Routes` count delta (or via prefix-captured pre-count). Emits Route state either way.
3. `LmpClient/Harmony/Wolf/ScenarioPersister_CreateHopperPostfix.cs` — postfix on `CreateHopper`. Reads `__instance.Hoppers[^1]`.
4. `LmpClient/Harmony/Wolf/ScenarioPersister_CreateTerminalPostfix.cs` — postfix on `CreateTerminal`. Reads `__instance.Terminals[^1]`.
5. `LmpClient/Harmony/Wolf/ScenarioPersister_CreateCrewRoutePostfix.cs` — postfix on `CreateCrewRoute`. Reads `__instance.CrewRoutes[^1]`.
6. `LmpClient/Harmony/Wolf/ScenarioPersister_RemoveHopperPostfix.cs` — postfix on `RemoveHopper`. Emits `AgencyWolfHopperStateMsgData` with `RemovedKeys = [id]`.
7. `LmpClient/Harmony/Wolf/ScenarioPersister_RemoveTerminalPostfix.cs` — postfix on `RemoveTerminal`. Emits `AgencyWolfTerminalStateMsgData` with `RemovedKeys = [id]`.
8. `LmpClient/Harmony/Wolf/CrewRoute_EmbarkPostfix.cs` — postfix on `CrewRoute.Embark`. Emits updated CrewRoute state with new Passenger.
9. `LmpClient/Harmony/Wolf/CrewRoute_DisembarkPostfix.cs` — postfix on `CrewRoute.Disembark`. Emits updated state (Passenger removed).
10. `LmpClient/Harmony/Wolf/CrewRoute_LaunchPostfix.cs` — postfix on `CrewRoute.Launch`. Emits with FlightStatus=Enroute + ArrivalTime.
11. `LmpClient/Harmony/Wolf/CrewRoute_CheckArrivedPostfix.cs` — postfix on `CrewRoute.CheckArrived`. Emits state when transition fires (`__result == true` AND `FlightStatus == Arrived`).
12. `LmpClient/Harmony/Wolf/Depot_EstablishPostfix.cs` — postfix on `Depot.Establish`. Emits updated depot state.
13. `LmpClient/Harmony/Wolf/Depot_SurveyPostfix.cs` — postfix on `Depot.Survey`. Emits updated depot state.
14. `LmpClient/Harmony/Wolf/Depot_NegotiatePostfix.cs` — postfix on `Depot.NegotiateProvider` + `NegotiateConsumer`. Emits depot state with new resource stream values. **Likely high-frequency** — needs coalescing (see §3.e).
15. `LmpClient/Harmony/Wolf/Route_AddResourcePostfix.cs` / `Route_RemoveResourcePostfix.cs` / `Route_IncreasePayloadPostfix.cs` — postfixes on Route mutations.
16. `LmpClient/Harmony/Wolf/WOLF_CrewTransferScenario_LaunchPrefix.cs` — prefix on `WOLF_CrewTransferScenario.Launch`. UX-only kerbal-authority pre-check: iterates `_passengers.Values` + scans the selected kerbals against `AgencySystem.LocalAgencyId` via the client-side mirror (Stage 5.18a's `AgencyMembership.VesselOwnership`). If any kerbal is on a vessel owned by a different agency, popup a warning and return `false` to abort. This is INSTANT feedback; the server-side `AgencyWolfCrewRouter` is the authoritative reject. **Defense in depth.**

**Type resolution + self-disable on missing types — CLIENT-SIDE ONLY** (integration-logic MUST FIX #2): use `AccessTools.TypeByName("WOLF.ScenarioPersister")` etc. at module-load on the LMP client; emit `[fix:WOLF-R4] WOLF.* not found; client-side WOLF routing disabled` and abort patch registration if any resolution fails. Self-disable matches MKS-R0/R1/R2 client-side pattern.

**Server-side router does NOT depend on WOLF mod presence.** The Phase 4 routers (`AgencyWolfDepotRouter` / etc.) operate purely on LMP-side types (`AgencyState.WolfDepots` dicts, wire MsgData). The server never imports `WOLF.*` namespaces, never resolves `AccessTools.TypeByName("WOLF.*")`. A server WITHOUT WOLF installed still:
- Loads `Universe/Agencies/*.txt` files with their WOLF dicts (forward-compat, may be empty).
- Accepts inbound `AgencyWolfDepotStateMsgData` etc. and routes via `AgencyMsgReader` switch + `AgencyWolfDepotRouter.TryRoute`.
- Persists per-agency WOLF state via `SaveAgency`.
- Projects WOLF state to clients via `AgencyScenarioProjector`'s `WOLF_ScenarioModule` splice.

The asymmetric case (server without WOLF, client with WOLF) is operator-meaningful only in mixed installs (some peers run with WOLF, others without). The server's WOLF state would still be coherent across the agency — the WOLF-bearing client emits mutations, the server persists + projects, OTHER WOLF-bearing clients receive the projection. Clients WITHOUT WOLF skip the entire WOLF surface (no postfixes register; no UI; the projected `WOLF_ScenarioModule` blob lands in their KSP but with no `WOLF_ScenarioModule` mod-side scenario module to receive it — KSP silently ignores unknown scenario modules per its standard ProtoScenarioModule load path).

### 2.d.i Client-side apply path on catchup / echo (integration-logic MUST FIX #1)

How does an inbound `AgencyWolfDepotStateMsgData` (echo or catchup) actually update the client's live `ScenarioPersister.Depots` list? Three possible shapes:

- **(a) Reflective mutation of `ScenarioPersister.Depots` protected list.** Direct dict-style add/update via `AccessTools.FieldRefAccess<>`. Brittle to WOLF version drift.
- **(b) `OnLoad`-equivalent reconstruction.** Client serializes the wire entries to a ConfigNode, invokes `ScenarioPersister.OnLoad(node)`. Heavyweight — full re-load of all 5 lists per echo.
- **(c) AgencyMembership-internal mirror only; the live `ScenarioPersister` is rebuilt by the next scene-load's projection apply.** The client's `AgencyMessageHandler` stores the inbound state in `AgencyMembership.VesselOwnership`-style structures. Live WOLF state stays as whatever was last applied via the scenario-projection path (which fires on every `SendScenarioModules` from server).

**Locked design: (c).** Matches the Stage 5.18a `AgencyMembership` mirror pattern + Phase 3's `AgencyKolonyEntry` apply path:

- Inbound `AgencyWolfDepotStateMsgData` updates a new `AgencyMembership.WolfDepots`-style internal cache (or sibling new class `WolfMembership.cs` if `AgencyMembership.cs` gets too crowded — Slice E decision).
- This cache is for client-side queries (e.g. the prefix's `ClientKerbalAgencyResolver.GetOwningAgencyForKerbal` lookup against `FlightGlobals.Vessels`, see §8.e).
- **Live `ScenarioPersister.Depots / .CrewRoutes / etc.` lists are NOT updated by the wire echo.** They get rebuilt by KSP's normal scenario-load cycle: server's next `SendScenarioModules` ships the projected `WOLF_ScenarioModule` blob; client's KSP calls `WOLF_ScenarioModule.OnLoad` which calls `ScenarioPersister.OnLoad`; the persister rebuilds all 5 lists.
- This means the **echo's role is for the LMP-side mirror only** — the live WOLF UI updates lag by one scenario-load cycle (typically scene transition or save / load). This matches Phase 3's behavior; not a regression.

**Implication for Slice E:** the client-side apply path needs ONE new mirror cache (the `WolfMembership.cs` or equivalent), NOT five reflective mutations against WOLF's protected lists. Lower brittleness, simpler impl, consistent with Phase 3 precedent.

**Forward-edge consideration:** if a future product decision wants real-time WOLF UI updates (sub-second cadence vs the current scenario-load cadence), shape (a) or (b) would be needed. Defer to future iteration; current shape is acceptable for v5 release.

### 2.e Single-class-per-slot wire-type contract

Per spec §2.e (the established convention since Phase 3): each MsgData is reused both directions:

- **C→S**: client postfix → server `AgencyMsgReader` dispatch → router `TryRoute` → server-derived agency (wire-supplied AgencyId IGNORED, same trust posture as `AgencyContractRouter`).
- **S→C**: server `AgencySystemSender.SendXxxStateToOwner` → client `AgencyMessageHandler` → local `ScenarioPersister` mirror update.
- **S→C catch-up**: same MsgData type on handshake-time bulk send.

5 new MsgData classes:

1. `AgencyWolfDepotStateMsgData` — `List<AgencyWolfDepotEntry> Entries`, `List<string> RemovedKeys` (defensive; depots aren't normally removed but the field future-proofs).
2. `AgencyWolfRouteStateMsgData` — `List<AgencyWolfRouteEntry> Entries`, `List<string> RemovedKeys`.
3. `AgencyWolfHopperStateMsgData` — `List<AgencyWolfHopperEntry> Entries`, `List<string> RemovedKeys`.
4. `AgencyWolfTerminalStateMsgData` — `List<AgencyWolfTerminalEntry> Entries`, `List<string> RemovedKeys`.
5. `AgencyWolfCrewRouteStateMsgData` — `List<AgencyWolfCrewRouteEntry> Entries`, `List<string> RemovedKeys`. **No `RejectionReason` field** per §2.b.v Option C (silent server-side drop on cross-agency-kerbal reject; client-side prefix is the UX path).

Each carries `MaxEntryCount` (per `[[feedback-wire-msgdata-chunking-caps]]`); both sender-side cap-throw + reconnect catch-up chunks. **Concrete constants:**

| MsgData | `MaxEntryCount` | `MaxRemovedKeyCount` | Rationale |
|---|---|---|---|
| `AgencyWolfDepotStateMsgData` | 200 | 50 | Realistic depot count per agency is ~tens; cap is operator-protective against malformed wire. Depots aren't normally removed mid-session, so RemovedKeys cap is small. |
| `AgencyWolfRouteStateMsgData` | 200 | 50 | Same shape as Depot; routes connect depot pairs so the count is bounded by depots-squared but typically ~tens. |
| `AgencyWolfHopperStateMsgData` | 200 | 200 | Hoppers can be created/removed at parity, so cap symmetric. Mid-session removal is the legitimate path. |
| `AgencyWolfTerminalStateMsgData` | 200 | 200 | Same as Hopper. |
| `AgencyWolfCrewRouteStateMsgData` | 100 | 50 | CrewRoutes are heavier (carry Passenger list); lower cap. |

**Send-side cap-throw shape** (mirrors `AgencyKolonyStateMsgData.InternalSerialize` precedent at [Server/System/Agency/AgencyKolonyRouter.cs](../../Server/System/Agency/AgencyKolonyRouter.cs)): if `Entries.Count > MaxEntryCount` or `RemovedKeys.Count > MaxRemovedKeyCount`, throw with a descriptive message. Caller's responsibility to chunk.

**Catchup chunking shape** (mirrors `SendOrbitalCatchupTo` precedent — Phase 3 Slice D-1 closed the symmetric-caps gap per `[[feedback-wire-msgdata-chunking-caps]]`): `AgencySystemSender.SendWolfDepotCatchupTo` (and siblings) iterate the agency's dict in chunks of `MaxEntryCount`, emit one MsgData per chunk. Multi-chunk catchup is rare (only on universes with >100-200 entries per type) but the loop must be present from Slice A — asymmetric guards = invisible producer bugs.

Caps are operator-protective against malformed wire bytes, not normal-traffic constraints. Real megabase cohorts could approach the lower bound on `AgencyWolfCrewRouteStateMsgData` (100) — bump to 200 if soak shows clipping; keep symmetric on send + catchup sides per the chunking-caps discipline.

### 2.f Entity class shapes — concrete field tables (consumer-lens MUST FIX #3)

The 5 wire entry classes + 2 nested helper classes. Slice A authors WOLF entity classes following exactly these shapes. Fields derived from WOLF source `OnSave` / `OnLoad` methods at the pinned MKS SHA `ed0f6aa6`; verify at impl time if WOLF version drift.

**Common conventions** (all 7 classes):

- Class location: `LmpCommon/Message/Data/Agency/` (mirrors `AgencyKolonyEntry.cs` Phase 3 precedent).
- `[Serializable]` attribute on the class.
- Field types use `LmpCommon` wire-safe primitives: `string` (UTF-8 length-prefixed), `int`, `double`, `bool`, `byte[]`, `List<T>` of nested wire-safe entries. **No KSP-engine types on the wire** (no `ProtoCrewMember`, no `ConfigNode`, no `Vessel` references).
- `MaxXxxLength` constants for any string field whose runtime upper bound matters (typically 64-128 chars per the existing convention).
- Field names mirror WOLF source field names exactly (case-sensitive) — eases ConfigNode round-trip + makes the projector splice's `entryNode.GetValue("Body")` lookup trivially correct.
- ConfigNode persistence: child node names per the "Common conventions" table below.

**ConfigNode persistence container names** (in `Universe/Agencies/{guid}.txt`):

| Dict | Container node | Per-entry node | Nested container | Nested per-entry node |
|---|---|---|---|---|
| `WolfDepots` | `WOLF_DEPOTS` | `WOLF_DEPOT` | `WOLF_RESOURCE_STREAMS` | `WOLF_RESOURCE_STREAM` |
| `WolfRoutes` | `WOLF_ROUTES` | `WOLF_ROUTE` | `WOLF_ROUTE_RESOURCES` | `WOLF_ROUTE_RESOURCE` |
| `WolfHoppers` | `WOLF_HOPPERS` | `WOLF_HOPPER` | — | — |
| `WolfTerminals` | `WOLF_TERMINALS` | `WOLF_TERMINAL` | — | — |
| `WolfCrewRoutes` | `WOLF_CREWROUTES` | `WOLF_CREWROUTE` | `WOLF_PASSENGERS` | `WOLF_PASSENGER` |

#### 2.f.i `AgencyWolfDepotEntry`

Derived from WOLF [Depot.cs:13-16, 219-264](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Depot.cs).

| Field | Type | ConfigNode key | Source |
|---|---|---|---|
| `Body` | `string` | `Body` | `Depot.Body` |
| `Biome` | `string` | `Biome` | `Depot.Biome` |
| `IsEstablished` | `bool` | `IsEstablished` | `Depot.IsEstablished` |
| `IsSurveyed` | `bool` | `IsSurveyed` | `Depot.IsSurveyed` |
| `ResourceStreams` | `List<AgencyWolfResourceStreamEntry>` | nested under `WOLF_RESOURCE_STREAMS` child | `Depot._resourceStreams.Values` |

**Dict key in `AgencyState.WolfDepots`:** `$"{Body}|{Biome}"` (Ordinal compare).

#### 2.f.ii `AgencyWolfResourceStreamEntry` (nested in Depot)

Derived from WOLF `Depot._resourceStreams` (`IResourceStream` shape — see [ResourceStream.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ResourceStream.cs); reading shows 3 wire-serialized fields).

| Field | Type | ConfigNode key |
|---|---|---|
| `ResourceName` | `string` | `ResourceName` |
| `Incoming` | `int` | `Incoming` |
| `Outgoing` | `int` | `Outgoing` |

#### 2.f.iii `AgencyWolfRouteEntry`

Derived from WOLF [Route.cs:41-52, 188-206](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Route.cs).

| Field | Type | ConfigNode key | Source |
|---|---|---|---|
| `OriginBody` | `string` | `OriginBody` | `Route.OriginBody` |
| `OriginBiome` | `string` | `OriginBiome` | `Route.OriginBiome` |
| `DestinationBody` | `string` | `DestinationBody` | `Route.DestinationBody` |
| `DestinationBiome` | `string` | `DestinationBiome` | `Route.DestinationBiome` |
| `Payload` | `int` | `Payload` | `Route.Payload` |
| `Resources` | `List<AgencyWolfRouteResourceEntry>` | nested under `WOLF_ROUTE_RESOURCES` child | `Route._resources` |

**Dict key in `AgencyState.WolfRoutes`:** `$"{OriginBody}|{OriginBiome}|{DestinationBody}|{DestinationBiome}"` (Ordinal).

#### 2.f.iv `AgencyWolfRouteResourceEntry` (nested in Route)

Derived from WOLF `Route._resources` dict (string key → int quantity, emitted as paired nodes per [Route.cs:200-203](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Route.cs#L200-L203)).

| Field | Type | ConfigNode key |
|---|---|---|
| `ResourceName` | `string` | `ResourceName` |
| `Quantity` | `int` | `Quantity` |

#### 2.f.v `AgencyWolfHopperEntry`

Derived from WOLF [HopperMetadata.cs:12-14, 37-49](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/HopperMetadata.cs).

| Field | Type | ConfigNode key | Source |
|---|---|---|---|
| `Id` | `string` | `Id` | `HopperMetadata.Id` (Guid ToString **with hyphens** per `HopperMetadata.cs:18`) |
| `Body` | `string` | `Body` | `HopperMetadata.Depot.Body` |
| `Biome` | `string` | `Biome` | `HopperMetadata.Depot.Biome` |
| `Recipe` | `string` | `Recipe` | Flat `"resource,qty,resource,qty,..."` format per `HopperMetadata.cs:44-48`. Single ConfigNode value; not a nested structure. |

**Dict key in `AgencyState.WolfHoppers`:** `Id` (the Guid string with hyphens — Ordinal compare).

**Gotcha:** Hopper Id format is `Guid.NewGuid().ToString()` (with hyphens). Terminal Id format is `Guid.NewGuid().ToString("N")` (no hyphens, hex only). Different formats by design in WOLF source; preserve the difference in the wire entry classes — DO NOT normalize.

#### 2.f.vi `AgencyWolfTerminalEntry`

Derived from WOLF [TerminalMetadata.cs:9-18, 31-37](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/TerminalMetadata.cs).

| Field | Type | ConfigNode key | Source |
|---|---|---|---|
| `Id` | `string` | `Id` | `TerminalMetadata.Id` (Guid ToString("N") **no hyphens** per `TerminalMetadata.cs:15`) |
| `Body` | `string` | `Body` | `TerminalMetadata.Body` |
| `Biome` | `string` | `Biome` | `TerminalMetadata.Biome` |

**Dict key in `AgencyState.WolfTerminals`:** `Id` (Guid string, "N" format, Ordinal compare).

#### 2.f.vii `AgencyWolfCrewRouteEntry`

Derived from WOLF [CrewRoute.cs:51-65, 253-276](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/CrewRoute.cs).

| Field | Type | ConfigNode key | Source |
|---|---|---|---|
| `ArrivalTime` | `double` | `ArrivalTime` | `CrewRoute.ArrivalTime` |
| `OriginBody` | `string` | `OriginBody` | `CrewRoute.OriginBody` |
| `OriginBiome` | `string` | `OriginBiome` | `CrewRoute.OriginBiome` |
| `DestinationBody` | `string` | `DestinationBody` | `CrewRoute.DestinationBody` |
| `DestinationBiome` | `string` | `DestinationBiome` | `CrewRoute.DestinationBiome` |
| `Duration` | `double` | `Duration` | `CrewRoute.Duration` |
| `EconomyBerths` | `int` | `EconomyBerths` | `CrewRoute.EconomyBerths` |
| `LuxuryBerths` | `int` | `LuxuryBerths` | `CrewRoute.LuxuryBerths` |
| `FlightNumber` | `string` | `FlightNumber` | `CrewRoute.FlightNumber` (3-char namespace per WOLF; display-only) |
| `FlightStatus` | `string` | `FlightStatus` | `CrewRoute.FlightStatus` (enum, serialized as enum-name string for forward-compat — matches WOLF source) |
| `UniqueId` | `string` | `UniqueId` | `CrewRoute.UniqueId` (Guid ToString("N") **no hyphens** per `CrewRoute.cs:90`) |
| `Passengers` | `List<AgencyWolfPassengerEntry>` | nested under `WOLF_PASSENGERS` child | `CrewRoute.Passengers` |

**Dict key in `AgencyState.WolfCrewRoutes`:** `UniqueId` (Guid string, "N" format, Ordinal compare).

**Culture-sensitivity:** `ArrivalTime` and `Duration` are `double` — serialize/parse with `CultureInfo.InvariantCulture` per `[[stack-notes-invariant-culture]]` precedent (BUG-013 family / Invariant 9). Phase 3's `AgencyKolonyEntry` numeric fields use `ParseDoubleOrZero` ([AgencyState.cs:1009-1019](../../Server/System/Agency/AgencyState.cs#L1009-L1019)) which threads the invariant-culture parse — mirror that helper for Phase 4 numerics.

#### 2.f.viii `AgencyWolfPassengerEntry` (nested in CrewRoute)

Derived from WOLF [Passenger.cs:22-26, 59-76](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Passenger.cs).

| Field | Type | ConfigNode key | Source |
|---|---|---|---|
| `Name` | `string` | `Name` | `Passenger.Name` (`ProtoCrewMember.name` — used for `KerbalAgencyResolver` lookups) |
| `DisplayName` | `string` | `DisplayName` | `Passenger.DisplayName` |
| `IsTourist` | `bool` | `IsTourist` | `Passenger.IsTourist` |
| `Occupation` | `string` | `Occupation` | `Passenger.Occupation` (e.g. "Pilot", "Engineer", "Scientist") |
| `Stars` | `int` | `Stars` | `Passenger.Stars` (experienceLevel 0-5) |

No nested structure; flat fields only.

#### 2.f.ix Forward-compat + per-entry isolation contract

Following the [AgencyState.cs:980-1024](../../Server/System/Agency/AgencyState.cs#L980-L1024) Phase 3 precedent:

- **Missing entries / containers** load as empty collections (`new List<T>()` / `new Dictionary<...>()`) — never null.
- **Missing required fields** (`Body` / `Biome` / `Id` / `UniqueId`) skip the entry with `LunaLog.Warning($"[fix:WOLF-R4] {entry-type} skipped: missing {field-name}")`. Sibling entries continue loading.
- **Malformed numerics** (`double`, `int`) default to 0 via the existing `ParseDoubleOrZero` / `ParseIntOrZero` helpers.
- **Malformed Guid** (Hopper/Terminal/CrewRoute Ids) — preserve raw string per the Phase 3 `KolonyEntries` precedent (line 1001-1003 normalizes if parseable, raw fallback if not); enables operator hand-edit + handles WOLF-quirk Ids that don't round-trip through Guid.TryParse.
- **Duplicate dict keys** keep the LAST occurrence (Dictionary indexer overwrites — operator hand-edit case).

All five entity classes follow this contract via the existing per-entry-isolation patterns.

### 3.a Per-entry exception isolation

Identical to Phase 3 + AgencyContractRouter precedent. Single try/catch covers the entire per-entry pipeline including classify + cross-agency check + upsert. A malformed entry's failure is logged with `[fix:WOLF-R4]` prefix; the batch continues. Rationale: WOLF persisted data is operator-hand-edit-vulnerable (the disk file is plain-text ConfigNode); one corrupt entry must not abort the per-agency apply for hundreds of valid siblings.

### 3.b Per-agency lock discipline

All mutations to `AgencyState.WolfDepots / .WolfRoutes / .WolfHoppers / .WolfTerminals / .WolfCrewRoutes` hold `AgencySystem.GetAgencyLock(agencyId)`. Reads that iterate `.Values` (projector, sender) also hold the lock for snapshot atomicity. Pattern established Phase 3 and uniform across all per-agency routers.

### 3.c Defensive copy of mutable wire payloads

`AgencyWolfRouteEntry._resources` is a `Dictionary<string, int>` from the wire. Storing the reference directly into `AgencyState.WolfRoutes` would let a subsequent re-arrival mutate the same dict in place. **Copy on store** via `new Dictionary<string,int>(incoming, StringComparer.Ordinal)`.

`AgencyWolfCrewRouteEntry.Passengers` is a `List<AgencyWolfPassengerEntry>`. Similarly defensive copy.

`AgencyWolfDepotEntry.ResourceStreams` (similar dict shape) — defensive copy.

Same pattern as Phase 3 `AgencyOrbitalTransferEntry.PayloadBytes` precedent.

### 3.d Cross-agency rejection at the router AND defense-in-depth at the embark prefix

**Two layers** in line with the 5.17a + write-path counterpart precedent:

1. **Router-level (server-side):** `AgencyWolfCrewRouter.TryRoute` per-entry cross-agency kerbal check. Authoritative. Silent drop + Warning log per §2.b.v Option C (no snap-back echo; rejected entries are absent from the success-only owner echo).
2. **Prefix-level (client-side, UX-only):** `WOLF_CrewTransferScenario_LaunchPrefix` warns + aborts BEFORE the kerbal-removal storm. Cannot be trusted (modified clients can patch around it) but provides instant UX feedback for legitimate clients.

Defense in depth — the prefix is UX, the router is authority.

### 3.e Threading model + coalescing for high-frequency postfixes

Most postfixes (Create*/Establish/Survey) are rare and per-action: emit immediately via the `TaskFactory.StartNew → NetworkSender.QueueOutgoingMessage` two-line pattern established by Stage 5.18a (`LmpClient/Systems/Agency/AgencyMessageSender.cs`).

**`Depot.NegotiateProvider/NegotiateConsumer` postfix is a hotspot** — Negotiate fires every time MKS' resource conversion ticks (potentially every `FixedUpdate` on a busy depot). Emitting per-tick floods the wire. Phase 4 introduces **per-depot debounce** in the client mirror: collect Negotiate-driven changes on a tick, batch-emit on the next 1-second timer. Same shape as `VesselResourceMessageSender`'s 2.5s pulse.

**Verified at impl time** — if cadence shows up in profiling, expand the coalescing window. If not, the simple "emit on next 1s timer" suffices.

### 3.f Operator notification

Each router emits one `[fix:WOLF-R4]` log line per batch at Debug level (not per-entry — noise). The cross-agency-kerbal reject path logs at **Warning** level per `[[feedback-review-lens-framing]]` operator-visibility convention (matches Phase 3 cross-agency reject Warning).

`ForkBuildInfo.ActiveFixes` gets `"WOLF-R4"` appended at boot.

### 3.g `IgnoredScenarios` — gate-conditional broadcast suppression (Phase 3 Option B reuse)

The Phase 3 Slice B `PerAgencyOnlyIgnoreSend` HashSet extension lives in `LmpClient/Systems/Scenario/ScenarioSystem`. Phase 4 adds one entry:

```text
private static readonly HashSet<string> PerAgencyOnlyIgnoreSend = new HashSet<string>(StringComparer.Ordinal)
{
    "KolonizationScenario",       // Phase 3 Slice B
    "PlanetaryLogisticsScenario", // Phase 3 Slice C
    "ScenarioOrbitalLogistics",   // Phase 3 Slice D
    "WOLF_ScenarioModule",        // Phase 4 — per-agency WOLF router handles under gate=on
};
```

Gate=on suppresses the broadcast; gate=off keeps the legacy 30s SHA pass. Same as Phase 3.

---

## 4. Per-agency dovetail — gate-on, gate-off, Sandbox/Science, upgrade-in-place

### 4.a Gate=on (`PerAgencyCareer=true` + GameMode=Career, the Stage 5 design)

- Routers fire on every WOLF mutation.
- Projection splices per-agency state into outgoing scenarios.
- Cross-agency CrewRoute kerbal seizure is closed (router rejects + snap-back echo + UX preflight).
- Agency separation is enforced end-to-end: privacy (no cross-agency WOLF state leak), persistence (`Universe/Agencies/{guid}.txt` carries per-agency WOLF state).

### 4.b Gate=off (`PerAgencyCareer=false`, shared-scenario default)

**Postfix is a no-op; 30s SHA pass unchanged; cross-agency kerbal seizure remains possible (pre-Phase-4 baseline).**

- `AgencySystem.PerAgencyEnabled` returns false.
- Server-side routers' `TryRoute` returns false immediately; the projector's `Project` returns input text unchanged.
- Client-side postfixes early-return (no wire emit). The `IgnoredScenarios` gate-conditional filter doesn't suppress `WOLF_ScenarioModule`. **Result: shared-mode WOLF propagation is bit-identical to the pre-Phase-4 `master` baseline.**
- **Cross-agency kerbal seizure** under gate=off: a known pre-existing shared-mode hazard that Phase 4 does NOT close. Under shared-mode "cross-agency" isn't a concept — all kerbals belong to all players by definition; the seizure UX is the intended shared product. Operators wanting strict kerbal authority need per-agency mode. Documented as a known limitation (§5).

**Dual-mode silence preserved (per spec §11):** zero observable regression for shared-mode operators on any surface. WOLF behaves exactly as pre-Phase-4 under gate=off.

### 4.c Sandbox / Science game mode

- `AgencySystem.PerAgencyEnabled` includes the `GameMode == Career` check; under Sandbox/Science it's always false even if `PerAgencyCareer=true` is misconfigured.
- All routers no-op; projection no-ops; client-side postfixes early-return.
- WOLF behavior under Sandbox/Science is identical to pre-Phase-4. WOLF doesn't actually do anything "career-affecting" by stock design (no Funds/Sci/Rep emission from WOLF logistics), so Sandbox/Science suitability of WOLF is unchanged.

### 4.d Pre-0.31 upgrade-in-place — boot-time diagnostic + hazard-gate wiring

Mirror of Phase 3's `WarnAboutSharedKolonyOnUpgrade` / etc.

**Hazard-gate wiring (upgrade-lens MUST FIX precedent).** Append one boot-time warning to `AgencySystem.LoadExistingAgencies`:

```text
WARNING [fix:WOLF-R4] Pre-0.31 upgrade detected: shared WOLF_ScenarioModule has N existing
entries (D depots, R routes, H hoppers, T terminals, C crew routes) that will be STRIPPED
from the projected view on first per-agency client connect. Existing shared WOLF state does
NOT migrate to specific agencies.

CRITICAL: any CrewRoute currently in-flight (FlightStatus=Boarding or Enroute) carries kerbals
in RosterStatus.Missing state with SetTimeForRespawn(double.MaxValue) per WOLF source. Once
the route is stripped on first per-agency connect, those kerbals are PERMANENTLY ORPHANED —
no CrewRoute exists to call Disembark, and the shared kerbal roster carries them as Missing
forever. The K1 kerbal-roster grief guard (Stage 5.17e-8) prevents cross-agency removal of
these kerbals but does NOT restore them.

Operator options:
  (1) BEFORE upgrade: ensure no CrewRoutes are mid-flight (let in-flight routes complete to
      Arrived state OR Disembark all passengers OR delete the routes via in-game UI). Then
      reset the shared WOLF state via removing the WOLF child nodes from
      Universe/Scenarios/WOLF_ScenarioModule.txt. Players accept the loss of remaining state.
  (2) Manually re-create the WOLF graph on each agency's vessels after the gate flips
      (depots are tied to vessels with WOLF_DepotModule parts).
  (3) Hand-edit Universe/Agencies/{guid}.txt to migrate WOLF state to the right agencies
      BEFORE first per-agency connect (out-of-scope bulk-migration tooling per pre-spec §5).
  (4) Stay on shared-agency mode (PerAgencyCareer=false) — no data loss; WOLF continues as
      shared graph under the Phase 4 gate=off path.

If kerbal stranding does occur post-upgrade, recovery options are limited: deleteagency on
the affected agency removes its WOLF state but does NOT restore Missing kerbals to the AC
pool; manual ConfigNode editing of Universe/Kerbals/{name}.txt (set rosterStatus=Available,
type=Crew, time=0) is the only currently-supported restoration path.

Set AllowEnablePerAgencyOnExistingUniverse=true in Settings/GameplaySettings.xml to acknowledge
and proceed; otherwise the server refuses startup.
```

**Hazard predicate wiring shape (upgrade-lens MUST FIX #1):** the existing 10 hazard branches in `RefuseStartupIfUpgradeHazardWithoutOverride` at [Server/System/Agency/AgencySystem.cs](../../Server/System/Agency/AgencySystem.cs) follow an inline fall-through `if (!hasHazard && ScenarioStoreSystem.CurrentScenarios.TryGetValue("<name>", out var scn)) { lock (Scenario.ScenarioDataUpdater.GetSemaphore("<name>")) { /* inspect scn */ } }` pattern. **Phase 4 follows the same shape — NOT an extracted-predicate convention.** The Phase 4 branch:

```text
// Inside RefuseStartupIfUpgradeHazardWithoutOverride (after the existing 10 branches)
if (!hasHazard
    && ScenarioStoreSystem.CurrentScenarios.TryGetValue("WOLF_ScenarioModule", out var wolfScn))
{
    lock (Scenario.ScenarioDataUpdater.GetSemaphore("WOLF_ScenarioModule"))
    {
        // Check ALL 5 child node families. Missing-any-one of them is a silent-pass
        // hazard — operator with only TERMINALS state would boot into strip-on-first-
        // connect without acknowledgment. Mirrors the existing predicate at
        // AgencySystem.cs:441-446 for ProgressFacility (which checks multiple child
        // node families uniformly).
        if (wolfScn.HasNode("CREWROUTES") && wolfScn.GetNode("CREWROUTES").CountNodes > 0) hasHazard = true;
        else if (wolfScn.HasNode("DEPOTS") && wolfScn.GetNode("DEPOTS").CountNodes > 0) hasHazard = true;
        else if (wolfScn.HasNode("HOPPERS") && wolfScn.GetNode("HOPPERS").CountNodes > 0) hasHazard = true;
        else if (wolfScn.HasNode("ROUTES") && wolfScn.GetNode("ROUTES").CountNodes > 0) hasHazard = true;
        else if (wolfScn.HasNode("TERMINALS") && wolfScn.GetNode("TERMINALS").CountNodes > 0) hasHazard = true;
        // Note: empty child nodes are tolerated. Only NON-EMPTY content triggers
        // the refuse-startup. Matches the existing pattern.
    }
}
```

Joins the existing predicate disjunction: savings (Funding/RnD/Reputation) + ContractSystem + Research + ProgressFacility + KolonizationScenario + PlanetaryLogisticsScenario + ScenarioOrbitalLogistics + SCANcontroller + DMScienceScenario + StrategySystem ≈ 10 distinct hazard branches; Phase 4 adds the 11th (WOLF_ScenarioModule). The `lock (GetSemaphore("WOLF_ScenarioModule"))` acquire matches the BUG-033 precedent — read access during boot must serialize against any concurrent in-flight scenario writer (none should exist during boot, but the lock is the established discipline).

**Why all 5 children must be checked:** an operator with WOLF state that only contains TERMINALS (no DEPOTS) — say, a partial cleanup state from a hand-edit — would silently boot if the predicate only checked DEPOTS. Same-class hazard the existing ProgressFacility predicate already mitigates by checking multiple ScenarioUpgradeableFacilities children uniformly.

**Flip-back-out behaviour:** if operator enables Phase 4 + per-agency mode, plays a session, then flips `PerAgencyCareer=false`: per-agency `WolfDepots / .WolfRoutes / .WolfHoppers / .WolfTerminals / .WolfCrewRoutes` in `Universe/Agencies/{guid}.txt` files are FROZEN on disk (no router runs to update them under gate=off). The shared `WOLF_ScenarioModule.txt` continues accumulating from the legacy 30s SHA pass. On a future flip back to gate=on, the frozen per-agency entries reappear stale relative to the now-diverged shared state. Same caveat as Phase 3 §4.d — `INFO` log on detection at gate transition.

### 4.e Migration on `transferagency` / `setvesselagency` / `deleteagency`

Per pre-spec §11 + the per-router migration contract:

- **`deleteagency`** — walks the 5 WOLF dicts on the deleted agency and removes ALL entries (matches the existing deleteagency clearing of `Contracts` / `KolonyEntries` / `PlanetaryEntries` / `OrbitalTransfers` at the corresponding 5.18d / Phase 3 helpers). Wire-side: broadcasts `AgencyVisibilityMsgData` for the deletion + sends each surviving owner their (unchanged) WOLF state echo for closure.

  **Mid-flight CrewRoute kerbal restoration (upgrade-lens MUST FIX #2):** BEFORE removing `WolfCrewRoutes` dict entries, walk the agency's CrewRoutes and restore each Passenger's roster state. Per `WOLF_CrewTransferScenario.cs:587-589`, mid-flight passengers have `kerbal.rosterStatus = RosterStatus.Missing` + `SetTimeForRespawn(double.MaxValue)`. If deleteagency removes the route without restoring the kerbals, they're **permanently orphaned** — no remaining CrewRoute calls Disembark, the shared kerbal roster carries them as Missing forever. Phase 4 deleteagency MUST:

  ```text
  // Inside DeleteAgencyCommand's per-agency cleanup (Slice F or Slice E, depending on order):
  foreach (var route in agency.WolfCrewRoutes.Values) {
    // FlightStatus.Boarding routes haven't seized any kerbals yet (still on source vessel).
    // FlightStatus.Arrived routes have already disembarked.
    // Only Enroute routes need restoration.
    if (route.FlightStatus != "Enroute") continue;
    if (route.Passengers == null) continue;

    foreach (var passenger in route.Passengers) {
      // Look up the kerbal in the shared roster (Universe/Kerbals/{name}.txt).
      var path = Path.Combine(KerbalSystem.KerbalsPath, $"{passenger.Name}.txt");
      if (!FileHandler.FileExists(path)) {
        LunaLog.Warning(
          $"[fix:WOLF-R4] deleteagency cleanup: kerbal '{passenger.Name}' missing from roster; " +
          $"skipping restoration (was on CrewRoute {route.UniqueId} in agency {agency.AgencyId:N}).");
        continue;
      }
      // Re-stamp the kerbal ConfigNode: rosterStatus=Available, type=Crew, time=0.
      // Mirror the operator hand-edit recipe documented in §4.d WARN text — this is
      // the SAME restoration operation, automated.
      KerbalSystem.RestoreKerbalToRoster(passenger.Name);   // new helper, Slice E
      MessageQueuer.RelayMessage<KerbalSrvMsg>(client, ...);  // broadcast roster update
      LunaLog.Normal(
        $"[fix:WOLF-R4] deleteagency restored kerbal '{passenger.Name}' to AC pool " +
        $"(was Missing on CrewRoute {route.UniqueId} in deleted agency {agency.AgencyId:N}).");
    }
  }
  // THEN walk the dicts and remove all 5 sets of entries.
  ```

  New helper `KerbalSystem.RestoreKerbalToRoster(string name)` reads the kerbal's ConfigNode, sets `state = Available`, `type = Crew`, `ToD = 0`, writes back atomically. Slice F can ship this helper alongside the deleteagency cleanup if Slice E hasn't already needed it. Without this, deleteagency-on-WOLF-bearing-agency is a permanent-kerbal-loss vector that operators have no in-tool recovery from.
- **`transferagency`** — owner-RENAME only; the AgencyId is preserved, so all 5 dicts move with the renamed agency unchanged. No data migration needed.
- **`setvesselagency`** — moves a vessel's `OwningAgencyId` from A to B. **WOLF state does NOT move with the vessel** — none of the 5 WOLF dicts are vessel-keyed:
  - Depots/Routes are body+biome-keyed.
  - Hoppers/Terminals are Guid-keyed (and the Guid is per-agency-issued).
  - CrewRoutes are Guid-keyed by `CrewRoute.UniqueId`; the route conceptually belongs to the agency that created it, not to whatever vessel happens to host the kerbal mid-flight.

The only legitimate cross-agency-vessel-effect of setvesselagency is the **passenger kerbal authority gate** — if a CrewRoute is mid-flight and the destination vessel changes hands via setvesselagency, the destination is now owned by a different agency. The router's cross-agency kerbal check at `Embark` time wouldn't fire (kerbal is already on the CrewRoute), but the kerbal's eventual disembark happens normally regardless of destination ownership.

**Phase 4 ships zero new migration helpers for setvesselagency** — the WOLF state stays put. Documented in admin help text update for `setvesselagency`.

### 4.f Admin command refusal under gate=on

No new admin commands. The 5.18d admin command family (`setvesselagency`, `transferagency`, `deleteagency`) inherits the Phase 4 contract via §4.e.

---

## 5. Out-of-scope items

| Surface | Why not Phase 4 | Where it lives |
|---------|-----------------|----------------|
| WOLF `FlightNumber` hardening | 3-char namespace with silent collision after 10 retries is display-only; `CrewRoute.UniqueId` is the correctness anchor. | Upstream PR to USI/MKS (handoff §11), low priority. |
| Unloaded converter catch-up under per-agency for WOLF hoppers | Tied to R3 catch-up; needs Strategy B integration. Hopper mutations during long unloaded periods aren't currently a Phase 4 concern (hopper recipes only fire when the hopper's parent depot is loaded). | Phase 5 (handoff §R3, optional product). |
| WOLF UI rendering polish under per-agency | Cross-agency observation of others' depots (e.g. Alice's depot icon visible on Bob's map view) — the projector strips them from Bob's local `ScenarioPersister` so they shouldn't render at all, but verify in soak. | R5 / Phase 5 UI polish. |
| Cross-agency CrewRoute kerbal **soft sharing** (e.g. mutual consent flow) | User-confirmed s38: REJECT cross-agency kerbals. No mutual-consent UI; if a player wants to lend a kerbal, they transfer the vessel via `setvesselagency`. Soft-sharing is out of scope. | Future product iteration if cohort feedback demands it. |
| WOLF and USI-LS gameplay interaction | Separate brief (handoff §1, §12 explicit out-of-scope). | Out-of-scope. |
| Per-agency WOLF in N:1 multi-player-per-agency model | Phase 4's gate=on design is exact for 1-player-per-agency. The per-router upsert + cross-agency reject leans on the 1:1 invariant; under N:1 the design would need re-derivation (e.g. "any agency-member can register depot for the agency, but resource flows need lock-of-the-day handoff"). | Re-derive Phase 4 (and likely Phases 3/5) if a future product decision opens N:1. |
| Server-side resource-flow simulation for WOLF | Operator confirmed client-side authoritative simulation is the right approach (Phase 4 doesn't attempt to run WOLF on the server). | Out-of-scope by design. |
| Bulk-migration tooling for upgrade-in-place | The §4.d operator recipe is "reset the shared WOLF state OR stay on shared mode". Single-vessel `setvesselagency` doesn't apply (WOLF isn't vessel-keyed). | Operator decision; admin-script territory if cohort demand emerges. |

---

## 6. Brittleness — WOLF internal namespace + signature surface

Phase 4's hooks against MKS' WOLF internal types share the same brittleness class as R0 + R1 + R2's MKS-version-mismatch surface:

1. **`ScenarioPersister.CreateDepot/CreateRoute/CreateHopper/CreateCrewRoute/CreateTerminal/RemoveHopper/RemoveTerminal`** — public method signatures match `IRegistryCollection` interface contract; relatively stable across WOLF versions. **Mitigation:** `AccessTools.Method(AccessTools.TypeByName("WOLF.ScenarioPersister"), "CreateDepot")` at module-load; emit `[fix:WOLF-R4] WOLF.ScenarioPersister.CreateDepot not found; per-agency WOLF routing disabled` and abort patch registration if resolution fails — self-disabling pattern matches MKS-R0/R1/R2.

2. **`CrewRoute.Embark/Disembark/Launch/CheckArrived`** — public methods on a public type implementing `ICrewRoute`. Lower brittleness risk.

3. **`Depot.NegotiateProvider/NegotiateConsumer/Establish/Survey`** — public methods on a public type. Resolves at boot.

4. **`Route.AddResource/RemoveResource/IncreasePayload`** — public methods on a public type. Resolves at boot.

5. **`WOLF_CrewTransferScenario.Launch`** — public UI-handler method on a `MonoBehaviour` (NOT private — verify on the source: `public void Launch()` at `WOLF_CrewTransferScenario.cs:557`). Public surface; lower brittleness risk than the §6 item 5 wording of earlier drafts. **Mitigation:** `AccessTools.Method(AccessTools.TypeByName("WOLF.WOLF_CrewTransferScenario"), "Launch")` at boot + emit warning if not found. **Use `AccessTools.TypeByName` (string), NOT `typeof(WOLF.*)` (compile-time reference)** — LMP must not link against WOLF.dll at compile time because WOLF is a third-party mod that may or may not be installed at runtime. The `typeof` form would force a compile-time reference, breaking LMP builds without WOLF.dll present. All 5 WOLF type resolutions in this brittleness list use the `TypeByName` form uniformly. The UX preflight is best-effort, not load-bearing — the server-side router is authority.

6. **WOLF entity field rename / new field addition** — the wire shape (`AgencyWolfDepotEntry` etc.) mirrors the MKS-side classes. A new MKS field would flow through as an opaque addition (forward-compat tail). A renamed field would break the postfix's read. **Mitigation:** reflective read via `FieldInfo.GetValue` at impl time, OR explicit accept-list of known fields with a `LogWarning` on unknown additions. Verify at impl time (same decision Phase 3 left open).

7. **WOLF module-rename detection at boot** — append `[fix:WOLF-R4] WOLF types resolved` (or `... not found; per-agency WOLF routing disabled until WOLF is installed`) to the existing MKS-R0/R1/R2 module-resolution log lines. Single source-of-truth for operator grep: `grep -E "\[fix:(MKS-R[012]|WOLF-R4)\]" KSP.log`.

---

## 7. Test plan

### 7.a Unit tests in `ServerTest` (net10.0, MSTest)

**`ServerTest/AgencyWolfDepotRouterTest.cs`** (~10 cases):

1. `GateOff_TryRouteReturnsFalse_NoMutation`
2. `Sandbox_TryRouteReturnsFalse_NoMutation`
3. `ClientWithoutAgency_TryRouteReturnsFalse`
4. `ValidDepotEntry_Accepted_KeyedByBodyBiome`
5. `EmptyBody_EntryDroppedBatchContinues`
6. `EmptyBiome_EntryDroppedBatchContinues`
7. `RepeatedUpsertSameKey_Replaces`
8. `MultipleDepotsSameBodyDifferentBiomes_DistinctEntries`
9. `BatchWithMixedAcceptReject_PartialUpsert`
10. `ConcurrentUpsertSameKey_LastWriterWins_NoCorruption`

**`ServerTest/AgencyWolfRouteRouterTest.cs`** (~10 cases): mirror of Depot with 4-string composite key. Additional case: `IncreasePayloadPath_UpsertReplacesWithNewPayloadValue`.

**`ServerTest/AgencyWolfHopperRouterTest.cs`** (~10 cases): partition by Id. Additional cases for removal path: `RemoveHopperEcho_RemovesFromAgencyDict`, `RemoveHopperMissingId_NoOp`.

**`ServerTest/AgencyWolfTerminalRouterTest.cs`** (~10 cases): mirror of Hopper.

**`ServerTest/AgencyWolfCrewRouterTest.cs`** (~14 cases — the distinctive surface):

1-4. Standard gate/agency/client lookup cases.
5. `SameAgencyKerbal_RouteAccepted` — Passenger.Name resolves to a kerbal on requester's own vessel.
6. `UnassignedKerbal_RouteAccepted` — kerbal not aboard any vessel (AC pool / KIA-cleared).
7. `UnassignedSentinelVessel_RouteAccepted` — kerbal on a vessel with `OwningAgencyId == Guid.Empty` (pre-0.31).
8. `CrossAgencyKerbal_RouteSilentlyDropped_WarningLogged` — Passenger is on a non-Empty cross-agency vessel; entire route rejected; NO wire echo emitted to the requester; Warning log line emitted with `[fix:WOLF-R4]` prefix (Option C per §2.b.v).
9. `MultiplePassengersAllSameAgency_Accepted` — multi-kerbal route on requester's vessels.
10. `MultiplePassengersOneCrossAgency_EntireRouteRejected` — defense-in-depth: ONE bad apple rejects the whole batch for that route entry.
11. `EmptyPassengerList_Accepted` — Boarding route with no passengers yet (newly created via `CreateCrewRoute`).
12. `MalformedUniqueId_EntryDropped` — non-Guid UniqueId.
13. `PassengerWithEmptyName_Skipped` — defensive (treat as no-op, route accepted on remaining valid passengers).
14. `ConcurrentRouteUpsert_SerializeRoundTrip_StableState`.

**`ServerTest/KerbalAgencyResolverTest.cs`** (~8 cases — the new shared helper):

1. `EmptyKerbalName_ReturnsNull`
2. `NullKerbalName_ReturnsNull`
3. `KerbalNotOnAnyVessel_ReturnsNull`
4. `KerbalOnOwnAgencyVessel_ReturnsAgencyId`
5. `KerbalOnUnassignedSentinelVessel_ReturnsEmpty`
6. `KerbalOnCrossAgencyVessel_ReturnsThatAgency`
7. `VesselSerializeFailure_IsolatedScansSibling` — one vessel fails to serialize; helper continues to next.
8. `MultipleVesselsContainNeedleString_ReturnsFirstFound` — defensive (rare scenario, e.g. corrupted multi-vessel state).

**`ServerTest/K1Test_PostHelperExtraction.cs`** (~4 cases): re-run a subset of the existing K1 cases against the refactored `CanRemoveKerbalUnderK1` to prove behavior preservation through the helper extraction.

**`ServerTest/AgencyWolfProjectorTest.cs`** (~10 cases):

1-3. Gate/Sandbox/ClientWithoutAgency pass-through.
4. `EmptyAgencyWolf_StripsAllSharedEntries` — agency has 0 entries; output has empty `CREWROUTES{} DEPOTS{} HOPPERS{} ROUTES{} TERMINALS{}` child nodes.
5. `AgencyWithEntries_SplicesOnlyOwn` — agency has 2 depots + 1 route; shared scenario has 5 depots + 3 routes (from peers); output has exactly the 2 depots + 1 route.
6. `DepotsEmittedFirst_OrderConfirmed` — re-parse output; assert DEPOTS appears before CREWROUTES, HOPPERS, ROUTES, TERMINALS in the child-node sequence.
7. `MalformedAgencyEntry_DroppedBatchContinues`.
8. `WholeScenarioParseFailure_ReturnsInputUnchanged`.
9. `AgencyState_LockHeld_DuringSplice` (defensive — verify the lock acquire wraps the Values iteration).
10. `MultiAgencyConcurrentSplice_NoCrossLeak` — Bob's projection doesn't see Alice's entries even under concurrent SendScenarioModules.

**`ServerTest/AgencyStateWolfRoundTripTest.cs`** (~6 cases): per-agency persistence under a comma-decimal thread culture (`[[stack-notes-invariant-culture]]`). Tests ConfigNode round-trip of each entity type, with culture-sensitive numeric fields (none in WOLF's case — but defensive test). Forward-compat: pre-Phase-4 file loads as empty dicts on the 5 fields.

### 7.b MockClientTest end-to-end (net10.0 against ServerHarness)

**`MockClientTest/WolfRoutingTest.cs`** (~6 cases):

1. `GateOn_DepotCreate_RoutesPerAgencyOnly_PeersDontSee` — Alice creates depot; Bob doesn't see it.
2. `GateOff_DepotCreate_LegacyPathReachesBoth_DualModeSilence`.
3. `GateOn_CrossAgencyCrewRoute_RejectedWithSnapBack` — Bob tries to fly Alice's kerbal; rejection echo arrives; Bob's mirror clears the local route.
4. `GateOn_SameAgencyCrewRoute_Accepted` — Alice flies her own kerbal; route persists.
5. `GateOn_Reconnect_FullWolfStateCatchUp` — Alice disconnects with 2 depots + 1 route + 1 crew route in flight; reconnects; receives full state before mid-session mutations.
6. `GateOn_CrewRouteWithMixedKerbalAuthority_AllRejected` — defense-in-depth: 3-kerbal route with 1 cross-agency kerbal → entire route rejected, none of the kerbals seized.

### 7.c LmpCommonTest wire round-trips

**`LmpCommonTest/SerializationTests.cs`** extensions (~5 cases): 5 new WOLF MsgData types' round-trip (matches the Phase 3 + S2 + S4 wire test density).

### 7.d LmpClientTest decision-math (net472)

**`LmpClientTest/WolfCrossAgencyKerbalDecisionTest.cs`** (~8 cases): pure-helper extraction of the client-side `WOLF_CrewTransferScenario_LaunchPrefix` cross-agency check, mirroring the `AgencyLabelFormatterTest` pattern. Tests the client mirror's `VesselOwnership` lookup against the kerbal's source vessel.

---

## 8. Cross-agency CrewRoute kerbal authority — design detail

This is the distinctive Phase 4 surface that needs its own section.

### 8.a User-confirmed scope (s38)

Two policy questions confirmed via AskUserQuestion:

1. **Cross-agency CrewRoute kerbal authority** — REJECT cross-agency kerbals. Matches the cross-agency lock-acquire / vessel-write rejection pattern from 5.17a. Unassigned kerbals (K1-grief guard sentinel) treated as bypass like vessels. WOLF CrewRoute creation/embarkation refuses when any selected kerbal's source vessel's `lmpOwningAgency` differs from the requesting agency.
2. **Scope** — Full Phase 4: all 5 entity lists per-agency. Confirmed.

### 8.b Authority resolution algorithm

**Used at TWO call sites** (DRY via the new `KerbalAgencyResolver`):

1. Client-side `WOLF_CrewTransferScenario_LaunchPrefix` — UX preflight before the Launch button's storm of Embark+RemoveCrewmember calls.
2. Server-side `AgencyWolfCrewRouter.TryRoute` — authoritative gate on the inbound `AgencyWolfCrewRouteStateMsgData`.

Both call sites consult: "for the named kerbal, what agency does its current source vessel belong to?"

- If kerbal is **not aboard any vessel** (unassigned — AC pool, KIA-cleared, EVA-rescue completion): **allow**. Stock KSP processes these via different paths anyway; WOLF can't seize an absent kerbal.
- If kerbal is aboard a vessel with `OwningAgencyId == Guid.Empty` (pre-0.31 Unassigned-sentinel): **allow** per spec §10 Q3 (any agency may interact with pre-0.31 vessels).
- If kerbal is aboard a vessel owned by the requester's agency: **allow**.
- If kerbal is aboard a vessel owned by a different non-Empty agency: **REJECT**. Server logs Warning; UX shows toast on the prefix path; snap-back echo restores the client mirror.

### 8.c Why vessel-proxy instead of kerbal-level agency stamp

The fork has no kerbal-level agency stamp. The K1 grief guard at `KerbalSystem.cs:103-136` (Stage 5.17e-8) established the **vessel-proxy authority** pattern: "the kerbal's agency = the kerbal's current host vessel's agency". This pattern is acceptable for:

- WOLF CrewRoute kerbal authority (Phase 4).
- K1 grief guard on KerbalRemove (5.17e-8).
- Future cross-agency-EVA-restrict (if/when needed).

Alternatives considered + rejected:

- **Kerbal-level agency stamp** — would require:
  - New `Server/System/Agency/AgencyKerbalRoster.cs` registry.
  - Wire-extension on every `Kerbal*MsgData` (`KerbalProtoMsgData`, `KerbalRemoveMsgData`, `KerbalReplyMsgData`).
  - `AgencyState.AgencyKerbals: Dictionary<string,Guid>`.
  - Migration on `transferagency` / `setvesselagency` (e.g. all kerbals on the moved vessel transfer with it; AC-pool kerbals stay).
  - Recovery-economy integration (returning a kerbal home should NOT transfer the kerbal between agencies).
  - Spec §10 Q-Kerbal compatibility (the Stage 5 design only signed off on vessel-level OwningAgencyId; kerbal-level was deferred to "Stage 6 work" per the K1 guard XML at `KerbalSystem.cs:64-67`).

The kerbal-level stamp is the right long-term architecture but is a **Stage 6 surface**, NOT a Phase 4 prerequisite. The vessel-proxy approach is the **MVP authority gate** that closes the cross-agency kerbal seizure today without spending the Stage 6 budget upfront.

**If/when Stage 6 lands kerbal-level agency stamps, Phase 4's `KerbalAgencyResolver` becomes a one-line delegator to `AgencyKerbalRoster.GetAgencyForKerbal(name)` — no router/wire surface changes needed.** Forward-compatible by design.

### 8.d Edge cases

| Scenario | Phase 4 behavior | Reason |
|---|---|---|
| Alice's kerbal returns to KSC (recovery) → AC pool. Bob then tries to fly the kerbal via WOLF. | **Allow** | Kerbal is unassigned (not aboard any vessel). Bob legitimately recruits/uses AC kerbals. |
| Alice's vessel has a kerbal. Mid-flight, Alice does setvesselagency to give Bob the vessel. Then Bob tries to fly the kerbal via WOLF. | **Allow** | Kerbal is now on Bob's vessel. The setvesselagency mutation is the legitimate ownership transfer. |
| Alice's vessel has a kerbal. Mid-EVA, the kerbal is on a separate `EVA Kerbal` mini-vessel (a stock KSP construct). The mini-vessel's `OwningAgencyId` is set on EVA spawn. Bob tries to fly the EVA kerbal. | **Reject** if EVA vessel is stamped to Alice's agency; **allow** if Unassigned-sentinel. | Vessel-proxy works on whatever vessel the kerbal is currently on, including EVA mini-vessels. |
| Kerbal is on a vessel that's currently in flight to its destination via WOLF CrewRoute. Mid-flight, another player tries to start a new CrewRoute with the same kerbal. | **Allow if same-agency owner, reject if cross-agency owner.** | Kerbal is still considered "aboard the WOLF route" but in WOLF's data model the kerbal is `RosterStatus.Missing` with `SetTimeForRespawn(double.MaxValue)`. So `vessel.GetVesselCrew()` doesn't include them; `KerbalAgencyResolver` returns null (unassigned). The defensive "allow on null" path catches this. **Documented as an intentional WOLF design quirk** — once a kerbal is mid-flight on a CrewRoute, they're not on any vessel, so the authority gate doesn't apply until they Disembark. |
| Tourist kerbal (`ProtoCrewMember.KerbalType.Tourist`). | **Allow if on own vessel; reject if cross-agency.** | Tourist is just a flavor of `IPassenger`. Authority gate doesn't distinguish. |

### 8.e Cross-agency CrewRoute — locked design + race semantics

**Locked design (lead-with-the-answer per consumer-lens SHOULD CONSIDER #6):**

1. **Client-side prefix on `WOLF_CrewTransferScenario.Launch` is the primary UX path.** It runs synchronously before the Launch body's `Embark` + `RemoveCrewmember` storm. If any selected passenger's kerbal resolves to a non-Empty cross-agency vessel, the prefix shows a `LunaScreenMsg.PostScreenMessage` toast and returns `false` to abort the entire Launch. **No local state mutation occurs.** Same-agency + unassigned + Unassigned-sentinel kerbals pass through; the prefix returns `true` and the stock Launch body proceeds normally.
2. **Server-side `AgencyWolfCrewRouter.TryRoute` is defense-in-depth.** Modified clients that patch out the prefix can still emit `AgencyWolfCrewRouteStateMsgData` with cross-agency passengers. The router's per-entry check (§2.b.v step 5) silently drops the entire CrewRoute mutation + emits a Warning log. **No snap-back echo** (Option C per §2.b.v).
3. **No client-side rollback infrastructure.** Because the prefix prevents legitimate clients from mutating local state under gate=on for cross-agency kerbals, there's nothing to roll back. Modified-client desync is the cheater's concern per §8.f.
4. **v4 proto-guard prerequisite** ([v4-vessel-proto-cross-agency-write-guard.md](v4-vessel-proto-cross-agency-write-guard.md)) closes the underlying vessel-write hole: even if a modified client locally mutates Alice's vessel (removing Jeb), the resulting proto broadcast is rejected at the server. Alice's authoritative vessel state on the server is untouched, and Bob's next `VesselSync` reply repopulates his local view with the canonical state.

**Why this is simpler than the Path A / Path B alternatives:** earlier pre-spec drafts walked through "Path A: defer local mutation until server confirms" and "Path B: optimistic local mutation + rollback on rejection". Both alternatives assumed the v4 proto-write hole was already closed — they would have required a wire `RejectionReason` field on `AgencyWolfCrewRouteStateMsgData` + client-side `_ignoreNextRouteRemoval` flag bracketing to suppress rollback-loops. The pre-spec s39 multi-lens review surfaced that (a) the proto-write hole is in fact OPEN in v4 (driving the v4 proto-guard scoping doc); (b) once the proto-guard ships, the modified-client desync is already structurally bounded — Bob's local view recovers via VesselSync without LMP-side intervention; and (c) consequently neither Path A's deferred-mutation nor Path B's rollback apparatus add user-visible value over Option C's silent drop. Stripping the rejection wire field simplified §2.b.v + §2.e + §7.

**Client-side prefix shape (load-bearing — verify at impl time):**

```text
[HarmonyPatch(typeof(WOLF_CrewTransferScenario), "Launch")]
public static class WOLF_CrewTransferScenario_LaunchPrefix {
  static bool Prefix(WOLF_CrewTransferScenario __instance) {
    if (!AgencySystem.PerAgencyEnabled) return true;  // gate-off: stock behavior
    if (AgencySystem.LocalAgencyId == Guid.Empty) return true;  // no local agency: defensive bypass
    
    // _passengers is private in WOLF_CrewTransferScenario; access via Harmony
    // AccessTools.FieldRefAccess<WOLF_CrewTransferScenario, IDictionary<...>>("_passengers")
    // resolved at boot per §6 brittleness mitigation. Verify field type + access at impl.
    var passengers = WolfReflection.GetPassengers(__instance);
    if (passengers == null) return true;  // defensive — if reflection fails, fall through to stock
    
    foreach (var passenger in passengers) {
      if (string.IsNullOrEmpty(passenger.Name)) continue;
      
      var owning = ClientKerbalAgencyResolver.GetOwningAgencyForKerbal(passenger.Name);
      if (owning == null) continue;                          // unassigned: allow
      if (owning == Guid.Empty) continue;                    // sentinel: allow (spec §10 Q3)
      if (owning == AgencySystem.LocalAgencyId) continue;    // same agency: allow
      
      // Cross-agency kerbal — abort the Launch entirely.
      LunaScreenMsg.PostScreenMessage(
        $"Cannot fly cross-agency kerbal '{passenger.Name}' on this CrewRoute. The kerbal belongs to another agency.",
        5f,
        ScreenMessageStyle.UPPER_CENTER);
      return false;  // skip the Launch body — no Embark, no RemoveCrewmember
    }
    return true;  // all kerbals same-agency / unassigned / sentinel: allow stock Launch
  }
}
```

**Critical impl notes for the prefix:**

- **`LunaScreenMsg.PostScreenMessage(text, duration, ScreenMessageStyle)` is the real API.** `LunaScreenMsg.ShowMessage(...)` is NOT a method on the class — verify at impl by reading [LmpClient/LunaScreenMsg.cs](../../LmpClient/LunaScreenMsg.cs).
- **`_passengers` is a private field** on `WOLF_CrewTransferScenario` (verify at impl; field name + type may vary across WOLF versions). Use `AccessTools.FieldRefAccess<>` or reflective access; resolve at boot per §6 self-disable.
- **The prefix returns false on cross-agency**, which suppresses the stock Launch body entirely. If WOLF's Launch has side effects that need to fire on the no-op path (analytics, UI cleanup), the prefix would need to call them explicitly — verify at impl whether such side effects exist.
- **All-or-nothing scope:** the prefix aborts on the FIRST cross-agency passenger found. Same as the server-side router's batch-level drop. A future iteration could filter `_passengers` instead of aborting, but the locked semantic is "if any kerbal is cross-agency, the entire route attempt is rejected" — simpler UX, fewer edge cases.

**`ClientKerbalAgencyResolver` location + data source (pinned per consumer-lens MUST FIX #4):**

- **File:** `LmpClient/Systems/Agency/ClientKerbalAgencyResolver.cs` (new). Pure helper class in the `LmpClient.Systems.Agency` namespace, sibling to the existing `AgencyMembership.cs` (Stage 5.18a) and `AgencyLabelFormatter.cs` (Stage 5.18c).
- **Data source:** scans `FlightGlobals.Vessels` (KSP-side, full vessel list including loaded + unloaded + ProtoVessel-backed) for any vessel whose `GetVesselCrew()` (or `protoVessel.GetVesselCrew()` for unloaded vessels) contains a `ProtoCrewMember` with matching `name`. When found, the resolver looks up the vessel's agency via the Stage 5.18b client mirror: `AgencySystem.Singleton.VesselOwnership.TryGetValue(vessel.id, out var agencyId)`. Returns `Guid?` (null = kerbal not aboard any tracked vessel; the resolver does NOT fall back to scanning unsynced vessels).
- **Cost profile:** O(N vessels × M kerbals-per-vessel) per call. Realistic per-Launch invocation count is ~3-10 passengers; scan runs on the Unity main thread so cost matters. Cache: an `OnDestroy`-invalidated dict of `(kerbalName → vesselId)` indexed at vessel-load time would reduce scan cost; deferred to v5 Slice E impl if profiling shows the scan as a hotspot.
- **Mirror relationship to server-side `KerbalAgencyResolver` (§2.b.v):** the two helpers share intent (vessel-proxy authority for kerbal-to-agency resolution) but NOT implementation. Server-side scans `VesselStoreSystem.CurrentVessels` (server-authoritative vessel ConfigNode text); client-side scans `FlightGlobals.Vessels` (KSP-resident vessel objects). Both round-trip through `lmpOwningAgency` / `OwningAgencyId` for the agency lookup.

**Concurrent-race semantics — what happens when two clients try to embark the same kerbal:**

The single-player-per-agency invariant (spec §10) bounds the race surface: each agency has exactly one player, so the only way "two clients race for the same kerbal" can happen is **two DIFFERENT agencies' players each trying to embark a kerbal from a third party's vessel**. Trace:

1. Alice owns vessel `V_A` carrying Jeb. Bob (Agency B) and Carol (Agency C) are both in physics range.
2. Both Bob and Carol click Launch with Jeb selected, nearly simultaneously.
3. Both clients' prefixes resolve `Jeb → Alice's agency`. Both prefixes show toast + abort. **No race; both attempts fail at the client.** No wire traffic.
4. If Bob and/or Carol are modified clients that bypassed the prefix: both send `AgencyWolfCrewRouteStateMsgData`. Server's per-entry cross-agency check fires on each. Both are silently dropped. No wire echo, no state mutation. Warning logged for each.
5. Bob's and Carol's local views diverge from server-canonical until the next `VesselSync` reply restores their views. Acceptable per §8.f modified-client framing.

**No within-agency race** because each agency has exactly one player. If Stage 6 ever opens N-players-per-agency, the cross-agency check at the router still serializes against `AgencySystem.GetAgencyLock(agencyId)` (per §2.b mandatory router-body shape) — first-write-wins on the `WolfCrewRoutes[UniqueId]` dict key.

### 8.f Kerbal-sharing model under per-agency v1 — what stays shared, what gets restricted

**Load-bearing framing for implementers + reviewers.** The Phase 4 cross-agency CrewRoute kerbal authority gate is the narrowest possible restriction on a v1 design where kerbals are deliberately shared. This section makes that scope explicit so no implementer accidentally widens it.

**Per-spec invariant from spec §10 Q-Kerbal sign-off (documented in [KerbalSystem.cs:63-67](../../Server/System/KerbalSystem.cs#L63-L67)):** under per-agency v1 (Stage 5), the kerbal roster is **shared across agencies**. There is one canonical roster at `Universe/Kerbals/`. Every agency sees every kerbal. The Astronaut Complex pool is shared. Hiring, EVA-rescue completion, KIA, retirement — all operate against the single shared roster. Per-agency kerbal rosters are explicitly **Stage 6 work** and are NOT a Phase 4 prerequisite.

**What Phase 4 adds — and what it intentionally does NOT add:**

| Surface | Pre-Phase-4 v1 behavior | Phase 4 contribution | Stage 6 forward path |
|---|---|---|---|
| Astronaut Complex hire (any kerbal in pool) | Shared. Any agency can hire any unassigned kerbal. | **Unchanged.** No restriction. | Stage 6 partitions the pool per-agency. |
| Kerbal assignment to a new vessel at launch | Shared. Either agency can assign any unassigned kerbal. | **Unchanged.** No restriction. | Stage 6 restricts to per-agency kerbal pool. |
| `KerbalRemove` (despawn / kick) on a kerbal aboard a cross-agency vessel | K1 guard (5.17e-8) REJECTS. | **Unchanged.** K1 guard refactored to share `KerbalAgencyResolver` but behavior preserved. | Stage 6 enforces via kerbal-level stamps; K1 guard becomes redundant. |
| `KerbalRemove` on a kerbal in AC pool / not aboard any vessel | Allowed (any agency). | **Unchanged.** | Stage 6 restricts per-agency. |
| Stock KSP recovery — kerbal returns to AC after mission | Returns to shared pool. | **Unchanged.** | Stage 6 routes back to originating agency's pool. |
| Stock KSP KIA — kerbal dies on a vessel | Removed from shared roster. | **Unchanged.** | Stage 6 unchanged (death is roster-shared). |
| Stock KSP EVA — kerbal goes EVA from a vessel | EVA mini-vessel inherits source vessel's `lmpOwningAgency` per [VesselProto.cs](../../LmpClient/Systems/VesselProtoSys/VesselProto.cs) stamp logic. | **Unchanged.** EVA kerbal authority follows vessel-proxy. | Stage 6 unchanged (EVA derives from kerbal stamp directly). |
| WOLF `CreateCrewRoute` — Bob creates an empty route | No restriction (route has no Passengers yet at creation). | **No restriction** at create time. The check fires at Embark time. | Stage 6 may restrict at create time if route is bound to a specific kerbal pool. |
| WOLF Embark a kerbal **aboard own-agency vessel** onto a CrewRoute | No restriction. | **Allow.** Same-agency. | Stage 6 unchanged. |
| WOLF Embark a kerbal **aboard a cross-agency vessel** onto a CrewRoute | No restriction (the seizure vector). | **REJECT** (the Phase 4 restriction). Vessel-proxy authority: `KerbalAgencyResolver.GetOwningAgencyForKerbal(name)` returns the cross-agency Guid; check fails. | Stage 6 enforces via kerbal-level stamp; Phase 4 path becomes redundant. |
| WOLF Embark a kerbal **aboard pre-0.31 Unassigned-sentinel vessel** | No restriction. | **Allow** per spec §10 Q3. Any agency may interact with sentinel vessels. | Stage 6 inherits the same sentinel bypass at kerbal-level. |
| WOLF Embark a kerbal **not aboard any vessel** (AC pool, KIA-cleared, Missing) | No restriction. | **Allow.** `KerbalAgencyResolver` returns null; cross-agency check is dormant. (WOLF itself blocks the embark — kerbal must be on a nearby vessel for the UI to offer them.) | Stage 6 may restrict if kerbal-level stamp resolves to a different agency. |
| KSP-stock crew transfer (drag kerbal from pod to pod on the same vessel) | No restriction. | **Unchanged.** Same-vessel transfer doesn't change vessel ownership. | Stage 6 unchanged. |
| KSP-stock crew transfer (drag kerbal from your vessel to a cross-agency vessel that's docked) | Restricted indirectly via 5.17a write-path counterpart on the cross-agency vessel write. | **Unchanged.** The cross-agency vessel-write rejection at the relay layer (`Server/Message/VesselMsgReader.RejectIfCrossAgencyWrite`) blocks the cross-agency-vessel-resulting-from-the-transfer write. | Stage 6 enforces at kerbal-stamp level too. |
| Both agencies want to hire the same AC kerbal at the same time | First-write-wins via stock KSP serialization. | **Unchanged.** No new race surface. | Stage 6 partitions the pool. |
| Both agencies WOLF-fly the same kerbal at the same time | Cannot happen: the kerbal can only be aboard one vessel; whichever agency owns that vessel passes the cross-agency check; the other fails. | **Resolves cleanly via vessel-proxy.** | Stage 6 same outcome via kerbal-stamp. |

**One real edge case — kerbal mid-flight on WOLF CrewRoute (Open Question #5 in §9):** while a kerbal is `RosterStatus.Missing` mid-flight (per `WOLF_CrewTransferScenario.cs:588`), they are NOT aboard any vessel from `vessel.GetVesselCrew()`'s perspective. `KerbalAgencyResolver` returns null. A second cross-agency embark attempt would **pass** the cross-agency check (kerbal looks "unassigned" to the resolver). In practice WOLF's own Embark requires the kerbal to be on a nearby crewed vessel — and a `Missing` kerbal isn't — so the second Launch fails inside WOLF's `_passengers.Contains(kerbal.name)` check at `WOLF_CrewTransferScenario.cs:572-574` (a `Missing` kerbal never shows up in any nearby vessel's `GetVesselCrew()` enumeration). The race is structurally closed by WOLF's design constraint, but the cross-agency-check dormancy during the mid-flight window is worth pinning at Slice E impl to confirm WOLF can't produce a phantom CrewRoute entry that points to a kerbal who never embarks.

**Stage 6 forward-compatibility:** when kerbal-level agency stamps land, `KerbalAgencyResolver.GetOwningAgencyForKerbal(name)` becomes a one-line delegator to `AgencyKerbalRoster.GetAgencyForKerbal(name)`. The K1 guard, the WOLF cross-agency CrewRoute reject, and any future kerbal-authority surface all upgrade simultaneously without router/wire-surface changes. The vessel-proxy authority pattern is the **MVP gate**, not a permanent design.

**Why this matters for soak:** an operator running per-agency mode is going to notice that "I can still hire each other's kerbals from the AC" and "death affects both agencies' rosters". This is by design under v1 (per the K1 guard XML). The pre-Phase-4 v1 shared-roster product is intentional. Phase 4 closes the ONE WOLF-specific seizure vector without breaking the shared-roster product everywhere else. Document this clearly in the v4 release notes so soak feedback isn't "kerbals aren't per-agency!" complaints — it's a known Stage 5 v1 limitation with a documented Stage 6 forward path.

---

## 9. Open questions for s40 review

1. **Hopper recipe ingestion under per-agency**: Are hopper recipes pulled from a per-agency definitions store, or are they globally defined in mod config? Answer: globally (WOLF's `Recipe` definitions live in mod config nodes). Hoppers are per-agency only in the sense that "Alice's hopper uses recipe X" is per-agency state; the recipe definition itself is shared.

2. **Resource stream ownership on cross-agency depots at the same body+biome**: If Alice's depot and Bob's depot both exist at `(Duna, Lowlands)`, do they share resources? **Locked decision: NO.** They are two distinct depots, each per-agency. Bob's depot's `Hydrates` stream is independent of Alice's depot's `Hydrates` stream. Resource flows between agencies on the same body would require an explicit cross-agency-trade UI which is out of scope for Phase 4.

3. **WOLF UI on cross-agency physics-range observation**: Bob is in physics range of Alice's depot. Bob's WOLF UI shows Bob's own depots only (because the projector strips Alice's from Bob's view). But Bob's KSP map may render Alice's `WOLF_DepotModule` part. Soak verify this isn't a visible-but-non-interactable scenario (a "ghost" depot Bob can see but can't touch).

4. **Confirm `WOLF_CrewTransferScenario.Launch` is the correct prefix anchor for kerbal-embark.** §8.e's re-derivation showed it IS, but verify at impl that the prefix runs BEFORE the Launch's Embark+RemoveCrewmember storm, NOT inside it.

5. **Migration on `setvesselagency` when CrewRoute is mid-flight**: A vessel changes hands while a CrewRoute is in-flight with passengers from that vessel. Per §8.d, mid-flight kerbals are `RosterStatus.Missing` so the authority gate doesn't apply until Disembark. **Verify at impl** that the Disembark path correctly resolves the destination kerbal back to the new vessel agency's roster — KSP may or may not handle this transparently.

6. **Symmetric send-side caps on the 5 new MsgData types**: Per `[[feedback-wire-msgdata-chunking-caps]]`, each new MsgData needs `MaxEntryCount` + send-side cap-throw + catch-up chunking. Slice values: Depots/Routes ~20-50 typical; Hoppers/Terminals ~20-50 typical; CrewRoutes ~10-20 typical. Cap of 200 entries per send is operator-protective without normal-traffic friction.

7. **Confirm WOLF.csproj `<Compile>` list at impl time**: re-verify the 5 entity files + ScenarioPersister + WOLF_ScenarioModule are still in the compile list on the operator's installed WOLF version (the audit pinned at MKS SHA `ed0f6aa6`, but the operator's KSP install may have a different WOLF release). The boot-time `AccessTools.TypeByName` self-disable catches the version mismatch case.

---

## 10. Implementation slices (post-pre-spec)

| Slice | Scope | Approx commits |
|-------|-------|----------------|
| **A** | `AgencyState` 5 new dicts + entity classes (`AgencyWolfDepotEntry`, `AgencyWolfRouteEntry`, `AgencyWolfHopperEntry`, `AgencyWolfTerminalEntry`, `AgencyWolfCrewRouteEntry`, `AgencyWolfPassengerEntry`) + ConfigNode round-trip + `AgencyStateWolfRoundTripTest` (5 cases) + 5 wire MsgData stubs (no router wiring yet — just the wire shape) | 1 commit |
| **B** | `AgencyWolfDepotRouter` + matching MsgData fully wired + projector splice DEPOTS portion + tests (~10 cases) + boot diagnostic stub + `WarnAboutSharedWolfOnUpgrade` + hazard predicate wired into `RefuseStartupIfUpgradeHazardWithoutOverride` | 1 commit |
| **C** | `AgencyWolfRouteRouter` + projector splice ROUTES portion + tests (~10 cases) + Route postfixes (AddResource/RemoveResource/IncreasePayload) | 1 commit |
| **D** | `AgencyWolfHopperRouter` + `AgencyWolfTerminalRouter` (combined — both are simple Guid-keyed) + projector splice HOPPERS+TERMINALS portion + tests (~20 cases) + Remove* postfixes | 1 commit |
| **E** | `AgencyWolfCrewRouter` + `KerbalAgencyResolver` extraction + K1 refactor + cross-agency kerbal authority gate + WOLF_CrewTransferScenario_LaunchPrefix + projector splice CREWROUTES portion + tests (~14 + 8 + 4 cases) + MockClientTest e2e (~6 cases) | 1 commit |
| **F** | ForkBuildInfo `"WOLF-R4"` entry + CLAUDE.md update + admin help text for `setvesselagency` documenting Phase 4 no-migration + integration soak | 1 commit |
| **G** (optional, deferred to Phase 4.5) | Sub-slice if E bloats: separate kerbal authority gate from the basic CrewRoute routing | 1 commit if needed |

**Sub-slice candidate** per `[[feedback-subslice-long-session]]`: Slice E is the most complex. If session work shows it bloating past ~3 weeks for that one slice, propose E-1 (router + persistence + standard cases) and E-2 (cross-agency kerbal authority + prefix + e2e + soak) via AskUserQuestion mid-implementation.

---

## 11. Acceptance criteria

Mirror of Phase 3 §10 acceptance.

**Two-agency two-client soak (gate=on, Career):**

1. Alice's depots / routes / hoppers / terminals / crew routes are invisible to Bob in Bob's WOLF UI (Planning Monitor, Route Monitor, Scenario Monitor). Reciprocal.
2. Both agencies can establish independent depots at the same `(Body, Biome)` without conflict.
3. Reciprocal CrewRoute creation: Alice runs A's kerbal on A's CrewRoute → success. Bob runs B's kerbal on B's CrewRoute → success.
4. Cross-agency CrewRoute attempt: Bob tries to fly Alice's kerbal → UI shows toast "Cannot fly cross-agency kerbal", server logs Warning, no state mutation on either side.
5. Reconnect: Alice disconnects mid-WOLF-session (e.g. 1 depot + 1 route + 1 mid-flight crew route). Reconnects. Receives full WOLF state catch-up before any new mutations.
6. Modified client (Bob bypasses the client-side prefix and sends a cross-agency CrewRoute): server router rejects + logs Warning. Bob's WOLF state shows no new CrewRoute (silent drop).

**Two-agency two-client soak (gate=off, shared):**

7. Bit-identical pre-Phase-4 baseline. Alice's depots visible to Bob; both can edit; the existing 30s SHA pass propagates state. Pre-existing cross-agency kerbal seizure remains possible (documented out-of-scope hazard). No regression on the existing MKS-multiplayer shared product.

**Upgrade-in-place (pre-0.31 universe with existing WOLF state):**

8. Server boots with `PerAgencyCareer=true` + `AllowEnablePerAgencyOnExistingUniverse=false` (default). Warns about existing WOLF state. Refuses startup. Operator sets `AllowEnablePerAgencyOnExistingUniverse=true`. Server starts; first per-agency connect strips shared WOLF state from projected view; operator-acknowledged data loss.

**Mod/WOLF version mismatch:**

9. Server boots with `PerAgencyCareer=true` but WOLF.dll not installed. `AccessTools.TypeByName("WOLF.ScenarioPersister")` returns null. Boot log emits `[fix:WOLF-R4] WOLF types not found; per-agency WOLF routing disabled`. Server starts cleanly. Other Phase 3 routing (Kolony/Planetary/Orbital) and band-1 routing (Currency/Tech/etc.) work normally.

---

## 12. v4 → v5 upgrade compatibility

Consolidated upgrade-compatibility reference. Phase 4 ships in v5; v4 ships the proto-guard prerequisite (see [v4-vessel-proto-cross-agency-write-guard.md](v4-vessel-proto-cross-agency-write-guard.md)) without Phase 4 itself. Operators upgrading from v4 to v5 fall into one of four scenarios; each is documented here so the upgrade pathway has a single source of truth (vs. scattered references in §4.d / §8.f / §11).

### 12.a Scenario A — operator never enabled per-agency mode in v4

`PerAgencyCareer=false` throughout v4. WOLF runs via legacy 30s SHA pass on the shared `WOLF_ScenarioModule`. All players share one WOLF graph.

**v4 → v5 upgrade behavior:**

- Server-binary swap: clean. v5 reads v4 `AgencyState.txt` files (no WOLF child nodes) with empty WOLF dicts initialized at field declaration (verified at [AgencyState.cs:980-1024](../../Server/System/Agency/AgencyState.cs#L980-L1024) — same forward-compat pattern Phase 3 already uses).
- No `WarnAboutSharedWolfOnUpgrade` boot diagnostic fires (the predicate only triggers when `PerAgencyCareer=true` + shared WOLF state exists).
- v5 runs identically to v4 for these operators. Phase 4 is dormant under gate=off.

**Recommended operator action:** none. Upgrade is silent.

### 12.b Scenario B — operator enabled per-agency mode in v4, no WOLF mod installed

`PerAgencyCareer=true` in v4. WOLF mod not installed (or removed). Per-agency Stage 5 features work; WOLF doesn't apply.

**v4 → v5 upgrade behavior:**

- Server-binary swap: clean.
- v5 boot-time `AccessTools.TypeByName("WOLF.ScenarioPersister")` returns null. Phase 4 self-disables (per §6 mitigation). Boot log emits `[fix:WOLF-R4] WOLF types not found; per-agency WOLF routing disabled`.
- No `WarnAboutSharedWolfOnUpgrade` fires (no shared `WOLF_ScenarioModule` scenario exists — the scenario only gets created when WOLF is installed).
- Other per-agency features (5.17a / 5.17c / 5.17d / Phase 3 routers) run unchanged.

**Recommended operator action:** none. Phase 4's WOLF code path is inert until WOLF gets installed.

### 12.c Scenario C — operator enabled per-agency mode in v4 with WOLF installed but no accumulated state

`PerAgencyCareer=true` in v4. WOLF mod installed but no depots / routes / hoppers / terminals / crew routes built yet (or all cleaned via the legacy in-game UI before upgrade).

**v4 → v5 upgrade behavior:**

- Server-binary swap: clean.
- `WarnAboutSharedWolfOnUpgrade` predicate checks `ScenarioStoreSystem.CurrentScenarios["WOLF_ScenarioModule"]` for non-empty `CREWROUTES`/`DEPOTS`/`HOPPERS`/`ROUTES`/`TERMINALS` child nodes. Empty → predicate false → no boot warning, no refuse-startup gate.
- v5 starts cleanly. First WOLF mutation post-upgrade routes through `AgencyWolfDepotRouter` / etc. into the appropriate per-agency dict. WOLF state grows from empty.

**Recommended operator action:** none. Equivalent to a fresh-start universe from the WOLF side.

### 12.d Scenario D — operator enabled per-agency mode in v4 with accumulated WOLF state (the operator-pain case)

`PerAgencyCareer=true` in v4. WOLF mod installed. Players have accumulated WOLF state over the v4 session (e.g. months of MKS logistics graph). This state lives in the SHARED `WOLF_ScenarioModule.txt` because v4 doesn't have Phase 4 routing — the 30s SHA pass is the only WOLF persistence path under v4.

**v4 → v5 upgrade behavior:**

- Server-binary swap: file-side clean (v5 reads v4 `AgencyState.txt` files cleanly — empty WOLF dicts).
- `WarnAboutSharedWolfOnUpgrade` fires. Boot log emits the strengthened WARN text per §4.d (now covering the mid-flight CrewRoute kerbal-stranding hazard explicitly).
- `RefuseStartupIfUpgradeHazardWithoutOverride` refuses startup. Server doesn't accept connections.
- Operator must choose:
  - **Option (1):** Pre-upgrade preparation. Ensure no CrewRoutes mid-flight. Then either reset shared WOLF state via hand-editing `Universe/Scenarios/WOLF_ScenarioModule.txt`, or accept full WOLF state loss.
  - **Option (2):** Manually re-create the WOLF graph on each agency's vessels after upgrade.
  - **Option (3):** Hand-edit `Universe/Agencies/{guid}.txt` to migrate WOLF state to specific agencies (no bulk tooling, per §5 out-of-scope).
  - **Option (4):** Stay on shared-agency mode (`PerAgencyCareer=false`) — no data loss; WOLF continues as shared graph.
- If operator sets `AllowEnablePerAgencyOnExistingUniverse=true` and proceeds: server starts; first per-agency client connect strips WOLF child nodes from outgoing scenario blob; player-visible WOLF state evaporates to empty.

**Recommended operator action:** strongly consider Option (1) or (3) before flipping the override. Option (4) is the zero-risk path for operators who don't need WOLF per-agency.

### 12.e Wire-protocol compatibility (all scenarios)

**v5 bumps protocol to 0.32.0** (mandatory per the verified [LmpVersioning.cs:59-76](../../LmpCommon/LmpVersioning.cs#L59-L76) cross-compat check). Without the bump, mixed-cohort produces asymmetric silent-drop hazards:

- **v5 client → v4 server:** v5's `WolfDepotState=9` etc. messages dispatch through `MessageBase.GetMessageData(subtype=9)` on the v4 server; v4's `AgencyCliMsg.SubTypeDictionary` lacks the slot; exception thrown; receiver silently drops. v5 client sees Lidgren ack, persists nothing on server. Soak-grade desync.
- **v4 client → v5 server:** v4 doesn't emit WOLF messages, no interaction.

The bump structurally prevents mixed-cohort connection — clients of one version refuse handshake with servers of the other. This is the established practice since the `0.30.0` bump (BUG-005/006, commit `d64acf66`).

**Effect on rolling upgrade:** none — single-server operators stop the server, swap binaries, restart. Clients upgrade on next connect attempt (the handshake-version-mismatch refusal forces the client owner to update their LMP install).

### 12.f Persistence compatibility — ConfigNode round-trip

- **v5 reading v4 `AgencyState.txt`:** safe. Missing WOLF child nodes load as empty dicts (verified at [AgencyState.cs:980-1024](../../Server/System/Agency/AgencyState.cs#L980-L1024) precedent for Phase 3's KOLONY_ENTRIES / PLANETARY_ENTRIES / ORBITAL_TRANSFERS).
- **v5 saving back over a v4 file:** safe. v5 emits WOLF child nodes only when the corresponding dict is non-empty; first-save-after-upgrade produces a file with no WOLF nodes (matching v4 shape) until WOLF mutations occur.
- **v4 reading a v5-touched `AgencyState.txt` (DOWNGRADE):** lossy. v4's `FromConfigNode` doesn't know the WOLF child node names; unknown nodes are silently dropped from the in-memory state. v4's next save back to disk would lose the WOLF nodes. **Downgrade is unsupported** — standard fork policy (no downgrade after a session on the newer binary).
- **Vessel ConfigNodes (`Universe/Vessels/*.txt`):** unchanged across v4/v5. Phase 4 doesn't modify vessel proto shape. WOLF PartModule data round-trips identically.
- **Shared scenarios (`Universe/Scenarios/*.txt`):** unchanged on disk. The gate-conditional `IgnoredScenarios` filter prevents the 30s SHA broadcast under gate=on but doesn't touch the on-disk file.

### 12.g v4 → v5 forward-only constraint summary

| Direction | Files | Wire | State preservation |
|---|---|---|---|
| v4 → v5 (upgrade) | safe; v5 reads v4 cleanly | requires protocol bump | Scenario A/B/C: full preservation. Scenario D: operator-managed loss via boot WARN + refuse-startup gate. |
| v5 → v4 (downgrade) | **lossy**; v4 silently drops WOLF nodes from `AgencyState.txt` | requires protocol bump (forces split cohort) | Operators MUST NOT downgrade after a v5 session that touched WOLF state. Pre-upgrade backups (`Universe/` snapshot) are the recovery path. |

### 12.h Acceptance criteria additions for v5 release soak

Mirror Phase 3's release-soak pattern:

1. **v5 boots cleanly against a v4 fresh-start universe** (Scenario A or C with empty WOLF state). No diagnostic noise.
2. **v5 boots cleanly against a v4 shared-agency universe** (Scenario A). Phase 4 stays dormant.
3. **v5 emits `WarnAboutSharedWolfOnUpgrade` against a v4 per-agency universe with accumulated WOLF state** (Scenario D). Refuses startup. Sets `AllowEnablePerAgencyOnExistingUniverse=true`, restarts, accepts the override.
4. **v5 self-disables WOLF routing on boot when WOLF mod is absent** (Scenario B). No noise, no false positives.
5. **Mixed-cohort handshake refusal** between v4 and v5 clients/servers. Both directions confirm the protocol version mismatch is enforced.

These join the existing §11 acceptance set.

---

## 13. Cross-links

- [mks-lmp-compatibility-handoff.md](mks-lmp-compatibility-handoff.md) §6 R4 + §7 Phase 4 + §10 item 10 — source-of-truth
- [mks-lmp-compatibility-phase-3-prespec.md](mks-lmp-compatibility-phase-3-prespec.md) — structural template
- [Server/System/Agency/AgencyKolonyRouter.cs](../../Server/System/Agency/AgencyKolonyRouter.cs) — Phase 3 Slice B router template
- [Server/System/KerbalSystem.cs:103-136](../../Server/System/KerbalSystem.cs#L103-L136) — K1 grief guard, direct template for the cross-agency CrewRoute kerbal gate
- [LmpCommon/Message/Types/AgencyMessageType.cs](../../LmpCommon/Message/Types/AgencyMessageType.cs) — wire enum, append slots 9-13
- [LmpCommon/Message/Server/AgencySrvMsg.cs](../../LmpCommon/Message/Server/AgencySrvMsg.cs) + [LmpCommon/Message/Client/AgencyCliMsg.cs](../../LmpCommon/Message/Client/AgencyCliMsg.cs) — SubTypeDictionary append, BUG-010 wire-symmetry rule
- [Server/Message/AgencyMsgReader.cs](../../Server/Message/AgencyMsgReader.cs) — dispatch switch append
- [Server/System/Agency/AgencyState.cs](../../Server/System/Agency/AgencyState.cs) — append 5 new dicts, mirror KolonyEntries/PlanetaryEntries/OrbitalTransfers shape
- [F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/ScenarioPersister.cs) — WOLF persister, 5 entity lists + 7 Create/Remove methods
- [F:/tmp/mks-external/MKS/Source/WOLF/WOLF/CrewRoute.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/CrewRoute.cs) — CrewRoute Embark/Disembark/Launch surface
- [F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Modules/WOLF_CrewTransferScenario.cs](file:///F:/tmp/mks-external/MKS/Source/WOLF/WOLF/Modules/WOLF_CrewTransferScenario.cs) — UI handler, Launch() at line 557 is the prefix anchor
- [[project-wolf-phase-4-pickup]] — session-38 user-confirmed scope
- [[project-per-agency-pickup]] — Stage 5 tracker
- [[project-mks-merge-to-per-agency]] — historical merge to feature/per-agency
- [[reference-agency-wire-extension]] — 7-step recipe for the 5 new MsgData slots
- [[feedback-audit-via-prespec]] — re-walk source before pre-spec authoring (this pre-spec exists because of this discipline)
- [[feedback-research-first]] — cite file:line; subagent summaries are leads not conclusions
- [[feedback-review-lens-framing]] — parallel consumer + upgrade lens reviews (MANDATORY for pre-spec)
- [[feedback-integration-logic-review]] — 4th lens, end-to-end flow trace
- [[feedback-wire-msgdata-chunking-caps]] — symmetric send-side + catchup chunking
- [[feedback-negative-assertions-lock-in-bugs]] — single try/catch per-entry shape
- [[stack-notes-csproj-compile-list-orphan-check]] — verified WOLF_ScenarioModule IS in compiled DLL

---

_End of Phase 4 pre-spec. Next: 4-lens review (general / consumer / upgrade / integration-logic) → apply findings → final → Slice A implementation._
