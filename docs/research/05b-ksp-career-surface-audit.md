# KSP Career Surface Audit for Per-Agency Career

**Purpose:** Capture the research findings needed to evaluate Luna Multiplayer's Stage 5 per-agency career design against Kerbal Space Program career mechanics and Luna's existing synchronization architecture.

**Audience:** A follow-on model or engineer reviewing whether `feature/per-agency` is complete enough to ship.

**Status:** Research / architecture review. No code changes implied by this document alone.

---

## Executive Summary

The per-agency design is directionally sound: stable server-issued agency GUIDs, opt-in dual mode, atomic persistence, `lmpOwningAgency` vessel tags, cross-agency lock rejection, server-side scenario projection for reads, and routed writes for mutations are the right primitives.

The risk is completeness. KSP career progression is wider than funds/science/reputation/contracts. Luna already has explicit synchronization surfaces for science subjects, technology, part purchases, experimental parts, achievements, strategies, facility upgrades, facility damage/repair, kerbals, contracts, and generic ScenarioModules. Per-agency mode is not safe to ship until every existing shared career sync path is either routed per agency or explicitly declared shared by design.

Current local code appears partially advanced beyond the progress tracker: `AgencyContractRouter` exists and `ShareContractsSystem` routes contracts through it under `PerAgencyCareer=true`. Most other `Share*` systems still follow shared-agency behavior unless later uncommitted work exists elsewhere.

---

## Sources Consulted

Repo docs:

- `docs/research/05-per-agency-spec.md`
- `docs/research/05a-stage5-progress.md`
- `docs/research/05a-plaguenz-audit.md`
- `docs/research/04-mock-client-harness-design.md`
- `CLAUDE.md`

Repo code surfaces:

- `Server/System/Agency/AgencyState.cs`
- `Server/System/Agency/AgencySystem.cs`
- `Server/System/Agency/AgencyScenarioProjector.cs`
- `Server/System/Agency/AgencyContractRouter.cs`
- `Server/System/ScenarioSystem.cs`
- `Server/System/ScenarioStoreSystem.cs`
- `Server/System/Share*System.cs`
- `Server/System/KerbalSystem.cs`
- `Server/Message/ShareProgressMsgReader.cs`
- `Server/Message/FacilityMsgReader.cs`
- `LmpClient/Systems/Share*/`
- `LmpClient/Systems/ShareCareer/ShareCareerSystem.cs`
- `LmpClient/Systems/KerbalSys/`
- `LmpClient/Systems/Facility/`
- `LmpCommon/IgnoredScenarios.cs`

External references:

- Luna Multiplayer wiki: `https://github.com/LunaMultiplayer/LunaMultiplayer/wiki/Limitations`
- Luna issue #3: `https://github.com/LunaMultiplayer/LunaMultiplayer/issues/3`
- KSP `ScenarioModule` API docs: `https://kspmoddinglibs.github.io/KSPDocsSite/class_scenario_module.html`
- KSP `KerbalRoster` API docs: `https://kspmoddinglibs.github.io/KSPDocsSite/class_kerbal_roster.html`
- KSP `ContractSystem` API docs: `http://ksp-api-docs.github.io/XML-Documentation-for-the-KSP-API/class_contracts_1_1_contract_system.html`

---

## KSP Career Surfaces That Matter

### Career resources

KSP Career mode centers on:

- Funds
- Science
- Reputation

These are obvious and already central to the spec. However, they are only scalar summaries. The full career state includes separate supporting records that can diverge from the scalar totals.

### Research and Development

`ResearchAndDevelopment` is not just `sci = <value>`.

It includes:

- Current science total
- Science subjects and experiment completion history
- Tech node unlock state
- Purchased-entry parts under tech nodes
- Experimental part availability/counts

Luna already treats these as separate `ShareProgress` subtypes. A per-agency R&D model must split all of them, not only the `sci` scalar.

### Contracts

KSP `ContractSystem` has:

- Offered/current contracts
- Active contracts
- Finished contracts
- Contract parameters
- Contract predicates/types
- Contract Configurator and KSPCF interaction via `ContractPreLoader`

The spec's hybrid approach is correct: shared Offered pool, per-agency post-accept state. This is one of the strongest parts of the design because it reflects PlagueNZ's painful findings and avoids full per-agency Offered-pool bloat.

### Kerbals

KSP `KerbalRoster` covers:

- Available crew
- Assigned crew
- Missing crew
- KIA crew
- Crew assignments
- Respawn behavior
- Roster mutation through hire/fire/status changes

Luna currently stores kerbals as `Universe/Kerbals/{name}.txt` and returns the full roster on request. A per-agency roster must namespace storage and wire flows or deterministic same-name Kerbals will collide.

### Facilities

There are two different concepts:

- Facility upgrade levels, from `ScenarioUpgradeableFacilities`
- Physical building destruction/repair, from `ScenarioDestructibles` plus facility messages

The spec clearly wants per-agency facility upgrade tiers with one shared physical KSC. It is less explicit about destructible building state. A mixed model is possible, but it must be intentional:

- Upgrade tiers can be per agency.
- Physical destruction can remain globally shared.
- If so, UI/projection must handle a building whose tier is agency-specific but damaged/intact state is global.

### Strategies and administration

Strategies affect funds/science/reputation conversion and progression. Luna already has `ShareStrategy`. The spec includes per-agency strategies, but implementation must split both:

- The StrategySystem scenario read path
- Strategy activation/deactivation writes

### Achievements and world firsts

KSP progress tracking affects career feel and sometimes contracts. Luna currently uses `ShareAchievements` and writes `ProgressTracking`. The spec says world firsts/milestones are per-agency, but this must cover both:

- `ProgressTracking`
- `ScenarioAchievements`

### Recovery, launch costs, revert, termination

Luna has special multiplayer semantics:

- Quickload is disabled.
- Revert cannot be normal single-player time travel.
- Revert-to-editor removes the vessel and refunds ship cost through local career events.
- Recovery removes a vessel from the server and KSP applies career rewards locally.
- Termination/removal has lock and kerbal effects.

Per-agency design must define who receives or pays:

- Launch cost
- Revert-to-editor refund
- Recovery funds
- Recovery science
- Reputation changes
- Vessel termination side effects

The likely rule is: the agency owning the vessel pays or receives vessel-linked economic effects, while contract rewards go to the contract-owning agency. The spec already says contract owner is paid and vessel ownership is irrelevant for contract reward routing; it should also state the non-contract recovery/revert rule explicitly.

### Science mode

Luna's `ShareScience`, `ShareTechnology`, and `ShareScienceSubject` run in `Career | Science`. The Stage 5 spec is framed as "per-agency career," but current projection bypasses Sandbox, not Science mode. That means Science mode may partially receive per-player R&D behavior unless explicitly blocked or designed.

Decision needed:

- Career-only: reject or ignore `PerAgencyCareer=true` when `GameMode != Career`.
- Reduced Science mode: split science total, science subjects, tech, part purchase, experimental parts; omit funds/reputation/contracts/facilities.
- Full shared Science behavior: document that Science mode remains shared even when PerAgencyCareer is true.

### DLC and extra scenarios

Luna creates or handles:

- `DeployedScience`
- `ROCScenario`
- `ResourceScenario`
- `PartUpgradeManager`
- `VesselRecovery`
- `CommNetScenario`
- `SentinelScenario`
- `ScenarioContractEvents`

Most are not explicitly per-agency in the spec. That may be fine, but it should be documented. Ground deployables are a known Luna limitation, and `DeployedScience` can create vessels (`DeployedSciencePart`, `DeployedScienceController`) through paths that are not normal launches.

---

## Luna's Existing Career Sync Architecture

### Generic scenarios

Luna serializes ScenarioModules through `LmpClient/Systems/Scenario/ScenarioSystem.cs`, sends them to the server, and the server stores them in `ScenarioStoreSystem.CurrentScenarios` and `Universe/Scenarios/*.txt`.

However, `LmpCommon/IgnoredScenarios.cs` excludes many career-critical modules from generic send:

- `ContractSystem`
- `Funding`
- `ProgressTracking`
- `Reputation`
- `ResearchAndDevelopment`
- `ScenarioDestructibles`
- `ScenarioUpgradeableFacilities`
- `StrategySystem`

These are intentionally handled through dedicated systems.

### ShareProgress

`ShareProgressMsgReader` dispatches these subtypes:

- `FundsUpdate`
- `ScienceUpdate`
- `ScienceSubjectUpdate`
- `ReputationUpdate`
- `TechnologyUpdate`
- `ContractsUpdate`
- `AchievementsUpdate`
- `StrategyUpdate`
- `FacilityUpgrade`
- `PartPurchase`
- `ExperimentalPart`

For shared-agency mode, the server generally relays to all other clients and writes the canonical shared scenario.

Important exception:

- `ShareTechnologySystem` has BUG-025 duplicate tech purchase protection and can send `TechnologyRejected` to refund the sender.
- `ShareContractsSystem` now appears to call `AgencyContractRouter.TryRoute` when `PerAgencyCareer=true`.

Any per-agency implementation must preserve these existing special semantics.

### Kerbals

Kerbal sync is separate from ShareProgress:

- Client sends kerbal proto/remove/request messages.
- Server stores `Universe/Kerbals/{KerbalName}.txt`.
- Server sends all kerbals on request.

This is currently global and not agency-scoped.

### Facility damage/repair

Facility damage and repair are not the same as upgrade level. They use facility messages:

- `FacilityCollapseMsgData`
- `FacilityRepairMsgData`

Server updates `ScenarioDestructibles` and relays to peers.

### Settings

Settings reply carries game mode, difficulty, starting funds/science/rep, economy multipliers, negative currency, Kerbal XP flags, CommNet settings, part upgrade flags, and other global rules. Per-agency state splitting does not imply per-agency rules splitting.

Recommended spec wording: "Agency career state is split. Server difficulty/economy rules remain global unless explicitly stated otherwise."

---

## Current Per-Agency Implementation Observations

### Implemented or partially implemented

- `PerAgencyCareer` server setting exists.
- Protocol bumped to `0.31.0`.
- `AgencyState` persists scalar funds/science/reputation and contract entries.
- `AgencySystem` registers/loads/saves agencies.
- Handshake sends agency state when gate is on.
- `AgencyScenarioProjector` projects root-level `funds`, `sci`, and `rep` for outgoing scenarios.
- `lmpOwningAgency` exists on vessels.
- `LockSystem` rejects cross-agency vessel lock acquisition.
- `AgencyContractRouter` implements a server-side hybrid contract route.

### Not complete

- `AgencyScenarioProjector` only projects `Funding`, `ResearchAndDevelopment` scalar `sci`, and `Reputation`.
- Most `Share*` systems still need per-agency branches.
- `AgencyState` does not yet persist R&D internals, science subjects, purchased parts, experimental parts, kerbals, strategies, achievements, facility levels, destructibles, etc.
- Client-side `AgencySystem` mirror is not present in the explored code.
- `AgencyContractMsgData` can be sent by server but has no complete client consumer yet.
- Admin commands are incomplete; current `setfunds` and `setscience` refuse under `PerAgencyCareer=true`.
- Science mode behavior is undefined.

---

## Main Critiques of the Spec

### 1. R&D is underspecified

The spec says per-agency tech tree, but R&D state is larger:

- Science scalar
- Science subjects
- Tech nodes
- Purchased parts
- Experimental parts

The spec should add a dedicated R&D section that maps each of those to persistence, projection, routing, and tests.

### 2. Science mode is ambiguous

Luna shares science/tech/subjects in Science mode. The spec says Career. This must be a product decision, not an accidental partial behavior.

### 3. Kerbal roster namespacing is not detailed enough

The spec says each agency starts with deterministic original Kerbals. It should also specify:

- File path layout
- Wire routing
- Name collision behavior
- Whether visible roster includes all agencies or local agency only
- How rescue/tourist contract limitations interact

### 4. Facilities need split-tier/shared-damage rules

Per-agency upgrade tiers with shared physical KSC is plausible, but destructible building damage needs explicit rules.

### 5. Recovery and revert economics need explicit ownership rules

Do not rely on generic funds/science events here. Recovery and revert are special in Luna and should have targeted tests.

### 6. Existing Luna limitations should be declared

The release/spec should explicitly inherit or solve:

- Quickload disabled
- Revert restrictions after vessel switch/spectating
- Tourist contracts unsupported
- Rescue contracts unsupported
- Ground deployables limitations

### 7. Difficulty/economy settings remain global

Reward multipliers, negative currency, Kerbal XP, CommNet, part upgrade flags, and facility damage settings are global server rules. This should be stated.

---

## Shipping-Relevant Verdict

The Stage 5 architecture is viable, but the implementation should not be considered complete until every current career sync path is accounted for.

Minimum rule:

For each existing `ShareProgressMessageType`, `KerbalMessageType`, facility message, and relevant scenario module, define exactly one of:

1. Per-agency routed and persisted.
2. Shared by design.
3. Unsupported in per-agency v1 and blocked/disabled.

Anything not classified is a potential cross-agency leak or shared-state corruption.
