# Stage 5 — Per-Agency Career — Progress Tracker

**Branch:** `feature/per-agency` (created 2026-05-17, branched from `master` at `6515e006`).
**Spec:** [`05-per-agency-spec.md`](05-per-agency-spec.md) — source of truth, 351 lines, signed-off.
**Audit:** [`05a-plaguenz-audit.md`](05a-plaguenz-audit.md) — PlagueNZ benchmark comparison (Stage 5.13).

This file is the **working tracker** for Stage 5 execution — updated each session, lives on `feature/per-agency` only. Never merges back to `master` (it's branch-state, not a fork artifact).

---

## Sign-off ledger

Resolved 2026-05-17 (session 10) by Majestic95 — recorded in spec §10.

| ID | Question | Decision |
|---|---|---|
| Q1 | Other agencies' funds/sci/rep visible? | **Hidden by default.** `PrivateAgencyResources = true`. |
| Q2 | `transferagency` admin handling of owned vessels? | **Preserve.** Vessels follow the agency to new owner. |
| Q3 | Pre-0.31 vessels missing `lmpOwningAgency`? | **Unassigned sentinel agency** owns them; operator transfers via admin command. |
| Q4 | Contract reward routing? | **Contract owner is paid.** Vessel ownership is irrelevant to reward routing. |
| Migration | Auto-migration tool in v1? | **No.** Fresh-start-only. Operators archive their shared universe and start fresh. |
| CommNet | Per-agency CommNet in v1? | **No.** Shared infrastructure in v1. Deferred to v2+ with inter-agency relay billing on top. |

### Future direction recorded but NOT in Stage 5 scope

- **Inter-agency relay billing** — per-agency CommNet + opt-in funds-transfer for relay usage. Sets up "agencies can choose to cooperate or not, with currency consequences" as a v2+ surface. Recorded in spec §10 "Future / v2+ direction".
- **Inter-agency funds / science / contract transfer UI** — same workstream as relay billing.

---

## Stage 5.14 go/no-go gate

Stage 5.14 is the first code commit. It does NOT start in session 10. Conditions to open the gate:

1. **Soak window for v0.30.0-private-1 ≥ 48-72h** with no critical regressions from the cohort. If a regression surfaces (especially BUG-008 4a pack-on-load), fix it on `master` and cherry-merge here before resuming Stage 5.14.
2. **MockClientTest `Bug001SoloBroadcastTest.SoloDetected_BroadcastsToConnectedClient` flake investigated.** Either timing fix or documented workaround. Each new MockClientTest in spec §8 inherits this harness reliability ceiling — fix it once now beats working around 4-5 times across Stage 5.
3. **Three design questions surfaced by the PlagueNZ audit (see §"Pre-5.14 design checks" below) are explicitly decided** — either by amending the spec, or by acknowledging "we'll discover this during 5.18". Both are fine; what's not fine is starting code and *then* discovering them.

If any gate is unmet at next session start, prioritise the gate over 5.14 work.

---

## Pre-5.14 design checks (surfaced by 5.13 audit)

These are NOT spec amendments — they're questions the audit forced into the open. Resolve before any code lands.

### Check 1 — Server-side scenario projection vs. client-side Harmony patching

PlagueNZ achieves per-player funds/science/rep/tech without patching `Funding.Instance` / `ResearchAndDevelopment.Instance` / `Reputation.Instance`. They project a personalised scenario blob per client at handshake via `SendScenarioModules`, so the client-side singletons load up "correct" for that player. **Spec §2 + §5 currently bake in a Harmony-heavy design (~83 patch sites for `Funding.Instance` alone).** That work disappears if scenario projection covers the slice.

**Decision needed before 5.14:**
- **Option A — scenario projection only** (PlagueNZ pattern). Cuts Stage 5.18b Harmony work from ~83 sites to near-zero for funds/science/rep/tech. But: harder to enforce per-agency reads at arbitrary call sites the spec doesn't enumerate, fights any third-party mod that bypasses `SendScenarioModules`, and the projection runs on every handshake (cost amortised, but not free).
- **Option B — Harmony-only** (current spec). Maximum coverage, every singleton read is per-agency. Maximum work.
- **Option C — hybrid** (most likely sweet spot). Projection covers the "load per-agency state into the right singletons at handshake/scene-load" path; Harmony patches cover the few high-traffic call sites where mid-session mutation must route per-agency (probably `Funding.AddFunds` / `ResearchAndDevelopment.AddScience` writers, not readers).

Recommendation: re-read PlagueNZ's `Server/System/Scenario/ScenarioSystem.SendScenarioModules` + `PlayerCareerRestore.GetScenarioForPlayer` carefully before deciding. Spec §5 needs an amendment paragraph either way.

### Check 2 — Contract architecture: shared offered pool + per-player Active/Completed

Spec §2 currently says "Per-agency offering pool" (full isolation). PlagueNZ shipped that and hit chaos with Contract Configurator interop (Issue #2 in their tracker, the entire 2026-04-29 DEVLOG session), retreating to the hybrid. **Pre-commit to the hybrid in our spec rather than discovering it the painful way.**

**Decision needed before 5.14:**
- Amend spec §2 row "Contracts" to: shared offered pool, per-agency Active/Completed/Decline-counter. Implementation note: `ContractSystem.Instance.Contracts` is the shared offered pool; per-agency state tracks `ContractState` (Active/Completed/Declined) and reward routing keyed by `(agency, contractGuid)`.
- Or: explicitly defer this decision to Stage 5.17b "Per-agency routing of `Share*` messages" and accept that we'll iterate. Higher risk of mid-stage rework.

### Check 3 — AgencyId stability and key derivation

Spec §3 already declares `AgencyId` as a stable server-issued GUID — good. But there's a subsidiary question PlagueNZ surfaced via their `MigrateLegacyFile` (renamed hardware-hash files to player-name): **what's the agency-lookup key on the wire and on disk?**

**Decision needed before 5.14:**
- Persistence path: `Universe/Agencies/{AgencyId GUID}.json` (PlagueNZ-pattern, plus atomic `.tmp + move + .bak`)? Or `Universe/Agencies/{PlayerName}.json`?
  - GUID is rename-safe but harder to inspect manually. Player-name is easy to read but breaks if a player changes their handle.
  - Recommendation: GUID. Operator inspection helper: a `listagencies` admin command that prints `{AgencyId} → {DisplayName} (currently owned by {PlayerName})`.
- Wire-level: `AgencyId` GUID, not player name. Player-name is a lookup convenience only.

### Audit-derived items NOT to action

- **`lmpOwningAgency` vessel tag** — PlagueNZ punted ("Vessels are always shared"); our spec ships it. Audit confirms this is a clean addition. Don't regress.
- **PlagueNZ commit-trailer AI attribution** — they're not silent-partner. Cosmetic difference, no action needed on our side beyond awareness while reading their history.

See [`05a-plaguenz-audit.md`](05a-plaguenz-audit.md) for full audit; §8 (Conclusion) is the load-bearing section.

---

## Step ledger (per spec §12)

Status legend: ⬜ not started · 🟡 in progress · ✅ done · ⏸ blocked · ❌ skipped (with reason)

| Step | Title | Status | Notes |
|---|---|---|---|
| 5.12 | Branch + Q&A sign-off + audit | ✅ | Session 10 (2026-05-17). Branch from `6515e006`. |
| 5.13 | PlagueNZ audit doc | ✅ | Session 10. [`05a-plaguenz-audit.md`](05a-plaguenz-audit.md). |
| 5.14a | `PerAgencyCareer` setting (default false) | ⬜ | Behaviour-preserving when false — gate this with regression run of existing ServerTest suite. |
| 5.14b | Protocol bump 0.30.0 → 0.31.0 + cross-version reject | ⬜ | Update `LmpCommon/LmpVersioning.cs`. Mirror the 0.30.0 break pattern (no cross-compat row). |
| 5.14c | `Server/System/Agency/AgencyState.cs` + ConfigNode round-trip | ⬜ | Test: `AgencyStateTest`. Pure data; no wire, no UI. |
| 5.15a | `Server/System/Agency/AgencySystem.cs` lifecycle | ⬜ | Register/load/save/cleanup. Hook into `ClientConnectionHandler`. Test: `AgencySystemTest`. |
| 5.15b | Wire protocol — `LmpCommon/Message/Data/Agency/*MsgData` | ⬜ | Definitions only; no handlers. Test: `SerializationTests` round-trip. |
| 5.15c | Server-side message handlers in `Server/Message/Agency/` | ⬜ | Each mutation validates + applies + responds. |
| 5.16a | MockClientTest agency harness | ⬜ | Extend `MockNetClient` to send `AgencyCreateRequest` and consume agency-state. Test: `AgencyHandshakeTest`. |
| 5.16b | `lmpOwningAgency` on vessels — stamp on launch + proto round-trip | ⬜ | Test: `VesselOwningAgencyTest`. |
| 5.17a | `LockSystem` cross-agency rejection | ⬜ | Test: `LockSystemAgencyTest` + `CrossAgencyLockRejectionTest`. |
| 5.17b | Per-agency routing of `Share*` messages (×13 files) | ⬜ | One branch per system. Each gets focused test. |
| 5.18a | Client `AgencySystem` mirror | ⬜ | Receive state updates. |
| 5.18b | Client Harmony patches: Funding → Science → Rep → tech → roster → facilities → contracts → strategies → milestones | ⬜ | Long pole. One subsystem per session. |
| 5.18c | Client UI — `AgencyWindow`, `AgencyCreateWindow`, tracking-station overlay | ⬜ | |
| 5.18d | Admin commands — `listagencies`, `setagency*`, `transferagency`, `deleteagency`, `setagencyfunds`, etc. | ⬜ | |
| 5.18e | Continuous PlagueNZ comparison pass | ⬜ | Re-run audit after major changes; not a one-time activity. |
| 5.18f | Final CLAUDE.md update + Stage 5 acceptance run | ⬜ | Per spec §11. Then dual-mode regression. |

---

## Known coordination concerns (per spec §13)

- **AdmiralRadish.** At session start: `git fetch upstream` + `git log master..upstream/master -- LmpClient/Harmony Server/System/Share*`. If anything lands in his branch touching those areas, merge into `feature/per-agency` before continuing.
- **PlagueNZ.** Alpha, bus factor 1. 113 commits — covered by the [audit doc](05a-plaguenz-audit.md) once and again at the end of step 5.18e. We adopt nothing.
- **Upstream PR posture.** Per fork-master strategy, **no upstream PRs from this branch**. It's fork-divergent by design.

---

## Cross-links

- [`05-per-agency-spec.md`](05-per-agency-spec.md) — authoritative spec.
- [`05a-plaguenz-audit.md`](05a-plaguenz-audit.md) — Stage 5.13 PlagueNZ comparison.
- [`../near-term-todos.md`](../near-term-todos.md) — pre-Stage-5 backlog (currently: MockClient Bug001 flake, auto-updater fork-edit).
- [`01-bug-inventory.md`](01-bug-inventory.md) — top-10 bug closures that gated Stage 5.
- CLAUDE.md "Stage Roadmap" §Stage 5 — high-level checklist.
- CLAUDE.md "Branching" — fork-local rule.
