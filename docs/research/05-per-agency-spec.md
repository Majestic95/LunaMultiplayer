# Per-Agency Career Spec — Stage 5

**Status:** Spec — not yet implemented. Branch `feature/per-agency` not yet created (Stage 5.12 will create it).

**Owner:** Majestic95 fork. Ground-up implementation, not derived from `PlagueNZ/LunaMultiplayer-SplitProgression` (benchmark only — see Stage 5.18).

**Target protocol version:** 0.31.0 (bumped from 0.30.0). Per-agency mode is incompatible with 0.30.x peers; the version matrix in `LmpCommon/LmpVersioning.cs` rejects mixed connections.

---

## 1. Goal

Replace LMP's shared-agency career model with per-player agency career, opt-in via server setting. Each connected player operates as an independent KSP space agency with its own funds, science, reputation, tech tree, KSC facility upgrade levels, Kerbal roster, contract offerings, and vessel ownership. Other agencies' vessels remain visible in the tracking station, labelled with the owning agency.

The fork ships **both** modes from one binary. `GameplaySettings.PerAgencyCareer` toggles. Shared-agency mode is the default (preserves current behavior); per-agency is opt-in. A single server runs one mode at a time — no per-player toggle.

---

## 2. Architecture decisions

Decisions confirmed by Majestic95 (2026-05-16):

| Decision | Choice | Rationale |
|---|---|---|
| Agency-player binding | **1 player = 1 agency** | Matches `Dictionary<player, AgencyState>` in Stage 5.14. Simplest mental model. Multi-player teams deferred to a future stage on top of `GroupSystem`. |
| Tech tree | **Per-agency independent** | Each agency runs its own R&D tree. Standard career progression, N parallel copies. Matches PlagueNZ benchmark. Researching is meaningful progression. |
| KSC facilities | **Per-agency upgrade levels, shared physical KSC** | One visual KSC; building tiers swap based on active agency. Avoids the absurdity of N coincident KSCs. |
| Vessel visibility | **All visible, agency-labelled** | Tracking station shows every agency's vessels with `OwningAgency` tag. Focus permitted; Control lock acquisition rejected if requester's agency != vessel's agency. Preserves the "we share a sky" multiplayer feel. |

Defensible defaults for secondary decisions (flag if you disagree before coding starts):

| Decision | Default | Rationale |
|---|---|---|
| Funds / Science / Reputation pools | Per-agency, independent. Server-configurable starting amounts (default = stock career starting values). | Follows from per-agency tech tree. |
| Contracts | **Hybrid: shared offered pool + per-agency Active/Completed/Declined.** `ContractSystem.Instance.Contracts[State.Offered]` and the `ContractPreLoader` ScenarioModule stay global — CC's pre-loader cache sees the world it expects. Per-agency state holds the contract once accepted: `Active`, `Completed`, `Declined` and reward routing keyed by `(AgencyId, contractGuid)`. Stage 5.17b also commits to: (a) **do not persist Offered-state per-agency** (PlagueNZ saw 1,727-entry bloat); (b) **per-contract exception isolation** in the restore loop (one broken CC contract throwing `Register()` must not abort the whole load). | Empirically proven by PlagueNZ's painful retreat from full-isolation. CC source confirms five distinct fight surfaces with per-agency contract pools (`ContractPreLoader` ScenarioModule, `onContractsLoaded` event re-fire, `ContractDisabler.SetContractState` global Withdraw, hand-rolled `Activator.CreateInstance + Load`, null-unsafe `HomeWorld()`). The hybrid sidesteps all five. Decision audit-driven, signed off 2026-05-17 (§10 Q6). |
| Kerbal roster | Per-agency. Each agency starts with 4 auto-generated "original" Kerbals (deterministic names seeded by agency name). | Avoids N coincident Jebs. Server config can opt into a "shared originals" mode if desired. |
| Strategies (Mission Control) | Per-agency. | Standard career system; each agency runs its own. |
| World firsts / milestones | Per-agency tracking. Server can emit an optional global feed of `"<agency> achieved <milestone>"` chat-style announcements. | Preserves multiplayer feel without conflating progression. |
| CommNet | Shared infrastructure (MVP). All ground stations and relays available to all agencies. Per-agency CommNet deferred to post-Stage-5. | Building per-agency CommNet would massively expand scope; not worth it in v1. |
| Vessel ownership on dock | Initiator's agency wins the merged vessel (same model as current `HandleVesselCouple` AuthoritativeSubspaceId behavior). | Consistent with existing dock semantics; no new edge cases. |
| Vessel ownership on undock | Each child vessel inherits the merged vessel's agency. Voluntary transfer UI deferred. | Simplest. If P2's craft was docked to P1's station and they undock, P2's craft now belongs to whichever agency owned the merged vessel — they can re-claim via voluntary transfer (post-v1). |
| Save migration | Fresh-start only. Per-agency mode requires a clean universe. No auto-migration of shared-agency saves. | Migration would need a UI to assign existing vessels/funds/science to agencies; not worth the complexity for v1. Operator workflow: archive the shared universe, start fresh with per-agency enabled. |
| Agency name | Default `"{PlayerName} Space Agency"`, editable via UI on first connect. Stored on the server `AgencyState`. | Lets agencies have flavor without forcing a naming dialog. |
| Inactivity / disconnected agencies | Agency persists indefinitely on the server. Vessels keep their `OwningAgency`. Reconnecting player resumes their agency. Server config `AgencyInactivityDays` for automatic cleanup (default = 0 = never). | Avoids accidental data loss; cleanup is opt-in. |

---

## 3. Data model

### Server-side

New file: `Server/System/Agency/AgencyState.cs`

Fields per agency:

- `AgencyId` — stable GUID. Persisted across player sessions. Indexed by `PlayerName` in `AgencySystem` for lookup, but the GUID is the canonical key.
- `OwningPlayerName` — current owner. Editable via admin command if the player changes their name.
- `DisplayName` — flavor name. Defaults to `"{PlayerName} Space Agency"`.
- `Funds`, `Science`, `Reputation` — `double`. Per-agency pools.
- `TechTree` — `Dictionary<string, TechNodeState>`. Each entry tracks unlock state + per-part purchase state.
- `KerbalRoster` — `Dictionary<string, ProtoCrewMember>` (serialized via existing ConfigNode path).
- `FacilityLevels` — `Dictionary<SpaceCenterFacility, int>`. Building upgrade tiers (launchpad, R&D, MC, AC, runway, tracking station, admin building).
- `Contracts` — `List<ContractState>`. Active + offered contracts. Completed/declined contracts archived separately for stats.
- `Strategies` — `List<StrategyState>`. Active strategies.
- `WorldFirsts` — `HashSet<string>`. Milestone IDs achieved by this agency.
- `Achievements` — `Dictionary<string, AchievementState>`. Stock achievement tracking.

Persistence: one file per agency under `Universe/Agencies/<AgencyId>.txt` in ConfigNode format, where `<AgencyId>` is the canonical GUID (no player-name in path — survives renames; PlagueNZ had to ship `MigrateLegacyFile` to recover from a player-name keyed scheme, we don't repeat that). Flushed by `BackupSystem.RunBackup()` like other universe state. Archived by `RunArchiveBackup()`.

**Atomic write requirement:** AgencyState is the player's career — a half-written file on power-loss = lost progression. `FileHandler.WriteToFile` is `FileMode.Create` direct; Stage 5.14c adds `FileHandler.WriteAtomic(path, bytes)` that writes to `<path>.tmp`, renames `<path>` → `<path>.bak` (single-generation rotation), then renames `<path>.tmp` → `<path>`. AgencyState writes go through this path; on load, if `<path>` is missing or unparseable, fall back to `<path>.bak`. Operator-readable inspection helper is the `listagencies` admin command (already in §5) printing `{AgencyId} → {DisplayName} (owned by {PlayerName})`.

New file: `Server/System/Agency/AgencySystem.cs`

- `ConcurrentDictionary<Guid, AgencyState> Agencies` — server-wide registry.
- `ConcurrentDictionary<string, Guid> AgencyByPlayerName` — convenience index.
- Lifecycle:
  - `RegisterAgency(playerName)` on first connect — creates `AgencyState`, persists to disk.
  - `LoadAgency(agencyId)` on subsequent connects.
  - `SaveAgency(agencyId)` periodically + on disconnect.
  - `CleanupInactive(daysThreshold)` — admin-triggered or scheduled.

### Vessel ownership

New top-level field on the vessel ConfigNode: `lmpOwningAgency = <AgencyId GUID>`.

Follows the `lmp*` prefix convention established by `lmpAuthSubspace` — KSP's vessel loader ignores unknown top-level fields, so the addition round-trips through any KSP-side persistence path.

Stored via the existing `MixedCollection<string, string> Fields` on `Server/System/Vessel/Classes/Vessel.cs`. Setter is the launch path (vessel creation) and the dock/couple path. Reader is `LockSystem.AcquireLock` (gating cross-agency Control lock acquisition) and the tracking-station UI (label rendering).

### Client-side

`LmpClient/Systems/Agency/AgencySystem.cs` mirrors the server registry. The local player's `AgencyState` is the active one — read by Harmony patches over `Funding.Instance`, `ResearchAndDevelopment.Instance`, etc. Other agencies' states are cached for tracking-station display; their `Funds`/`Science` aren't shown (privacy default), but `DisplayName` + vessel count are visible.

---

## 4. Wire protocol

Protocol version: **0.31.0**. Add to `LmpCommon/LmpVersioning.cs`. Per-agency 0.31.0 servers reject 0.30.x clients; shared-agency 0.30.x is unchanged.

### New messages

Under `LmpCommon/Message/Data/Agency/`:

| Message | Direction | Purpose |
|---|---|---|
| `AgencyHandshakeMsgData` | server → client (on connect) | Server informs client of all known agencies (id + display name + owning player) and the client's assigned agency. |
| `AgencyCreateRequestMsgData` | client → server | First-connect: client requests agency creation with desired DisplayName. |
| `AgencyCreateReplyMsgData` | server → client | Confirms creation, returns `AgencyId`. |
| `AgencyStateMsgData` | server → client | Full or partial state for one agency. Sent on connect (full) and on mutation (delta). |
| `AgencyFundsMutateMsgData` | bidirectional | Client requests a mutation, server validates and broadcasts the new value. |
| `AgencyScienceMutateMsgData` | bidirectional | Same shape for Science. |
| `AgencyReputationMutateMsgData` | bidirectional | Same shape for Reputation. |
| `AgencyTechUnlockMsgData` | bidirectional | Client requests a tech node unlock or part purchase; server validates funds/science and applies. |
| `AgencyFacilityUpgradeMsgData` | bidirectional | Building upgrade request + validation. |
| `AgencyKerbalUpdateMsgData` | bidirectional | Roster change (hire, fire, status change). |
| `AgencyContractMsgData` | bidirectional | Contract offer/accept/decline/complete events scoped to one agency. |
| `AgencyStrategyMsgData` | bidirectional | Strategy activation/deactivation. |
| `AgencyVisibilityMsgData` | server → client | Lightweight: list of `(VesselId, OwningAgencyId)` pairs for tracking-station rendering. |

### Modified messages

- `VesselProtoMsgData` — extend to include `OwningAgencyId` (16 bytes, GUID). Backward-incompatible — covered by the protocol bump.
- `LockMsgData.LockAcquireResponse` — extend with rejection reason `CrossAgencyDenied` alongside existing `CrossSubspaceDenied`.
- `SettingsMsgData` — add `PerAgencyCareerEnabled` flag so client knows which mode to render.

### Removed / replaced messages

The 13 existing `Share*` message types in shared-agency mode (`ShareFundsMsgData`, `ShareScienceMsgData`, `ShareReputationMsgData`, etc.) are **kept** but routed differently:

- **Shared-agency mode (PerAgencyCareerEnabled = false):** unchanged — broadcast to all clients as today.
- **Per-agency mode (PerAgencyCareerEnabled = true):** server still receives them, but applies the mutation to the **sending player's** `AgencyState` only and echoes back only to that player. Other clients are not informed.

This means `Share*Sender` on the client doesn't need to change in v1 — the routing is server-side. Phase-2 cleanup can replace the `Share*` family with `AgencyMutate*` messages once the system is proven, but the v1 path is minimum-invasive.

---

## 5. Server-side implementation

### Career-data projection strategy (Hybrid — signed off §10 Q5)

Per-agency funds/science/reputation/tech/contracts/strategies/facilities/kerbals reach the right client via **two layers, not one**:

1. **Server-side scenario projection at handshake + scene-load.** `Server/System/ScenarioSystem.cs` is extended so `SendScenarioModules` calls a new `Server/System/Agency/AgencyScenarioProjector.GetScenarioForPlayer(playerName, scenarioName)` per scenario per client. The projector substitutes the relevant `Funding`/`ResearchAndDevelopment`/`Reputation`/`ResearchAndDevelopmentParts`/`ContractSystem[Active|Completed]`/`StrategySystem`/`ScenarioUpgradeableFacilities`/`KerbalRoster`/`ScenarioAchievements`/`ScenarioDestructibles` ConfigNode blobs with the **sending client's agency** projection on top of the shared scenario template. The client's KSP singletons load up with correct per-agency state without knowing other agencies exist. **This is the read path** — covers ~80% of the call-site count (the ~83 `Funding.Instance` reference sites CLAUDE.md notes) for free.
2. **Client-side Harmony patches on high-traffic write paths only.** Mid-session mutations (`Funding.Instance.AddFunds`, `ResearchAndDevelopment.Instance.AddScience`, `Reputation.Instance.AddReputation`, tech-unlock writers, facility-upgrade writers, contract-state writers) route through Harmony postfixes that fire `AgencyMutate*MsgData` to the server. The server validates, applies to that agency's `AgencyState`, and broadcasts the new state delta back. Reads are NOT patched — they hit the singleton populated by step 1. **This is the write path** — much smaller surface than the spec's original Harmony-only design (~5-10 write methods total vs. ~83+ read sites).

**Why hybrid, not projection-only:** projection covers handshake + scene-load. Mid-session writes between scenes (e.g. recovering a vessel mid-flight credits funds) would diverge silently if there's no write-path interception. PlagueNZ ships projection-only and gets away with it because the existing `Share*` family relays writes — but their relay is "broadcast to all," which we have to scope to "this agency's owner only." That scoping IS the write-path Harmony layer.

**Why hybrid, not Harmony-only:** the original spec's ~83 read-site count is the per-`Funding.Instance` reference count — multiplied by `R&D.Instance` + `Reputation.Instance` + tech + facilities + kerbal-roster + contracts + strategies, the read-side patch surface easily exceeds 250 sites. Projection collapses every one of those reads to a singleton already loaded with the right per-agency value. The remaining write-path patches are small and high-value.

Audit-derived risk: CC's `ContractDisabler.SetContractState` calls `Withdraw()` on contracts globally across `ContractSystem.Instance.Contracts`. If our projection partitions `Contracts[Active|Completed]` per-agency on the client, a CC-driven type-disable could withdraw agency A's in-flight contracts when it should only touch agency B's. Hybrid + §2 contracts decision (shared offered pool) keeps the surface CC touches global; per-agency divergence only kicks in after `Contract.Accept` where CC's loops no longer reach.

### New systems

1. **`Server/System/Agency/AgencySystem.cs`** — registry, lifecycle, persistence.
2. **`Server/System/Agency/AgencyValidator.cs`** — server-side validation of mutation requests. Funds requests check non-negative result; tech unlocks check prerequisites; facility upgrades check both funds and tier-1-before-tier-2 ordering.
3. **`Server/System/Agency/AgencyScenarioProjector.cs`** — per-player scenario projection (the read-path layer). Produces a personalised `ConfigNode` for each of the ~10 career scenarios at handshake + scene-load. Called by `ScenarioSystem.SendScenarioModules`.
4. **`Server/System/Agency/AgencyContractRouter.cs`** — hybrid contract architecture (§2 Contracts). Routes shared offered-pool generation through the existing CC-friendly path; routes Active/Completed/Declined transitions per-agency. Also enforces "do not persist Offered-state per-agency" + per-contract exception isolation in the restore loop.

### Modified systems

1. **`Server/System/Share*System.cs` (13 files)** — each gets a `PerAgencyCareer` branch: when true, apply the mutation to the sender's `AgencyState` (not the shared scenario) and echo back to the sender only. Touch points roughly identical in each file (a single condition guarding the broadcast call + a delegate to `AgencySystem.ApplyMutation`). This is the "wide-but-shallow" work CLAUDE.md flags. **Pairs with the §6 client-side Harmony write-path layer** — `Share*Sender` on the client doesn't need to change because the routing decision is server-side. (Per the projection strategy above, the read path is handled by `AgencyScenarioProjector` at handshake/scene-load; `Share*` is purely the write path.)
2. **`Server/System/LockSystem.cs`** — `AcquireLock` for `Control`/`Update`/`UnloadedUpdate` now ALSO checks `requesterAgency == vessel.OwningAgency`. Reject with `CrossAgencyDenied` if mismatch. (This stacks on the existing `CrossSubspaceDenied` check.)
3. **`Server/System/Vessel/Classes/Vessel.cs`** — add `OwningAgencyId` accessor backed by `Fields["lmpOwningAgency"]`.
4. **`Server/Message/Vessel/VesselProtoMsgReader.cs`** — read/write the new field.
5. **`Server/Settings/Definition/GameplaySettings.cs`** — add `PerAgencyCareer` bool (default false), `DefaultStartingFunds`, `DefaultStartingScience`, `DefaultStartingReputation`, `AgencyInactivityDays` (default 0).
6. **`Server/System/FileHandler.cs`** — add `WriteAtomic(path, byte[] data, int numBytes)` per §3. Existing `WriteToFile` keeps its semantics; AgencyState uses the new atomic path.
7. **`Server/System/ScenarioSystem.cs`** — `SendScenarioModules` calls `AgencyScenarioProjector.GetScenarioForPlayer` per scenario per client when `PerAgencyCareer=true`. Shared-agency path unchanged.
8. **`Server/Web/Handlers/`** — extend `/` dashboard payload to include agency counts; add `/agencies` endpoint listing all `AgencyState`s.
9. **`Server/Client/ClientConnectionHandler.cs`** — on `ConnectClient`, call `AgencySystem.RegisterOrLoadAgency(playerName)`. On `DisconnectClient`, persist agency state.
10. **`Server/ForkBuildInfo.cs`** — append `"Per-agency career (Stage 5)"` to `ActiveFixes[]`.

### Admin commands

Under `Server/Command/Command/`, add:

- `listagencies` — print all agencies + their owning players + funds/sci/rep.
- `setagencyfunds <agency> <amount>` — admin override.
- `setagencyscience <agency> <amount>`
- `setagencyreputation <agency> <amount>`
- `transferagency <fromPlayer> <toPlayer>` — move an agency to a different player (for renames or operator transfers).
- `deleteagency <agencyId>` — destructive; requires `--confirm`. Reassigns owned vessels to a sentinel "Abandoned" agency.

Extend existing commands:

- `setfunds`, `setscience` — gain optional `--agency <id>` flag. Without the flag in per-agency mode they error out with "agency required."

---

## 6. Client-side implementation

This is where Stage 5.18b ("Harmony write-path interception layer") lives. **Narrow-and-focused** — the read side is handled server-side by `AgencyScenarioProjector` (§5), so client-side Harmony only targets the **write methods** that mutate KSP singletons mid-session.

### Harmony patch surface (writes only)

For each KSP singleton accessed by LMP, intercept the **write methods** that fire when a player spends/earns funds, unlocks tech, etc. Route to the server via `AgencyMutate*MsgData`. **Read sites are not patched** — they hit the singleton populated by handshake/scene-load projection (§5).

| Singleton write method | Approx call sites | Harmony pattern |
|---|---|---|
| `Funding.Instance.AddFunds(amount, reason)` | ~5 (recover-vessel, contract-complete, strategy-tick, admin-grant, advance) | Postfix: server-confirm via `AgencyFundsMutateMsgData`. Local update happens via the existing `OnFundsChanged` event after server echo. |
| `Funding.Instance.set_Funds` (direct setter, rare — admin paths only) | ~2 | Same shape. |
| `ResearchAndDevelopment.Instance.AddScience(amount, reason)` | ~6 (experiment-transmit, lab-process, contract-complete, strategy-tick, admin-grant) | Postfix: `AgencyScienceMutateMsgData`. |
| `Reputation.Instance.AddReputation(amount, reason)` | ~4 (contract-complete, contract-fail, strategy-tick, admin-grant) | Postfix: `AgencyReputationMutateMsgData`. |
| `ResearchAndDevelopment.Instance.UnlockProtoTechNode` / `PartTechUnlock` | ~3 | Postfix: `AgencyTechUnlockMsgData` (per-node and per-part). |
| `ScenarioUpgradeableFacilities` facility upgrade writer | ~2 | Postfix: `AgencyFacilityUpgradeMsgData`. |
| `KerbalRoster` hire/fire/status mutation | ~4 | Postfix: `AgencyKerbalUpdateMsgData`. |
| `Contract.Accept` / `Contract.Decline` / `Contract.Complete` / `Contract.Fail` | ~4 | Postfix: `AgencyContractMsgData`. Pairs with `ContractSystem.Instance.Contracts[Offered]` staying shared per §2 — only post-accept transitions are per-agency. |
| `StrategySystem` strategy-activate / strategy-deactivate | ~2 | Postfix: `AgencyStrategyMsgData`. |
| `ProgressTracking` world-firsts achievement | ~3 | Postfix: `AgencyMilestoneMsgData`. |

**Approximate total: ~35 patch sites across all singletons**, down from the original spec's ~250+ read+write surface. CLAUDE.md's "~83 reference sites for `Funding.Instance`" count was reads + writes combined; the projection layer takes the reads off the table.

**Bracket pattern (BUG-025 precedent):** every server-acknowledged refund/credit that fires KSP's `OnXxxChanged` event must be bracketed in `ShareXxxSystem.Singleton.StartIgnoringEvents() / StopIgnoringEvents()` to suppress the feedback loop where the local mutation rebroadcasts our new total back to the server. Lesson learned the hard way on the shared-agency BUG-025 fix; mandatory for every `AgencyMutate*` handler.

### New client systems

- **`LmpClient/Systems/Agency/AgencySystem.cs`** — mirror of the server registry. Receives `AgencyStateMsgData` updates. Exposes `LocalAgency` to Harmony patches.
- **`LmpClient/Systems/Agency/AgencyMessageSender.cs`** — outbound mutation requests.
- **`LmpClient/Systems/Agency/AgencyMessageHandler.cs`** — inbound state updates.

### UI

- **`LmpClient/Windows/Agency/AgencyWindow.cs`** — IMGUI window showing the local agency's status and a list of all agencies on the server (display name + owning player + vessel count). No funds/science visibility for other agencies (privacy default).
- **`LmpClient/Windows/Agency/AgencyCreateWindow.cs`** — first-connect dialog: enter desired agency display name, confirm starting resources, create.
- **Tracking station overlay** — Harmony-patch the tracking station vessel list to append an "Agency: {name}" line per vessel.
- **Status window extension** — add the local agency name + a "switch to agency view" button.

### Vessel launch flow

When the local player launches a vessel from the editor, the launch path needs to stamp `OwningAgencyId` on the vessel proto before send. Patch site: `LmpClient/Systems/VesselProtoSys/VesselProtoSystem.SendVesselMessage` or the equivalent serialization entry point.

---

## 7. Settings additions

New entries in `Server/Settings/Definition/GameplaySettings.cs`:

```
PerAgencyCareer (bool, default false)
DefaultStartingFunds (double, default 25000)         // stock career value
DefaultStartingScience (double, default 0)
DefaultStartingReputation (double, default 0)
AgencyInactivityDays (int, default 0)                 // 0 = never auto-cleanup
AllowAgencyRename (bool, default true)
PrivateAgencyResources (bool, default true)           // hide other agencies' funds/sci/rep in UI
```

---

## 8. Test plan

### `ServerTest/` additions (net10.0, MSTest)

1. **`AgencyStateTest`** — round-trip persistence (save + load), default-construction defaults, mutation tracking.
2. **`AgencySystemTest`** — register, load, save, cleanup-inactive, transfer.
3. **`AgencyValidatorTest`** — funds non-negative, tech prerequisites, facility tier ordering.
4. **`LockSystemAgencyTest`** — `Control`/`Update`/`UnloadedUpdate` rejection when `requesterAgency != vesselAgency`. Stacks on existing `LockSystemTest`.
5. **`ShareFundsSystemAgencyTest`** — per-agency mode routes mutation to sender only, not broadcast. Shared-agency mode still broadcasts.
6. **`VesselOwningAgencyTest`** — round-trip `lmpOwningAgency` ConfigNode field. Vessel-couple sets to initiator's agency.

### `MockClientTest/` additions (Stage 4.10 follow-on)

7. **`AgencyHandshakeTest`** — connect a mock client; server creates agency; subsequent `AgencyStateMsgData` arrives within N seconds.
8. **`CrossAgencyLockRejectionTest`** — two mock clients in different agencies; one tries to acquire Control on the other's vessel; server rejects with `CrossAgencyDenied`.
9. **`AgencyFundsMutationTest`** — mock client requests funds mutation; server applies; new value arrives back.

### `LmpClientTest/` (new project, net472, MSTest)

Per Stage 4.10 the LmpClient lacks a test project. Per-agency work is the natural moment to create it:

10. **`AgencyHarmonyPatchTest`** — verify `Funding.Instance.Funds` getter returns local agency's funds, setter routes to mutation sender. Tests can stub KSP singletons without a full KSP load.

---

## 9. Migration & rollout

### Migration

**v1 = fresh-start only.** Operators wishing to enable per-agency mode on an existing shared-agency server must:

1. Archive the existing `Universe/` directory.
2. Set `PerAgencyCareer = true` in `Settings/GameplaySettings.xml`.
3. Restart the server with an empty universe.

No automated migration tool in v1. Document this clearly in CLAUDE.md and server release notes.

### Rollout

Branch: `feature/per-agency` (Stage 5.12 creates it).

1. **Stage 5.12** — branch creation. CLAUDE.md update flagging the branch's existence.
2. **Stage 5.13** — PlagueNZ comparison pass. Read their 113 commits; produce `docs/research/05a-plaguenz-audit.md` with their decisions and where ours differ. Output is research, no code.
3. **Stage 5.14** — server-side `AgencySystem` + `AgencyState` + persistence + admin commands. Lands without UI; verified by `ServerTest` + `MockClientTest`.
4. **Stage 5.15** — wire protocol (0.31.0 bump) + client Harmony patches. The "wide-but-shallow" pass over every singleton access.
5. **Stage 5.16** — client UI (`AgencyWindow`, `AgencyCreateWindow`, tracking-station overlay).
6. **Stage 5.17** — `OwningAgency` vessel field + `LockSystem` cross-agency rejection. Builds on Stage 5.15.
7. **Stage 5.18** — continuous PlagueNZ comparison checks. Pull a fresh diff every 2 weeks and update `05a-plaguenz-audit.md`.

Merge to `master`: only after all Stage 5.* items land green and the dual-mode toggle is verified to not regress shared-agency behavior.

---

## 10. Open questions and explicit deferrals

### Resolved (signed off 2026-05-17 by Majestic95)

- **Q1 — PrivateAgencyResources:** Hidden by default. `PrivateAgencyResources = true`. Each agency only sees its own pools; operator can flip the setting per server.
- **Q2 — `transferagency` vessel handling:** Preserve. Vessels follow the agency to the new owner (`lmpOwningAgency` unchanged).
- **Q3 — Pre-0.31 vessels missing `lmpOwningAgency`:** Assigned to the sentinel `"Unassigned"` agency. All players can interact; operator transfers via admin command.
- **Q4 — Contract reward routing:** Contract owner is paid. Vessel ownership is irrelevant for reward routing — a contract is an agency-level relationship, the vessel is just the instrument.
- **Migration tool:** Confirmed fresh-start-only in v1. Operator workflow remains "archive the shared-agency universe, start fresh with PerAgencyCareer enabled." No CLI migrator, no migration UI in v1. The Q3 Unassigned sentinel still covers any stray vessels loaded into a per-agency universe.
- **CommNet:** Confirmed shared infrastructure in v1. All agencies use the same ground stations and any deployed relay. See "Future / v2+" below for the inter-agency CommNet billing direction Majestic95 wants to head once v1 ships.

### Resolved (signed off 2026-05-17, audit-driven — pre-5.14 design checks)

PlagueNZ audit ([`05a-plaguenz-audit.md`](05a-plaguenz-audit.md)) surfaced three architectural decisions that needed pre-coding resolution. Decisions captured here and reflected in §2 (Contracts row), §3 (persistence), §5 (projection strategy), §6 (Harmony surface).

- **Q5 — Read-path projection vs. Harmony interception:** **Hybrid.** Server-side `AgencyScenarioProjector` handles reads at handshake + scene-load (substitutes per-agency ConfigNode blobs into `SendScenarioModules`). Client-side Harmony targets **write methods only** (~35 sites total vs. the original spec's ~250+ read+write surface). See §5 "Career-data projection strategy" for the why. Spec §6 rewritten to reflect the writes-only surface.
- **Q6 — Contract architecture:** **Hybrid: shared offered pool + per-agency Active/Completed/Declined.** Empirically proven by PlagueNZ's painful retreat from full-isolation; CC source verification confirmed five distinct fight surfaces with per-agency contract pools (`ContractPreLoader` ScenarioModule, `onContractsLoaded` event re-fire, `ContractDisabler.SetContractState` global Withdraw, hand-rolled `Activator.CreateInstance + Load`, null-unsafe `HomeWorld()`). Stage 5.17b commits to three implementation rules surfaced by the PlagueNZ DEVLOG: (a) do NOT persist Offered-state per-agency (their JSON bloated to 1,727 entries); (b) per-contract exception isolation in the restore loop (one broken CC contract throwing `Register()` must not abort the load); (c) the shared `ContractPreLoader` ScenarioModule is left untouched. See §2 Contracts row.
- **Q7 — AgencyId on disk + wire:** **GUID throughout.** `Universe/Agencies/{AgencyId GUID}.txt` persistence path with atomic `.tmp + move + .bak` rotation (new `FileHandler.WriteAtomic` in Stage 5.14c). Wire-level identifier is the GUID; player-name is a lookup convenience only. Operator inspection via the existing `listagencies` admin command. PlagueNZ shipped a hardware-hash key and had to write `MigrateLegacyFile` to recover; we pay the GUID complexity up front so future-us doesn't repeat the migration. See §3 persistence subsection.

### Explicit deferrals (out of scope for v1)

- Multi-player agencies / teams via `GroupSystem`.
- Per-agency CommNet enforcement (relays are agency-scoped, peers must pay/borrow for transit).
- Per-agency separate KSCs at different Kerbin coordinates.
- Voluntary vessel ownership transfer UI (admin command only in v1).
- Agency emblems / flags (cosmetic).
- Inter-agency funds/science transfer UI (admin only in v1).
- Migration tool for existing shared-agency saves.
- Per-agency administrative buildings (each agency has its own Mission Control instance, etc.). v1: facility upgrade tiers are per-agency, but the *instance* of Mission Control's contract list is per-agency-scoped, not a separate building.

### Future / v2+ direction (recorded 2026-05-17, not committed scope)

- **Inter-agency relay billing.** Per-agency CommNet plus an opt-in funds-transfer protocol: relay-owner agency can charge per-use (per-packet, per-second, flat session fee — TBD) for traffic that transits its relays. Sets up a broader "agencies can choose to cooperate or not, with currency consequences" surface — leases, science-trade, mission-subcontracting. Becomes the foundation for **player-agency-as-free-will**: the per-agency machinery hosts the rules, the players decide who they help, hinder, or do business with.
- **Inter-agency funds/science/contract transfer UI.** Same workstream — once Stage 5 ships, agency-to-agency transactions become first-class.

These items are NOT in Stage 5 scope. They drive the Stage-6+ workstream once Stage 5 lands and the v1 per-agency machinery is in the wild.

---

## 11. Acceptance criteria

A coding agent has finished Stage 5 when:

1. `feature/per-agency` is green: builds clean, all existing tests pass, all new tests pass.
2. With `PerAgencyCareer = false` (default), the server behaves identically to current `master`. Verified by running existing `ServerTest` suite against the new build.
3. With `PerAgencyCareer = true`:
   - Two clients connect; each gets a distinct `AgencyState`.
   - Client A launches a vessel; vessel has `lmpOwningAgency = A.AgencyId`.
   - Client B sees the vessel in tracking station, labelled with A's agency.
   - Client B tries to acquire `Control` on it; server rejects with `CrossAgencyDenied`.
   - Client A spends funds; client A's UI updates; client B's funds are unchanged.
   - Client A unlocks a tech node; client B's tech tree is unchanged.
   - Client A upgrades launchpad to tier 2; client B still sees tier 1 when launching.
   - Both clients disconnect, server persists both agencies, restarts; both reconnect, both agencies restored.
4. Cross-mode rejection: a 0.30.x client connecting to a 0.31.0 server is rejected with a clear error message.
5. CLAUDE.md updated: Stage 5 marked complete, new architecture notes added, `ActiveFixes[]` entry confirmed.

---

## 12. Implementation ordering for the coding agent

In dependency order. Each step lands as its own commit (or small commit series) on `feature/per-agency`. Don't move to step N+1 until N is green.

1. **Branch + settings + protocol bump.** Create `feature/per-agency`. Add `PerAgencyCareer` setting (default false, so behavior is unchanged). Bump protocol to 0.31.0 in `LmpVersioning.cs` and reject cross-version.
2. **`FileHandler.WriteAtomic` + `AgencyState` + persistence.** Add atomic-write helper (.tmp + move + .bak per §3) to `FileHandler`. Pure data + ConfigNode round-trip for `AgencyState`. No wire protocol, no UI. Test via `FileHandlerAtomicWriteTest` + `AgencyStateTest`.
3. **`AgencySystem` + lifecycle.** Register/load/save/cleanup. Hooked into `ClientConnectionHandler`. Test via `AgencySystemTest`.
4. **Wire protocol messages.** Define all `Agency*MsgData` types in `LmpCommon/Message/Data/Agency/`. No handlers yet — just the wire definitions. Verified by `SerializationTests`.
5. **Server-side handlers.** Implement message handlers in `Server/Message/Agency/`. Each mutation message validates + applies + responds.
6. **`MockClientTest` agency harness.** Extend the mock client to send agency-create-request and consume agency-state messages. Test via `AgencyHandshakeTest`.
7. **`OwningAgency` on vessels.** Add `lmpOwningAgency` field. Stamp on launch. Round-trip through proto. Test via `VesselOwningAgencyTest`.
8. **`LockSystem` cross-agency rejection.** Test via `LockSystemAgencyTest` + `CrossAgencyLockRejectionTest`.
9. **`AgencyScenarioProjector` + `ScenarioSystem.SendScenarioModules` hook (Q5 Hybrid — read path).** Per-player scenario projection for funds/science/rep/tech/contracts/strategies/facilities/kerbals. Tested by extending `MockClientTest` to assert a client receives an agency-specific `ScenarioModules` blob at handshake.
10. **`AgencyContractRouter` (Q6 contracts — shared offered + per-agency Active/Completed/Declined).** Hybrid contract routing with the three Stage-5.17b commitments: (a) no Offered persistence per-agency, (b) per-contract exception isolation, (c) CC's `ContractPreLoader` ScenarioModule untouched. Tested by `ContractRouterTest` + a CC-installed soak run in 5.18e.
11. **Server-side per-agency routing of `Share*` messages (write path).** Each of the 13 systems gets the `PerAgencyCareer` branch, applying mutations to sender's `AgencyState` and echoing only to the sender. Bracket every server-acknowledged delta with `Share*System.Singleton.StartIgnoringEvents/StopIgnoringEvents` per BUG-025 precedent.
12. **Client `AgencySystem`.** Mirror registry. Receive state updates.
13. **Client Harmony patches (Q5 Hybrid — write path).** ~35 sites across `Funding.Instance.AddFunds` → `R&D.AddScience` → `Reputation.AddReputation` → tech writers → facility writers → roster writers → contract Accept/Decline/Complete/Fail → strategy activate/deactivate → world-firsts. **Reads are NOT patched** — they hit the projector-populated singleton from step 9. One patch group per session, with the test plan verifying each.
14. **Client UI.** `AgencyWindow`, `AgencyCreateWindow`, tracking-station overlay.
15. **Admin commands.** `listagencies`, `setagency*`, `transferagency`, `deleteagency`.
16. **CLAUDE.md update + acceptance run + CC soak.** Full Stage 5 acceptance criteria walkthrough; plus an explicit soak with Contract Configurator installed to validate the §2/§6 hybrid against the audit's residual risks.
17. **Merge to `master`.** Squash or merge-commit per project convention.

Estimated effort by CLAUDE.md: 2–4 months. Step 13 (Harmony patches) is no longer the long pole at ~35 sites (projection collapsed the read surface) — step 10 + 11 + the CC soak are now the highest-uncertainty work.

---

## 13. Open coordination concerns

- **AdmiralRadish:** unlikely to touch per-agency turf (he's focused on stability fixes). Low coordination risk. Still: before any major commit on `feature/per-agency`, `git fetch upstream` and check for new commits touching `LmpClient/Harmony/` or career-related systems.
- **PlagueNZ:** alpha-quality fork, bus factor 1, 113 commits. Stage 5.13 + 5.18 cover the comparison. We adopt nothing from them but check our decisions against theirs as a sanity layer.
- **Upstream PR posture:** per CLAUDE.md fork-master strategy, no upstream PRs from Stage 5. Per-agency is fork-divergent by design.
