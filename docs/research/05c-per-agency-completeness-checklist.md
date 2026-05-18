# Per-Agency Career Completeness Checklist

**Purpose:** Give a follow-on model a concrete checklist for evaluating whether the existing and planned per-agency architecture is complete enough to ship.

**Inputs:** Read this alongside:

- `docs/research/05-per-agency-spec.md`
- `docs/research/05a-stage5-progress.md`
- `docs/research/05a-plaguenz-audit.md`
- `docs/research/05b-ksp-career-surface-audit.md`

---

## How to Use This Checklist

For each surface below, classify the current implementation as:

- `Per-agency complete`
- `Shared by design`
- `Unsupported / blocked in per-agency v1`
- `Incomplete / unsafe`

Do not accept "not mentioned" as an answer. Every current shared career path must have an explicit status.

For each `Per-agency complete` item, verify:

1. Incoming client writes are routed to the sender's agency only.
2. Server persistence writes to `AgencyState` or a clearly agency-scoped file.
3. Outgoing messages go only to the owning player unless intentionally public.
4. Scenario projection sends agency-specific state at handshake and scene load.
5. Reconnect catch-up restores agency state.
6. Shared-agency mode is unchanged when `PerAgencyCareer=false`.
7. Tests cover same-agency, cross-agency, gate-off, and reconnect behavior.

---

## Gate-Level Product Decisions

### Game mode

Decision needed:

- Is `PerAgencyCareer` legal only in Career mode?
- If Science mode is allowed, which R&D surfaces are per-player?
- If Sandbox mode is allowed, what does the setting do?

Suggested acceptance:

- Career: full per-agency mode.
- Science: either explicitly unsupported or a reduced per-player science progression mode.
- Sandbox: no-op.

Files to inspect:

- `Server/System/Agency/AgencyScenarioProjector.cs`
- `Server/System/ScenarioSystem.cs`
- `LmpClient/Systems/ShareProgress/ShareProgressBaseSystem.cs`
- `LmpClient/Systems/ShareScience/ShareScienceSystem.cs`
- `LmpClient/Systems/ShareTechnology/ShareTechnologySystem.cs`
- `LmpClient/Systems/ShareScienceSubject/ShareScienceSubjectSystem.cs`

### Global rules vs split state

Decision needed:

- Confirm server difficulty and economy parameters remain global.
- Confirm CommNet remains shared in v1.
- Confirm KSC physical location remains shared.

Global settings include:

- Reward multipliers
- Negative funds/science allowance
- Kerbal XP
- Part upgrades
- CommNet parameters
- Facility indestructibility/damage settings

Files to inspect:

- `Server/Settings/Definition/GameplaySettingsDefinition.cs`
- `LmpCommon/Message/Data/Settings/SetingsReplyMsgData.cs`
- `LmpClient/Systems/SettingsSys/SettingsMessageHandler.cs`

---

## ShareProgress Surface Checklist

### Funds

Existing shared files:

- `Server/System/ShareFundsSystem.cs`
- `LmpClient/Systems/ShareFunds/`
- `Server/System/Scenario/ScenarioFundsDataUpdater.cs`

Required per-agency behavior:

- Incoming funds updates mutate `AgencyState.Funds` for sender's agency.
- No relay to other agencies.
- Echo/catch-up only to owning player.
- Preserve revert-to-editor refund behavior.
- Preserve `IgnoreEvents` bracketing to avoid feedback loops.

Tests:

- Player A gains funds; B does not.
- Player A reverts to editor; refund affects A only.
- Gate off still broadcasts shared funds exactly as before.

### Science scalar

Existing shared files:

- `Server/System/ShareScienceSystem.cs`
- `LmpClient/Systems/ShareScience/`
- `Server/System/Scenario/ScenarioScienceDataUpdater.cs`

Required:

- Incoming science total updates mutate `AgencyState.Science`.
- No cross-agency relay.
- Revert suppression remains correct.

Tests:

- Experiment/transmit/lab science changes A only.
- Gate off unchanged.

### Reputation

Existing shared files:

- `Server/System/ShareReputationSystem.cs`
- `LmpClient/Systems/ShareReputation/`
- `Server/System/Scenario/ScenarioReputationDataUpdater.cs`

Required:

- Incoming rep updates mutate `AgencyState.Reputation`.
- Contract completion/failure/decline effects route to contract owner agency.
- Strategy-driven rep effects route to the local agency.

Tests:

- Contract reward/penalty does not leak to other agency.
- Gate off unchanged.

### Science subjects

Existing shared files:

- `Server/System/ShareScienceSubjectSystem.cs`
- `LmpClient/Systems/ShareScienceSubject/`
- `Server/System/Scenario/ScenarioScienceSubjectDataUpdater.cs`

Required:

- Persist per-agency science subject records.
- Project per-agency science-subject nodes into `ResearchAndDevelopment`.
- No relay to other agencies.

Tests:

- A runs experiment; B's archive/subject completion remains unchanged.
- Reconnect restores A's subject state.
- Science mode decision tested if Science mode is supported.

### Technology

Existing shared files:

- `Server/System/ShareTechnologySystem.cs`
- `LmpClient/Systems/ShareTechnology/`
- `Server/System/Scenario/ScenarioTechnologyDataUpdater.cs`

Required:

- Persist per-agency tech nodes.
- Project per-agency tech nodes into `ResearchAndDevelopment`.
- Preserve BUG-025 duplicate-purchase rejection/refund semantics per agency, not globally.
- No relay to other agencies.

Tests:

- A unlocks tech; B does not.
- A duplicate-click gets refund/rejection without affecting B.
- A and B can both independently unlock same tech.
- Gate off still uses shared duplicate protection.

### Part purchase

Existing shared files:

- `Server/System/SharePartPurchaseSystem.cs`
- `LmpClient/Systems/SharePurchaseParts/`
- `Server/System/Scenario/ScenarioPartPurchaseDataUpdater.cs`

Required:

- Persist purchased-entry parts per agency.
- Project purchased parts under the agency's tech nodes.

Tests:

- A buys part entry; B still sees it locked/unpurchased.
- Reconnect restores A purchase.

### Experimental parts

Existing shared files:

- `Server/System/ShareExperimentalPartSystem.cs`
- `LmpClient/Systems/ShareExperimentalParts/`
- `Server/System/Scenario/ScenarioExperimentalPartDataUpdater.cs`

Required:

- Decide whether experimental part offers are per-agency or shared.
- If per-agency, persist and project `ExpParts` per agency.
- No cross-agency relay.

Tests:

- A receives/loses experimental part; B unaffected unless shared by design.

### Contracts

Existing files:

- `Server/System/ShareContractsSystem.cs`
- `Server/System/Agency/AgencyContractRouter.cs`
- `Server/System/Scenario/ScenarioContractsDataUpdater.cs`
- `LmpClient/Systems/ShareContracts/`

Required:

- Offered/Generated remains shared.
- Active/Completed/Failed/Cancelled/DeadlineExpired/Withdrawn persist per agency.
- Contract owner receives reward, regardless of vessel ownership.
- `ContractPreLoader` remains untouched/shared.
- Per-contract exception isolation.
- Client has consumer for `AgencyContractMsgData`.
- Scenario projection merges shared Offered plus local Active/Finished.

Tests:

- A accepts an Offered contract; B no longer sees that Offered slot.
- A active contract does not appear active for B.
- A completes contract; A gets reward only.
- Broken CC contract does not abort restore loop.
- CC-installed soak.

### Achievements / progress tracking

Existing files:

- `Server/System/ShareAchievementsSystem.cs`
- `LmpClient/Systems/ShareAchievements/`
- `Server/System/Scenario/ScenarioAchievementsDataUpdater.cs`

Required:

- Decide whether world firsts/milestones are per-agency or globally announced but locally tracked.
- Persist per-agency `ProgressTracking`/achievement nodes if per-agency.
- Project per-agency progress on scene load.

Tests:

- A achieves milestone; B's progression remains available if per-agency.
- Optional global announcement does not mutate B's state.

### Strategies

Existing files:

- `Server/System/ShareStrategySystem.cs`
- `LmpClient/Systems/ShareStrategy/`
- `Server/System/Scenario/ScenarioStrategyDataUpdater.cs`

Required:

- Persist active strategies per agency.
- Route strategy activation/deactivation to local agency only.
- Project per-agency `StrategySystem`.

Tests:

- A activates strategy; B does not.
- Strategy effects route to A's funds/science/rep only.

### Facility upgrade levels

Existing files:

- `Server/System/ShareUpgradeableFacilitiesSystem.cs`
- `LmpClient/Systems/ShareUpgradeableFacilities/`
- `Server/System/Scenario/ScenarioFacilityLevelDataUpdater.cs`

Required:

- Persist upgrade levels per agency.
- Project per-agency `ScenarioUpgradeableFacilities`.
- Enforce tier-order and funds validation per agency.

Tests:

- A upgrades launchpad; B remains lower tier.
- A can launch craft allowed by A's tier; B cannot if lower tier.

---

## Non-ShareProgress Surface Checklist

### Kerbal roster

Existing files:

- `Server/System/KerbalSystem.cs`
- `LmpClient/Systems/KerbalSys/`
- `LmpCommon/Message/Data/Kerbal/`

Required:

- Decide file layout: likely `Universe/Agencies/{AgencyId}/Kerbals` or embedded in `AgencyState`.
- Requests return only local agency roster unless public roster is intentionally visible.
- Kerbal proto/remove writes are agency-scoped.
- Original four Kerbals do not collide across agencies.
- Hire/fire/status changes are scoped.
- Rescue/tourist limitations documented or implemented.

Tests:

- A hires/fires/assigns Kerbal; B roster unaffected.
- A and B can each have a Jeb-like original if that is the design.
- Reconnect restores local roster.

### Facility damage / repair

Existing files:

- `Server/Message/FacilityMsgReader.cs`
- `LmpClient/Systems/Facility/`
- `Server/System/Scenario/ScenarioDestructiblesDataUpdater.cs`

Required decision:

- Global shared building damage/repair, or per-agency damage projection?

If shared:

- Document that physical KSC damage is global even though upgrade tier is per-agency.
- Ensure repair costs, if any, charge correct agency or are disabled/shared by design.

If per-agency:

- Persist/project `ScenarioDestructibles` per agency.
- Do not relay collapse/repair to other agencies.

Tests:

- A destroys a building; expected B behavior matches decision.

### Vessel ownership and vessel-linked economy

Existing files:

- `Server/System/Vessel/Classes/Vessel.cs`
- `Server/System/Vessel/VesselDataUpdater.cs`
- `Server/Message/VesselMsgReader.cs`
- `Server/System/LockSystem.cs`
- `LmpClient/Systems/VesselRemoveSys/VesselRemoveEvents.cs`

Required:

- Launch stamps owner.
- First-proto/preserve behavior safe.
- Recovery funds/science route to owning agency.
- Termination/removal does not leak economy effects.
- Revert-to-editor refund route is explicit.
- Dock/undock ownership rules are implemented and tested.

Tests:

- A recovers A-owned vessel; A gets recovery reward.
- B observing/recovering rules cannot claim A reward.
- Dock ownership follows initiator rule.
- Undock inherits merged vessel owner unless transfer UI exists.

### Agency admin commands

Required commands from spec:

- `listagencies`
- `setagencyfunds`
- `setagencyscience`
- `setagencyreputation`
- `transferagency`
- `deleteagency --confirm`

Existing partial behavior:

- `setfunds` and `setscience` refuse under `PerAgencyCareer=true`.

Tests:

- Admin can repair bad state without hand-editing files.
- `transferagency` preserves vessel ownership by agency GUID.
- Delete reassigns or abandons vessels according to spec.

---

## Scenario Projection Checklist

For every per-agency surface, verify projection into outgoing `ScenarioDataMsgData`.

Currently implemented:

- `Funding.funds`
- `ResearchAndDevelopment.sci`
- `Reputation.rep`

Needed if per-agency:

- `ResearchAndDevelopment` science subject nodes
- `ResearchAndDevelopment` tech nodes
- `ResearchAndDevelopment` purchased parts
- `ResearchAndDevelopment` experimental parts
- `ContractSystem` Active and Finished local state plus shared Offered
- `ScenarioUpgradeableFacilities`
- `StrategySystem`
- `ProgressTracking`
- `ScenarioAchievements`
- `KerbalRoster` or kerbal request flow equivalent
- `ScenarioDestructibles` if per-agency

Projection rules:

- Never mutate `ScenarioStoreSystem.CurrentScenarios` during projection.
- Preserve shared-agency path bit-for-bit when gate off.
- Preserve Sandbox behavior.
- Define Science mode behavior.
- Use structured ConfigNode manipulation where feasible; regex only for simple root scalars.

---

## Client Mirror Checklist

Required client-side systems:

- Receive agency handshake.
- Store local agency ID and display name.
- Receive `AgencyStateMsgData`.
- Receive `AgencyContractMsgData`.
- Receive future mutation/catch-up messages.
- Expose local agency state to UI and write-path patches.
- Avoid applying other agencies' private resources when privacy is on.

Client must also preserve existing `Share*` event suppression:

- `StartIgnoringEvents`
- `StopIgnoringEvents`
- Revert-specific `SaveState`/`RestoreState`
- BUG-025 refund bracketing

---

## Acceptance Test Matrix

At minimum, run or add tests for:

### Dual-mode off

- Existing shared-agency tests pass unchanged.
- `PerAgencyCareer=false` sends no agency-only messages unexpectedly.
- Existing `Share*` broadcasts and scenario writes behave as before.

### Two agencies

- A and B connect and get distinct agencies.
- A state mutation does not alter B state.
- B does not receive private A mutation payloads.
- Reconnect restores both agencies.

### R&D

- Independent science totals.
- Independent science subjects.
- Independent tech unlocks.
- Independent part purchases.
- Independent experimental parts.

### Contracts

- Shared Offered pool.
- Per-agency Active/Finished.
- Contract reward routes to contract owner.
- CC installed soak.

### Kerbals

- Independent roster.
- No file/name collision.
- Assigned/missing/KIA statuses are agency-local unless explicitly shared.

### Vessels

- Ownership stamp on new vessel.
- Cross-agency lock rejection.
- Unassigned sentinel behavior.
- Recovery/revert economics.
- Dock/undock ownership.

### Facilities

- Per-agency upgrade tiers.
- Destructible damage behavior matches design.

### Game modes

- Career full path.
- Science behavior as decided.
- Sandbox no-op.

---

## Red Flags Before Shipping

Do not ship if any of these are true:

- Any `ShareProgressMessageType` still relays private per-agency state to all clients without a deliberate "shared by design" decision.
- `AgencyState` cannot persist all per-agency surfaces that can change mid-session.
- Scenario projection only covers scalar resources while write paths split richer state.
- Client has no handler for server-sent agency mutation/catch-up messages.
- Science mode behavior is accidental.
- Kerbal roster is still globally stored while spec claims per-agency rosters.
- Facility destructible behavior is ambiguous.
- Contract Configurator soak has not been run.
- `PerAgencyCareer=true` can be enabled on a non-fresh universe without clear operator warning/blocking.
