# `<ModName>` — compat layer analysis

_Use this template for each additional mod._ Fill sections; delete unused bullets.

---

## Summary

One paragraph: mod purpose + why it interacts with Luna MP.

---

## Luna Compat status

| In Luna Compat Harmony list? | In Part Sync list? | Server plugin flag? |
|------------------------------|---------------------|---------------------|
| yes / no / partial           | yes / no            | yes / no            |

Upstream link to Luna Compat file if known (Issue/PR):

---

## State ownership

Where does authoritative data live?

- [ ] VesselProto / parts / PartModules
- [ ] ScenarioModules (named: ___)
- [ ] Contracts / CC payloads
- [ ] RNG / resource fields (determinism?)
- [ ] Saves / persistence outside scenario
- [ ] Purely local GUI

---

## This fork touchpoints

List relevant Luna MP fork systems (paths):

- Scenario: `LmpClient/Systems/Scenario/ScenarioSystem.cs`, `Server/System/Scenario/*`, `Server/System/Agency/AgencyScenarioProjector.cs`
- Contracts: `Server/System/Agency/AgencyContractRouter.cs`, `ShareContractsSystem`
- Vessel: `Server/System/Vessel*`, locks, warp
- Custom relay: `LmpCommon/Message/Data/ModMsgData.cs`

---

## Interaction with PerAgencyCareer

Behaviour when **`PerAgencyCareer`** is **on**:

- Career-scoped payloads
- Peer visibility / privacy implications
- Contract Offered-shared vs Active-per-agency edge cases

When **off** (baseline shared career):

-

---

## Proposed layering

| Layer | Owner repo | Deliverable |
|-------|-------------|--------------|
| Core LMP fork | yes/no | Describe |
| Luna Compat (Harmony/MM) | yes/no | Describe |
| Luna Compat server plugin | yes/no | Describe |

---

## Risks

-

---

## Tests

1.
2.

---

## Tracking

| Last reviewed commit / date | |
