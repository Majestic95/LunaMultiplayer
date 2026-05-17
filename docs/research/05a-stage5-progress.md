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
| 2. MockClientTest `Bug001SoloBroadcastTest.SoloDetected_BroadcastsToConnectedClient` flake DOCUMENTED with retry workaround | ✅ **Resolved session 11 as documented workaround** (2026-05-17) | Tried event-driven `MessageReceivedEvent` receive + AssemblyInitialize JIT-warmup; both made things worse or had no measurable effect. Could not reliably reproduce on demand (3/10 first burst, then ≥40 clean runs, then 0/20 with attempted fixes — measurement noise dominated). Master reverted to baseline; investigation summary in [[project-mock-harness-flakes]]. Retry-once on flake stays the workaround. New Stage 5 MockClientTests inherit the same ~1/3 ceiling; the retry pattern is sufficient. Real fix deferred — would need instrumented repro (dotnet-trace across a failing run) before another attempt. Also surfaced a latent harness race in any AssemblyInitialize warmup that disconnects a client → backup → empty-Subspaces exception kills the receive thread (see memory for details). |
| 3. Three design questions from the PlagueNZ audit explicitly decided | ✅ **Resolved session 11** (2026-05-17) | All three signed off; spec amended. See §"Pre-5.14 design checks" below for the resolved record. |

**Net status: ALL 3 gates resolved.** Gate 2 is a documented-workaround resolution, not a code fix — same retry pattern that's been in use since session 5. Stage 5.14a unblocked.

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
| 5.14a | 1 | `PerAgencyCareer` setting (default false) + protocol bump 0.30.0 → 0.31.0 | ✅ | Session 12 (2026-05-17, commit `49583ec5`). Three edits: AssemblyInfo 0.30.0→0.31.0, `PerAgencyCareer` bool added to `GameplaySettingsDefinition` + explicit `false` in all four Set*() presets matching the file's exhaustive-preset convention, `"per-agency-career"` appended to `ForkBuildInfo.ActiveFixes`. Two-pass review-agent gate; pass-2 [CONSIDER] deferred — see "Deferred items" below. Build + ServerTest (87/87) + LmpCommonTest (6/6) + MockClientTest (12/12) green. |
| 5.14c | 2 | `FileHandler.WriteAtomic` + `Server/System/Agency/AgencyState.cs` + ConfigNode round-trip | ✅ | Session 12 (2026-05-17, commit `d0b484c4`). Five files: FileHandler.WriteAtomic+ReadAtomic helpers (.tmp+rotate-bak+rename, ReadAtomic falls back to .bak), AgencyState pure-data class with 6 scalar fields + ConfigNode round-trip (GUID-N filename, invariant-culture doubles, brace-wrap tolerant, forward-compat zero defaults, missing AgencyId throws), Universe.CheckUniverse hookup creates `Universe/Agencies/`, FileHandlerAtomicWriteTest (9 tests), AgencyStateTest (9 tests). Two-pass review-agent gate; pass-1 [SHOULD FIX] on doc-comment drift fixed; two deferred CONSIDERs below. Build + ServerTest (105/105) + LmpCommonTest (6/6) + MockClientTest (12/12) green. |
| 5.15a | 3 | `Server/System/Agency/AgencySystem.cs` lifecycle | ✅ | Session 12 (2026-05-17, commit `f45cd891`). Four files: AgencySystem static class with `Agencies` + `AgencyByPlayerName` registries, `RegisterAgency` (per-player-name locked, idempotent, seeds from `GameplaySettings.Starting*`), `LoadAgency` (registry-first, disk-fallback), `SaveAgency` (per-agency locked, Serialize+WriteAtomic), `LoadExistingAgencies` (boot-time, per-file isolation), `OnPlayerAuthenticated` (HandshakeSystem hook), `GetAgencyLock` (internal, for Stage 5.17b Share* writers), heal-on-bak-recovery in LoadAgencyFromFile (closes the 5.14c deferred CONSIDER). HandshakeSystem hook fires BEFORE the plugin event so plugins see a populated agency. MainServer.LoadExistingAgencies wired after LoadExistingScenarios. AgencySystemTest 18 cases including the concurrent same-name register from pass-2. Two-pass review-agent gate; pass-1 [SHOULD FIX] race + 3 CONSIDERs all addressed. ServerTest 124/124. |
| 5.15b | 4 | Wire protocol — `LmpCommon/Message/Data/Agency/` registration messages | ✅ | Session 12. **Scope split from the original spec wording** — re-derived: ship the 4 registration messages now (Handshake / CreateRequest / CreateReply / State) since those are what 5.15c (server handlers) and 5.16a (mock client harness) actually need. The 9 mutation + visibility messages from spec §4 (`AgencyFunds/Science/Reputation/Tech/Facility/Kerbal/Contract/Strategy/Visibility`) land alongside their consumers in Stage 5.17b / 5.17d / 5.18c — defining them now without consumers would be premature design. Files: `AgencyMessageType` enum, `AgencyBaseMsgData`, `AgencyInfo` summary helper, 4 concrete `Agency*MsgData`, `AgencySrvMsg` (channel 22), `AgencyCliMsg` (channel 21), `ServerMessageType.Agency=21` + `ClientMessageType.Agency=20`. 6 new `SerializationTests` round-trip cases (with field-equality assertions, stricter than the existing precedent which only checked no-throw). LmpCommonTest 12/12 (was 6). No protocol bump needed — already at 0.31.0 from 5.14a. |
| 5.15c | 5 | Server-side message handlers in `Server/Message/` + outbound sender | ✅ | Session 12 (2026-05-17, commit `57bbc2b1`). Five files: `Server/System/Agency/AgencySystemSender.cs` (SendHandshakeTo + SendStateTo owner-only + SendStateToOwner + SendCreateReplyTo, all gated), `Server/Message/AgencyMsgReader.cs` (CreateRequest validator + handler; S→C subtypes log+drop), `MessageReceiver.HandlerDictionary` += AgencyMsgReader, `HandshakeSystem` extension to push Handshake+State to the new client after auth, AgencySystemTest += 7 ValidateDisplayName cases. Single-pass review-agent approval; 3 deferred CONSIDERs (cross-agency awareness on join → 5.18c, MaxDisplayNameLength hoisting → 5.18a, Guid.Empty failure-marker decoder test → 5.18a). Privacy rule (spec §10 Q1) verified — AgencyStateMsgData is owner-only on every send site. Locking verified — consistent Name→Agency lock order, no AB-BA cycle with future 5.17b writers. ServerTest 131/131 (was 124). |
| 5.16a | 6 | MockClientTest agency harness | ✅ | Session 13 (2026-05-17, commit `d9161c27`). Five files: `MockClientTest/AgencyHandshakeTest.cs` (new — 5 end-to-end tests), `Harness/ServerHarness.cs` (`Universe.CheckUniverse` in Start + `AgencySystem.Reset` + `PerAgencyCareer=false` reset), `Harness/MockNetClient.cs` (inbox rework — List+ManualResetEventSlim replaces destructive BlockingCollection so cross-channel arrival order doesn't drop messages), `Server/Properties/AssemblyInfo.cs` (+InternalsVisibleTo MockClientTest), `Server/System/Agency/AgencySystem.cs` (Reset doc-comment update only). Single-pass independent review-agent: 0 [MUST FIX] / 0 [SHOULD FIX] / 3 [CONSIDER] all addressed in same commit. Build clean. MockClientTest 17/17 (was 12) + ServerTest 131 + LmpCommonTest 12 green. 10/10 dotnet test runs after the change. |
| 5.16b | 7 | `lmpOwningAgency` on vessels — stamp on launch + proto round-trip | ✅ | Session 13 (2026-05-17). Initial implementation at `076d3486`; **five consecutive review rounds** (`f0129746` → `e4b8f781` → `3973f77f` → `ebdee7ae` → `5499101d`) found and fixed 7 real production bugs the prior rounds missed. Round 1 (deep general review) caught 8 test-infra+doc issues (LockStore/RemovedVessels/ModControl harness leakage; persistence assertions served from cache; privacy assertion gaps). Round 2 (server-systems + network parallel) caught 3 real prod bugs: **dual-mode silence violation** (gate=off wrote `00...0` to disk), **RegisterAgency mutation ordering** (index flipped before SaveAgency persists → orphan agencies on crash), and **AgencyHandshakeMsgData OOM DoS** (unbounded OtherAgencyCount). Round 3 (server+net+persistence parallel) caught 1 prod (BackupSystem.SnapshotDirs missing Agencies — archives lost agency state on restore) + 4 hardening. Round 4 (final convergence) caught 1 prod **introduced by Round 3** (null-skip in InternalSerialize created wire desync — reverted). Round 5 (consumer-lens for Stage 5.18a + upgrade-lens for pre-0.31 universes) caught 2 real prod bugs: **first-proto-wins regression on pre-existing vessels** (contradicted spec §10 Q3 Unassigned-sentinel rule, mass-assigned vessels to whoever sent the first proto) and **silent CreateRequest drop under gate=off** (future client mirror would hang). Files (8 production + 4 test): `Vessel.cs`, `VesselDataUpdater.cs`, `VesselMsgReader.cs`, `VesselStoreSystem.cs`, `AgencySystem.cs`, `AgencyMsgReader.cs`, `LockSystem.cs`, `ModFileSystem.cs`, `BackupSystem.cs`, plus 4 AgencyMsgData XML classes hardened with bounded ReadString. Tests: ServerTest 131→140 (+9 unit + 1 stale-index heal regression test), MockClientTest 17→23 (+6 e2e tests covering first-proto stamp, preserve across re-proto, adversarial wire scrub, gate-off no-stamp, gate-off pass-through, pre-existing-Unassigned preserve). Suite-level flake ~20-40% per characterization run = documented Bug001-family baseline. **Verdict after 5 rounds: lens-framed reviews catch real bugs general reviews miss.** Stage 5.16 is shippable. |
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

## Deferred items (surfaced during execution, NOT to action now)

| Item | Surfaced | Why deferred | Where to action |
|---|---|---|---|
| `SettingsHandler.HasDifferencesAgainstGivenSetting` flips the operator's `GameDifficulty` to `Custom` on first boot when `PerAgencyCareer=true` is set with any non-Custom preset. | Stage 5.14a session 12, pass-2 review-agent | The validator reflects over every public property and compares stored `.ToString()` vs. preset-default `.ToString()`; adding `PerAgencyCareer = false;` to all four presets does NOT silence this (preset-default is false either way). `PerAgencyCareer=true` is a no-op today (per-agency logic does not exist yet), so the one-time flip-to-Custom is harmless and informative. | The Stage 5 step that first makes `PerAgencyCareer=true` shippable (likely 5.18 area). Options: (a) add a `[NotDifficultyControlled]`-style attribute and skip in `HasDifferencesAgainstGivenSetting`; (b) move `PerAgencyCareer` out of `GameplaySettingsDefinition` into a dedicated `AgencySettingsDefinition`; (c) accept the flip-to-Custom as documented behaviour. Decide deliberately at that point, not pre-emptively. |
| ~~`FileHandler.ReadAtomic` logs a `LunaLog.Warning` every time it recovers from `.bak`.~~ | ~~Stage 5.14c session 12, pass-1 review-agent~~ | ~~Caller-side concern, not a `FileHandler` design issue.~~ | **CLOSED Stage 5.15a (commit `f45cd891`).** `AgencySystem.LoadAgencyFromFile` checks `FileHandler.FileExists(path)` + `FileHandler.FileExists(path + ".bak")` before `ReadAtomic`; if canonical was missing and `.bak` was present, it rewrites the canonical path via `WriteAtomic` so the warning fires once, not on every subsequent read. Pinned by `LoadAgency_HealsCanonicalPath_AfterBakOnlyRecovery` in `AgencySystemTest`. |
| `FileHandler.GetLockSemaphore` keys per-directory, so every write under `Universe/Agencies/` serializes on a single lock. For N=2-3 players this is fine; at N=20+ with frequent saves it becomes a real contention point. | Stage 5.14c session 12, pass-1 review-agent | Existing FileHandler convention (not a 5.14c change). The lock is per-directory because the original Universe writers (vessels, scenarios, kerbals) wanted directory-level mutual exclusion against backup snapshots. Per-file locking would need a separate semaphore registry. | Stage 5 perf-tracker — revisit after a soak run at target cohort size shows real contention. Don't pre-optimize. |

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
