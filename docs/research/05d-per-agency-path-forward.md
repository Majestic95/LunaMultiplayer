# Per-Agency Career Path Forward

**Purpose:** Provide an implementation-oriented path for a follow-on model to evaluate and adjust the existing Stage 5 per-agency work before shipping.

This document is intentionally specific and prescriptive. It assumes the reader will inspect the current repo state and may need to pivot implementation details while preserving the overall design.

---

## North Star

Ship one Luna binary with two modes:

1. `PerAgencyCareer=false`: existing shared-agency behavior remains unchanged.
2. `PerAgencyCareer=true`: every connected player owns one server-persisted agency with isolated career state, while selected multiplayer-world surfaces remain shared by explicit design.

Do not ship a half-isolated mode where clients see projected private state at handshake but mid-session writes still mutate or relay through the shared global career.

---

## Read These First

Before coding, read in this order:

1. `docs/research/05-per-agency-spec.md`
2. `docs/research/05a-stage5-progress.md`
3. `docs/research/05a-plaguenz-audit.md`
4. `docs/research/05b-ksp-career-surface-audit.md`
5. `docs/research/05c-per-agency-completeness-checklist.md`
6. `CLAUDE.md` sections on Stage 5, MockClient flakes, and test constraints

Then inspect current code:

- `Server/System/Agency/`
- `Server/System/Share*System.cs`
- `Server/System/Scenario/`
- `Server/System/KerbalSystem.cs`
- `Server/Message/ShareProgressMsgReader.cs`
- `Server/Message/FacilityMsgReader.cs`
- `LmpClient/Systems/Share*/`
- `LmpClient/Systems/KerbalSys/`
- `LmpClient/Systems/Facility/`
- `LmpCommon/IgnoredScenarios.cs`

---

## Immediate Architecture Pivots / Clarifications

### 1. Decide Science mode now

Problem:

The spec says per-agency Career, but Luna's science systems run in `Career | Science`. `AgencyScenarioProjector` skips Sandbox but not Science mode.

Required decision:

- Option A: `PerAgencyCareer` is Career-only. If `GameMode != Career`, do not register agencies, do not project, and log a clear warning.
- Option B: Support a reduced per-player Science mode. Split only R&D state: science total, science subjects, tech nodes, purchased parts, experimental parts.

Recommendation:

Choose Option A unless the user explicitly wants per-player Science mode. It is safer and matches the product name.

Implementation implications:

- Gate `AgencySystem.OnPlayerAuthenticated`, `AgencyScenarioProjector`, and all per-agency routing on `GameMode == Career`, not merely non-Sandbox, if Career-only is chosen.
- Add tests for `GameMode.Science` and `GameMode.Sandbox`.

### 2. Declare global rules vs split state

Problem:

KSP difficulty parameters remain global server settings, but the spec may be read as splitting all career-related behavior.

Clarify:

- Split: agency state.
- Shared: server difficulty settings, reward multipliers, negative currency allowance, CommNet, Kerbal XP flags, part upgrade rules, physical KSC coordinates.

Implementation:

- Add explicit spec/release note text.
- Avoid creating per-agency settings unless deliberately scoped.

### 3. Make destructible facilities explicit

Problem:

Per-agency upgrade tiers plus shared KSC damage can coexist, but only if intentional.

Recommendation:

For v1, keep physical building damage/repair shared by design. Keep upgrade tiers per-agency.

Implementation:

- Document this in spec.
- Keep `FacilityMsgReader` / `ScenarioDestructiblesDataUpdater` shared unless user wants per-agency damage.
- Ensure any repair cost, if KSP applies one, charges the acting agency or is blocked/neutralized as needed.

### 4. Inherit Luna limitations explicitly

Problem:

Luna already disables or limits quickload, revert, tourist contracts, rescue contracts, and ground deployables. The per-agency spec should not imply those are solved.

Implementation:

- Add release note/spec text: v1 inherits Luna limitations unless separately implemented.
- If tourist/rescue contracts are still disabled globally, do not attempt per-agency support in Stage 5.

---

## Implementation Sequence

### Phase 0: Stabilize test harness expectations

Do not block the architecture on the known MockClient flake, but do not ignore new deterministic failures.

Use:

- `docs/research/04-mock-client-harness-design.md`
- `project-mock-harness-flakes` memory if available
- `CLAUDE.md` harness notes

Rules:

- Retry known flake once before suspecting logic.
- Do not add broad permanent retries to tests as a substitute for correctness.
- If investigating, instrument `LidgrenServer.SendMessageToClient` with `LunaLog.Debug` send-result capture, not `Console.WriteLine`.

### Phase 1: Complete server-side write routing for ShareProgress

Goal:

Every `ShareProgressMessageType` must be explicitly routed for `PerAgencyCareer=true`.

Pattern:

```text
if (!PerAgencyCareer or unsupported game mode)
    run existing shared path unchanged
else
    resolve sender agency
    mutate sender agency state under agency lock
    save agency
    echo/catch-up only to owner
    do not relay private payload to peers
```

Surfaces:

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

Keep existing special behavior:

- BUG-025 technology duplicate rejection/refund must become per-agency. Two agencies must be allowed to independently unlock the same tech.
- Revert `IgnoreEvents` suppression must remain intact.
- Contract routing must preserve shared Offered pool and owner-only post-accept state.

Suggested implementation:

- Add small router classes per domain rather than stuffing all logic into `Share*System`.
- Example names:
  - `AgencyCurrencyRouter`
  - `AgencyResearchRouter`
  - `AgencyProgressRouter`
  - `AgencyFacilityRouter`
  - Existing `AgencyContractRouter`
- Each `Share*System` should have a tiny gate:
  - `if (AgencyXRouter.TryRoute(client, data)) return;`
  - then existing shared path.

### Phase 2: Expand `AgencyState`

Goal:

Persist all agency-owned state that can change mid-session.

Add fields or child nodes for:

- Science subjects
- Tech nodes
- Purchased parts
- Experimental parts
- Facility upgrade levels
- Strategies
- Achievements / progress tracking
- Kerbal roster, if embedded in agency state

Potential approaches:

1. Structured model per field.
2. Raw ConfigNode bytes by scenario section, like PlagueNZ's JSON-outer/raw-ConfigNode-inner approach.

Recommendation:

Use raw ConfigNode child payloads for complex KSP internals where structure is unstable or third-party-mod affected, especially contracts and possibly tech/science/kerbals. Use typed scalars for funds/science/reputation.

Constraints:

- Use agency locks around multi-field updates.
- Keep forward-compatible parsing.
- Use atomic writes.
- Avoid storing Offered contracts per agency.

### Phase 3: Complete scenario projection

Goal:

At handshake and scene load, each client receives scenario blobs matching that agency's persisted state.

Already projected:

- `Funding.funds`
- `ResearchAndDevelopment.sci`
- `Reputation.rep`

Add projection for:

- `ResearchAndDevelopment` science subject nodes
- `ResearchAndDevelopment` tech nodes
- `ResearchAndDevelopment` purchased parts
- `ResearchAndDevelopment` experimental parts
- `ContractSystem` shared Offered plus local Active/Finished
- `ScenarioUpgradeableFacilities`
- `StrategySystem`
- `ProgressTracking`
- `ScenarioAchievements`
- `KerbalRoster` equivalent if applicable

Projection rules:

- Do not mutate `ScenarioStoreSystem.CurrentScenarios`.
- Prefer ConfigNode manipulation over regex for nested data.
- Keep regex only for simple root-level scalar replacement.
- Gate off must return original text exactly.

### Phase 4: Kerbal roster isolation

Goal:

Make per-agency rosters real or explicitly mark them out of scope.

If implementing:

- Namespace kerbal persistence by agency ID.
- Return only local agency kerbals on kerbal request.
- Route kerbal proto/remove by sender agency.
- Handle original four Kerbals without collisions.
- Define whether other agencies' crew are visible in vessel info and tracking station.

Recommended v1 stance:

Implement per-agency roster if the spec continues to promise it. If too large, downgrade v1 scope explicitly before shipping.

Do not leave global `Universe/Kerbals/{name}.txt` behavior while claiming per-agency roster.

### Phase 5: Client agency mirror and handlers

Goal:

Client can consume server agency messages and keep local state in sync.

Add or verify:

- `LmpClient/Systems/Agency/AgencySystem.cs`
- `AgencyMessageHandler`
- `AgencyMessageSender`
- handlers for:
  - handshake
  - state
  - contracts
  - future mutation/catch-up messages

Client rules:

- Do not show private resources for other agencies by default.
- Apply owner-only messages with event suppression.
- Preserve existing `ShareCareerSystem` ordering when touching KSP career singletons.

### Phase 6: Client write-path patches

Goal:

Ensure KSP mid-session writes route to server agency paths.

Patch or preserve existing event hooks for:

- Funds changes
- Science changes
- Reputation changes
- Science subject receipt
- Technology research
- Part purchase
- Experimental part add/remove
- Facility upgrade
- Strategy activation/deactivation
- Contract accept/decline/complete/fail/cancel
- Achievement/progress events
- Kerbal hire/fire/status changes

Use existing Luna `Share*` systems where possible. The point is not to invent a parallel client protocol if server-side routing of existing messages is enough.

Important:

- Reads are handled by scenario projection.
- Writes are handled by event hooks/message sending.
- Every server echo that mutates local KSP state must be bracketed to avoid rebroadcast feedback loops.

### Phase 7: Recovery/revert/vessel economy audit

Goal:

Close the highest-risk gameplay edge cases.

Define and test:

- Who pays launch cost.
- Who receives recovery funds.
- Who receives recovery science.
- Who gets reputation effects.
- Revert-to-editor refund target.
- Revert-to-launch state restoration.
- Termination effects.
- Vessel owner vs contract owner conflicts.

Recommended rules:

- Vessel-linked non-contract economy routes to `lmpOwningAgency`.
- Contract rewards route to contract owner.
- If vessel owner and active player differ, reject or require admin/transfer flow rather than guessing.

Files:

- `LmpClient/Systems/VesselRemoveSys/VesselRemoveEvents.cs`
- `LmpClient/Systems/ShareFunds/ShareFundsEvents.cs`
- `Server/Message/VesselMsgReader.cs`
- `Server/System/Vessel/VesselDataUpdater.cs`
- `Server/System/LockSystem.cs`

### Phase 8: Admin and operator safety

Goal:

Operators can inspect and fix state without hand-editing files.

Implement:

- `listagencies`
- `setagencyfunds`
- `setagencyscience`
- `setagencyreputation`
- `transferagency`
- `deleteagency --confirm`

Add safety:

- Warn or block enabling per-agency on non-fresh universe unless operator explicitly confirms.
- Keep existing warnings for savings loss / orphaned vessels.
- Ensure archive backups include `Universe/Agencies`.

### Phase 9: UI and visibility

Goal:

Player can understand agency state.

Implement:

- Local agency window
- Agency creation/rename if still desired
- Tracking station agency labels
- Cross-agency lock denial message
- Optional global achievement feed

Do not expose other agencies' funds/science/rep when `PrivateAgencyResources=true`.

### Phase 10: Soak and acceptance

Required before shipping:

- Full dual-mode regression with `PerAgencyCareer=false`.
- Full per-agency acceptance test with two clients.
- Contract Configurator installed soak.
- Reconnect persistence test.
- Recovery/revert test.
- Science mode decision test.
- MockClientTest known-flake handling documented.

---

## Concrete Implementation Checklist by File Family

### Server `Share*System.cs`

For each file:

- Add per-agency gate.
- Do not relay private messages to peers under gate on.
- Mutate `AgencyState`.
- Save agency.
- Send owner-only echo/catch-up.
- Preserve existing shared path when gate off.

Files:

- `ShareFundsSystem.cs`
- `ShareScienceSystem.cs`
- `ShareScienceSubjectSystem.cs`
- `ShareReputationSystem.cs`
- `ShareTechnologySystem.cs`
- `ShareContractsSystem.cs`
- `ShareAchievementsSystem.cs`
- `ShareStrategySystem.cs`
- `ShareUpgradeableFacilitiesSystem.cs`
- `SharePartPurchaseSystem.cs`
- `ShareExperimentalPartSystem.cs`

### Server `ScenarioDataUpdater`

Do not use shared scenario writers for private per-agency state except when intentionally updating shared pools.

Shared pool examples:

- Contract Offered/Generated
- Shared destructibles if v1 keeps physical KSC damage global

Private examples:

- Agency funds/science/rep
- Agency tech/science subjects/part purchases/experimental parts
- Agency active/finished contracts
- Agency facility upgrade levels
- Agency strategies/progress

### `AgencyScenarioProjector`

Move from scalar regex-only projection to a mixed approach:

- Scalars: regex replacement is acceptable.
- Complex nodes: parse to ConfigNode, replace relevant child nodes, serialize.

### `AgencyState`

Add child nodes carefully:

- Omit empty sections for readability.
- Parse missing sections as empty.
- Isolate malformed entries.
- Do not abort entire agency load because one contract/tech/kerbal entry is corrupt.

### `KerbalSystem`

Do not keep a global `Universe/Kerbals` path for per-agency roster unless the spec changes.

Potential design:

- `Universe/Agencies/{agencyId}/Kerbals/{name}.txt`
- Or embed roster child nodes in `AgencyState`

### Client `Share*`

Use existing event hooks where possible. Avoid broad read patches.

Verify:

- Existing `IgnoreEvents` behavior still prevents feedback loops.
- Revert SaveState/RestoreState still works.
- Incoming owner-only echoes update KSP singleton state.

---

## Review Prompts for Other Agents

### Completeness review prompt

```text
Read docs/research/05b-ksp-career-surface-audit.md and docs/research/05c-per-agency-completeness-checklist.md. Then inspect the current repo. Produce a risk-ordered list of every KSP/Luna career surface that is still shared, undefined, or only partially per-agency under PerAgencyCareer=true. For each finding, cite files/symbols and classify as: must fix before shipping, document as shared by design, or explicitly unsupported in v1.
```

### Implementation planning prompt

```text
Using docs/research/05d-per-agency-path-forward.md, propose a commit-by-commit implementation plan to complete per-agency career. Preserve dual-mode behavior when PerAgencyCareer=false. Prioritize ShareProgress routing, AgencyState persistence expansion, scenario projection, client agency mirror, and recovery/revert economics. Include tests for each commit and call out Contract Configurator soak requirements.
```

### Code review prompt

```text
Review the per-agency implementation for cross-agency leaks. Focus on ShareProgress relays, ScenarioDataUpdater writes, KerbalSystem global storage, facility damage/upgrade behavior, AgencyScenarioProjector projection completeness, and client feedback loops. Flag any path where Player A can mutate, receive, or persist Player B's private agency state.
```

---

## Final Guidance

Do not treat "projection works for funds/science/rep" as equivalent to "per-agency career works." Projection solves the read path for a few scalar values. The hard part is routing every mid-session write and every existing Luna sync surface.

The safe path is:

1. Classify every surface.
2. Route or explicitly share each one.
3. Persist private state under agency ownership.
4. Project private state back to the owning client.
5. Preserve shared mode.
6. Soak with Contract Configurator.
