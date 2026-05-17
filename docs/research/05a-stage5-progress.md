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

Stage 5.14 is the first code commit. Conditions to open the gate:

| Gate | Status | Notes |
|---|---|---|
| 1. Soak window for v0.30.0-private-1 ≥ 48-72h with no critical regressions | ✅ **Marked good by Majestic95** (session 11, 2026-05-17) | "We got as far as we could and had positive results." If a regression surfaces later (especially BUG-008 4a pack-on-load), fix on `master` and merge here before resuming Stage 5. |
| 2. MockClientTest `Bug001SoloBroadcastTest.SoloDetected_BroadcastsToConnectedClient` flake fixed | ⬜ **OPEN** — last remaining gate | ~1/3 flake on this workstation. Documented at [`../near-term-todos.md`](../near-term-todos.md) §5. Each new MockClientTest in spec §8 inherits this harness reliability ceiling. Est ≈2-3h focused. |
| 3. Three design questions from the PlagueNZ audit explicitly decided | ✅ **Resolved session 11** (2026-05-17) | All three signed off; spec amended. See §"Pre-5.14 design checks" below for the resolved record. |

**Net status: 1 of 3 gates still open** — Bug001 flake fix is the only remaining blocker before Stage 5.14a starts.

---

## Pre-5.14 design checks (surfaced by 5.13 audit) — ALL RESOLVED session 11

Resolved 2026-05-17 (session 11) by Majestic95. Decisions recorded in spec §10 "Resolved (signed off 2026-05-17, audit-driven — pre-5.14 design checks)". Summary below; full reasoning in spec.

| ID | Question | Decision | Spec ref |
|---|---|---|---|
| Q5 (Check 1) | Read-path projection vs. Harmony interception | **Hybrid.** Server-side `AgencyScenarioProjector` handles reads at handshake/scene-load; client-side Harmony targets write methods only (~35 sites vs. ~250+ in original spec). | §5 "Career-data projection strategy" + §6 rewrite |
| Q6 (Check 2) | Contract architecture | **Hybrid: shared offered pool + per-agency Active/Completed/Declined.** Plus three Stage-5.17b commitments: (a) no Offered persistence per-agency, (b) per-contract exception isolation, (c) `ContractPreLoader` ScenarioModule untouched. | §2 Contracts row |
| Q7 (Check 3) | AgencyId provenance | **GUID throughout** — disk + wire. Path `Universe/Agencies/{GUID}.txt` with atomic `.tmp + move + .bak` rotation via new `FileHandler.WriteAtomic` in Stage 5.14c. Operator inspection via existing `listagencies` admin command. | §3 persistence subsection |

### How Q6 was verified

The Contract Configurator source (`jrossignol/ContractConfigurator`) was read directly in session 11. Five distinct fight surfaces would collide with per-agency contract pools: (1) CC's own `ContractPreLoader` ScenarioModule (parallel to stock `ContractSystem` node), (2) CC both subscribes to AND fires `onContractsLoaded`, (3) `ContractDisabler.SetContractState` calls `Withdraw()` globally across `ContractSystem.Instance.Contracts`, (4) hand-rolled `Activator.CreateInstance + Load` deserialization per behaviour/requirement, (5) null-unsafe `HomeWorld()` global function. Hybrid sidesteps all five by keeping the shared offered pool global (CC sees the world it expects); per-agency divergence only kicks in post-Accept.

### Audit-derived items NOT to action (unchanged from session 10)

- **`lmpOwningAgency` vessel tag** — PlagueNZ punted ("Vessels are always shared"); our spec ships it. Audit confirms this is a clean addition. Don't regress.
- **PlagueNZ commit-trailer AI attribution** — they're not silent-partner. Cosmetic difference, no action needed on our side beyond awareness while reading their history.

See [`05a-plaguenz-audit.md`](05a-plaguenz-audit.md) for full audit; §8 (Conclusion) is the load-bearing section.

---

## Step ledger (per spec §12)

Status legend: ⬜ not started · 🟡 in progress · ✅ done · ⏸ blocked · ❌ skipped (with reason)

Step labels here map to spec §12 numbered steps; column "§12" gives the cross-reference. Step ordering revised session 11 to include two new architectural steps (scenario projector + contract router) surfaced by the audit decisions Q5/Q6.

| Step | §12 | Title | Status | Notes |
|---|---|---|---|---|
| 5.12 | — | Branch + Q&A sign-off + audit | ✅ | Session 10 (2026-05-17). Branch from `6515e006`. |
| 5.13 | — | PlagueNZ audit doc | ✅ | Session 10. [`05a-plaguenz-audit.md`](05a-plaguenz-audit.md). |
| 5.13b | — | Pre-5.14 design checks resolved + spec amendments | ✅ | Session 11 (2026-05-17). All three audit checks signed off; spec §2/§3/§5/§6/§10/§12 amended. |
| 5.14a | 1 | `PerAgencyCareer` setting (default false) + protocol bump 0.30.0 → 0.31.0 | ⬜ | Behaviour-preserving when false — gate this with regression run of existing ServerTest suite. Mirror the 0.30.0 break pattern (no cross-compat row in `LmpCommon/LmpVersioning.cs`). |
| 5.14c | 2 | `FileHandler.WriteAtomic` + `Server/System/Agency/AgencyState.cs` + ConfigNode round-trip | ⬜ | Test: `FileHandlerAtomicWriteTest` + `AgencyStateTest`. Pure data; no wire, no UI. Atomic .tmp+move+.bak per Q7 sign-off. |
| 5.15a | 3 | `Server/System/Agency/AgencySystem.cs` lifecycle | ⬜ | Register/load/save/cleanup. Hook into `ClientConnectionHandler`. Test: `AgencySystemTest`. |
| 5.15b | 4 | Wire protocol — `LmpCommon/Message/Data/Agency/*MsgData` | ⬜ | Definitions only; no handlers. Test: `SerializationTests` round-trip. |
| 5.15c | 5 | Server-side message handlers in `Server/Message/Agency/` | ⬜ | Each mutation validates + applies + responds. |
| 5.16a | 6 | MockClientTest agency harness | ⬜ | Extend `MockNetClient` to send `AgencyCreateRequest` and consume agency-state. Test: `AgencyHandshakeTest`. |
| 5.16b | 7 | `lmpOwningAgency` on vessels — stamp on launch + proto round-trip | ⬜ | Test: `VesselOwningAgencyTest`. |
| 5.17a | 8 | `LockSystem` cross-agency rejection | ⬜ | Test: `LockSystemAgencyTest` + `CrossAgencyLockRejectionTest`. |
| 5.17c | 9 | **NEW (Q5):** `AgencyScenarioProjector` + `ScenarioSystem.SendScenarioModules` hook (read path) | ⬜ | Per-player scenario projection. Tested by extending `MockClientTest` to assert agency-specific `ScenarioModules` at handshake. |
| 5.17d | 10 | **NEW (Q6):** `AgencyContractRouter` (shared offered + per-agency Active/Completed/Declined) | ⬜ | Hybrid contract routing with three commitments: no Offered persistence, per-contract exception isolation, `ContractPreLoader` untouched. Test: `ContractRouterTest` + CC soak in 5.18e. |
| 5.17b | 11 | Per-agency routing of `Share*` messages (×13 files) — write path | ⬜ | One branch per system. Bracket every refund with `StartIgnoringEvents/StopIgnoringEvents` per BUG-025 precedent. |
| 5.18a | 12 | Client `AgencySystem` mirror | ⬜ | Receive state updates. |
| 5.18b | 13 | Client Harmony **write-path** patches (~35 sites) | ⬜ | Funding.AddFunds → R&D.AddScience → Reputation.AddReputation → tech writers → facility writers → roster writers → contract Accept/Decline/Complete/Fail → strategy activate/deactivate → world-firsts. **Reads NOT patched** (projector handles them). |
| 5.18c | 14 | Client UI — `AgencyWindow`, `AgencyCreateWindow`, tracking-station overlay | ⬜ | |
| 5.18d | 15 | Admin commands — `listagencies`, `setagency*`, `transferagency`, `deleteagency`, `setagencyfunds`, etc. | ⬜ | |
| 5.18e | 16 | CC-installed soak + continuous PlagueNZ comparison pass | ⬜ | Re-run audit after major changes. CC soak validates §2 contract hybrid + §6 write-path Harmony against the audit's residual risks. |
| 5.18f | 16/17 | Final CLAUDE.md update + Stage 5 acceptance run + merge to `master` | ⬜ | Per spec §11. Then dual-mode regression. |

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
