# Per-agency `start` Tech node seed — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `113557de` (test(client): lint client-system lifecycle conventions)
**Discipline:** Per [[feedback-breakage-analysis]] — mandatory before non-trivial changes.
**Motivation:** Live-soak finding 2026-05-20. Per-agency Career players hit "Unavailable Experimental Parts" when launching any vessel using stock starter parts (`mk1pod`, `parachuteSingle`, `basicFin`, etc.). Root cause: KSP auto-unlocks the `start` Tech node at Career-game-creation time without firing `ResearchAndDevelopment.UnlockTechWithParts`, so `AgencyTechRouter.TryRoute` never sees it, so `AgencyState.TechNodes` never gets a `start` entry. The projector at [Server/System/Agency/AgencyScenarioProjector.cs:750-751](../../Server/System/Agency/AgencyScenarioProjector.cs#L750-L751) strips ALL `Tech` child nodes including `start` from outgoing scenarios and only re-splices entries present in `TechNodes` — leaving every per-agency client with an empty tech tree and no starter parts. Operator workaround (manually click "Research" on Start in R&D) fails because the universe's Start node has non-zero part `entryCost` in funds.

---

## Scope lock — IS

### 1. `AgencySystem.EnsureStartTechSeeded(AgencyState)` helper

NEW METHOD (private static, in existing `Server/System/Agency/AgencySystem.cs`). ~70 lines including XML doc.

**Signature:**
```csharp
private static void EnsureStartTechSeeded(AgencyState state)
```

**Body:**
1. Return early if `state == null`.
2. Return early if `!PerAgencyEnabled` (Career-only; Sandbox/Science skip).
3. Return early if `state.TechNodes.ContainsKey("start")` (idempotent; backfill of an already-seeded agency is a no-op).
4. Acquire `ScenarioDataUpdater.GetSemaphore("ResearchAndDevelopment")` and snapshot the `start` Tech ConfigNode from `ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"]`.
   - If the scenario key is absent (brand-new universe before scenarios populated) → log Warning, return.
   - If the scenario is present but no `Tech` child has `id = start` → log Warning, return.
5. Serialize the Tech ConfigNode to bytes:
   - `var serialized = techNode.ToString();`
   - Strip outer `{...}` braces (matches the wire format `AgencyTechRouter` stores — see [AgencyTechRouter.cs:105-115](../../Server/System/Agency/AgencyTechRouter.cs#L105-L115) + [ScenarioBaseDataUpdater.cs:38-42](../../Server/System/Scenario/ScenarioBaseDataUpdater.cs#L38-L42))
   - `Encoding.UTF8.GetBytes(...)` → `byte[] data`
6. Extract `part = X` values from the Tech ConfigNode (these are the parts auto-unlocked by Start).
7. Under `lock (GetAgencyLock(state.AgencyId))`:
   - Insert `AgencyTechNodeEntry { TechId = "start", Data = data, NumBytes = data.Length }` into `state.TechNodes`.
   - Insert the extracted part names into `state.PurchasedParts["start"]` (HashSet — handles dedup if shared scenario has duplicate `part` values from operator hand-edits).
8. Call `SaveAgency(state.AgencyId)`.
9. Log Normal: `[fix:per-agency-start] Seeded 'start' tech node + N starter parts into agency {AgencyId} for {OwningPlayerName}`.

**Why PurchasedParts too, not just TechNodes.** The shared scenario's Start node has `cost = 0` (free to research) but its parts have non-zero `entryCost` (funds cost to purchase). The projector splices Tech bytes verbatim including their internal `part = X` lines (see [AgencyScenarioProjector.cs:795-825](../../Server/System/Agency/AgencyScenarioProjector.cs#L795-L825)), but KSP's R&D scenario model treats the `part = X` entry as "this part lives in this tech node" — separate from "purchased". Without seeding PurchasedParts, a new agency on a universe with non-zero start-part `entryCost` would unlock Start but still be locked out of using the parts in VAB until they spent funds. Seeding both closes the loop.

### 2. Two call sites in `AgencySystem.cs`

**a) `RegisterAgency` mint path** ([AgencySystem.cs:1727-1764](../../Server/System/Agency/AgencySystem.cs#L1727-L1764)):
Insert `EnsureStartTechSeeded(state);` between the existing `Agencies.TryAdd(...)` (line 1737) and the existing `SaveAgency(state.AgencyId)` (line 1759). The seed will be included in the SAME `SaveAgency` call — one disk write covers both the bare AgencyState construction and the starter seed.

**b) `LoadAgencyFromFile` runtime + boot path** ([AgencySystem.cs:2344+](../../Server/System/Agency/AgencySystem.cs#L2344)):
Insert `EnsureStartTechSeeded(state);` after the load returns a non-null state and BEFORE the caller adds it to the `Agencies` registry. Idempotent guard ensures repeated loads (heal-on-bak recovery, admin disk-edit-then-reload, normal boot) cost only the dictionary check on agencies that already have `start`.

Boot ordering verified at [MainServer.cs:93-110](../../Server/MainServer.cs#L93-L110): `ScenarioStoreSystem.LoadExistingScenarios` (line 97) runs BEFORE `AgencySystem.LoadExistingAgencies` (line 110), so `CurrentScenarios["ResearchAndDevelopment"]` is populated when the boot-time backfill runs.

### 3. New ServerTest file `AgencyStartTechSeedingTest.cs`

~5 test methods, ~250 lines:

1. **`RegisterAgency_UnderPerAgencyCareerCareerMode_SeedsStartTechNode`** — Sets up minimal `ResearchAndDevelopment` scenario with start node containing `part = mk1pod` + `part = parachuteSingle`. Registers an agency. Asserts `state.TechNodes.ContainsKey("start")` + `state.PurchasedParts["start"]` contains both part names.
2. **`RegisterAgency_AlreadyHasStart_IsIdempotent`** — Manually pre-populates an agency's TechNodes with a stub "start" entry. Calls EnsureStartTechSeeded explicitly via reflection (or routes through a second RegisterAgency call on the same player). Asserts the existing entry is unchanged.
3. **`LoadAgencyFromFile_MissingStart_BackfillsOnLoad`** — Writes an agency file to disk with TechNodes that lacks `start` (mimicking Melaus's current state). Calls LoadAgencyFromFile. Asserts `state.TechNodes.ContainsKey("start")` post-load AND that the next `FileHandler.ReadAtomic` shows the persisted seed.
4. **`LoadAgencyFromFile_HasStart_DoesNotReseed`** — Writes an agency file with a custom `start` entry (`Data` distinct from what shared scenario would produce). Calls LoadAgencyFromFile. Asserts the custom Data bytes are preserved.
5. **`EnsureStartTechSeeded_SharedScenarioMissingStart_NoOp`** — Sets up `ResearchAndDevelopment` scenario with NO `start` child (only post-start nodes). Asserts EnsureStartTechSeeded leaves the AgencyState untouched + a Warning is logged.

Plus 1 negative: `EnsureStartTechSeeded_GateOff_NoOp` — PerAgencyCareer=false → helper short-circuits before any work.

### 4. New MockClientTest case `AgencyStartTechProjectionTest.cs`

1 test method, ~80 lines. Boots harness with PerAgencyCareer=true + GameMode=Career + a hand-built `ResearchAndDevelopment` shared scenario containing only the start Tech node. Connects MockNetClient. Asserts the ScenarioReplyMsgData for the new agency contains a `Tech` child with `id = start` and at least one `part = X` value matching the seeded parts.

### 5. CLAUDE.md updates

- **Stack Notes & Patterns Learned**: New entry dated 2026-05-20 documenting the auto-unlock gap + the seed shape (TechNodes + PurchasedParts). Future-self insurance against re-introducing the gap in a refactor.
- **Server System Inventory**: AgencySystem entry already lists the registry / persistence / lifecycle surface; append one sentence noting the boot-time + runtime start-tech seed.
- **Stage Roadmap**: 5.18d/g/WOLF Phase 4 sections updated; append a `5.18h` (or similar) checkpoint marking this fix.
- **ForkBuildInfo.ActiveFixes**: append `per-agency-start-tech-seed` so operators grepping `[fix:` see the seed events.

---

## Scope lock — IS NOT

- **No general "missing-tech-node backfill"** — only `start` is auto-unlocked outside the router path. KSP-side tech-tree edits via `RDTech` debug menu, contract rewards that unlock parts, or Strategia rewards all go through the existing wire paths. If a future bug reveals another auto-unlock vector, that's a separate fix.
- **No retroactive funds refund** — players who already spent funds purchasing Start parts via the manual workaround keep what they have. The seed is additive (PurchasedParts is a set; re-adding existing entries is a no-op).
- **No projector change** — the existing projector splice path is already correct; the bug was missing input data, not wrong projection logic.
- **No client-side change** — server seed is the entire fix; the client's existing handshake + scenario receive path picks up the seeded data automatically.
- **No protocol bump** — wire shape unchanged; this is a server-internal seed + persistence fix.

---

## Edge cases

| Case | Behaviour |
|------|-----------|
| Shared scenario doesn't exist (brand-new universe, no Career bootstrap done) | Helper logs Warning + returns. First player connects, RegisterAgency runs, EnsureStartTechSeeded sees no shared start → no-op. Agency mints unseeded; next handshake delivers an empty tech tree. Operator-visible warning in log gives them a recovery hook. |
| Shared scenario present but no `start` Tech child | Same as above. Defensive — covers operator hand-edits + non-stock career sandbox setups. |
| Agency already has `start` in TechNodes (e.g. seeded by an earlier boot, or operator hand-edited the agency file) | Helper short-circuits at the `ContainsKey` guard. Both call sites (RegisterAgency + LoadAgencyFromFile) are safe to invoke repeatedly. |
| Boot-time backfill races with first client handshake | Both go through `GetAgencyLock(agencyId)`. Boot completes synchronously before `LidgrenServer.SetupLidgrenServer()` at [MainServer.cs:117](../../Server/MainServer.cs#L117) starts accepting messages, so the race window is closed by ordering, not by the lock alone. The lock is defensive. |
| Concurrent RegisterAgency for two players | Already serialised via `PlayerNameLocks` ([AgencySystem.cs:1705](../../Server/System/Agency/AgencySystem.cs#L1705)). Per-agency lock inside the helper is independent and serves only the same-agency reentrancy case. |
| Sandbox or Science mode | `PerAgencyEnabled` returns false; helper no-ops at line 2. No need to handle. |
| Operator-deleted the agency file mid-session, then re-registered | `RegisterAgency` re-mints with `Guid.NewGuid()` (the heal path at [AgencySystem.cs:1717-1724](../../Server/System/Agency/AgencySystem.cs#L1717-L1724) handles the stale-index case). Either way the fresh mint goes through `EnsureStartTechSeeded`. |
| Shared scenario's `start` node bytes contain malformed XML / mod-injected garbage | `ConfigNode.GetNodes("Tech")` returns whatever KSP parsed at load time; if the entry is in CurrentScenarios it's already valid ConfigNode shape. The byte serialisation pass (`ToString()` → strip braces → UTF-8) is loss-less for any valid ConfigNode. |
| Player spent funds purchasing Start parts before the fix lands | Funds are not refunded. The PurchasedParts seed is a `HashSet.Add` — re-adding existing parts is silent. Pre-fix player's already-purchased parts stay purchased; the seed just ensures the rest are available too. |
| Existing agency that has SOME post-start tech nodes researched (Melaus's exact state) | LoadAgencyFromFile backfill adds `start` alongside the existing `basicRocketry`/`engineering101`/etc. nodes. The projector splices all of them. Next vessel-launch attempt succeeds. |

---

## Failure modes considered

| Mode | Mitigation |
|------|------------|
| `ScenarioDataUpdater.GetSemaphore("ResearchAndDevelopment")` deadlock with a concurrent scenario writer | Per BUG-033 precedent the per-scenario semaphore is non-reentrant + the helper only reads. No nested lock; the agency-lock acquisition is INSIDE the scenario-lock release (snapshot → release → mutate AgencyState). No AB-BA cycle. |
| Disk write inside the agency lock blocks too long under busy I/O | Same shape as the existing router writes; per [AgencyTechRouter.cs:117](../../Server/System/Agency/AgencyTechRouter.cs#L117) precedent the per-mutation `SaveAgency` is the established cadence and was deemed acceptable for v1 soak. Seed is one-shot per agency, not per-mutation. |
| Boot-time backfill runs before `LoadExistingScenarios` populates CurrentScenarios | Verified ordering at [MainServer.cs:93-110](../../Server/MainServer.cs#L93-L110): scenarios load at line 97, agencies at line 110. If a future refactor reverses this ordering the helper falls back to its "no shared scenario" branch (Warning + no-op), which is safe. |
| Multiple disjoint `start` Tech nodes in shared scenario | Should be impossible (KSP's R&D uniqueness contract). `GetNodes("Tech")` returns an array; we take the FIRST entry whose `GetValue("id") == "start"` and ignore subsequent matches. If KSP ever ships a build that breaks this contract the helper picks the first and logs a Warning. |
| AgencyState file write fails (disk full, permissions) | `SaveAgency` throws; the exception unwinds through RegisterAgency. Existing behaviour — not new. The agency Dictionary insert at line 1737 has already happened; on next boot LoadExistingAgencies finds the in-memory state was never persisted and the player re-registers. Annoying but not data-loss. |

---

## Multi-lens review plan

After implementation + tests pass, run all four lenses in parallel per [[feedback-review-lens-framing]] + [[feedback-integration-logic-review]]:

1. **General** — code correctness, locking, naming, log levels.
2. **Consumer** — what does the 5.18a client mirror author see? Does AgencyHandshake / AgencyState round-trip carry the new TechNodes + PurchasedParts entries? Is there an arrival-conditions doc gap?
3. **Upgrade** — pre-0.31 universe operators flipping the gate on; existing 3-agency universe (Melaus's) post-fix; partial deploys (server fixed, client stale).
4. **Integration-logic** — trace 6-8 end-to-end scenarios: fresh-mint+launch, returning-player+launch, Melaus-after-backfill+launch, gate-off no-op, shared-scenario-empty defensive path, concurrent-register race, /transferagency on seeded agency, /deleteagency cascade on seeded agency.

Expect 0 MUST-FIX (small surface, well-tested logic). Any SHOULD-FIX from the consumer or upgrade lens gets a follow-up commit before merge to master.

---

## Test surface delta

| Suite | Pre | Post | Delta |
|-------|-----|------|-------|
| ServerTest | 663 | ~669 | +6 (5 positive + 1 gate-off negative) |
| MockClientTest | ~100 | ~101 | +1 (e2e projection check) |
| LmpClientTest | 91 | 91 | 0 (no client change) |
| LmpCommonTest | 14 | 14 | 0 (no wire change) |

---

## Commit metadata

- **Branch**: `feature/per-agency`
- **Commit subject**: `fix(server,agency): seed start tech node + parts on agency mint + load`
- **Scope token**: `server,agency` (per CLAUDE.md allowed scopes)
- **No AI attribution** (silent partner rule)
- **Review receipt**: `.claude/review-receipts/{sha1}.txt` required by `require-bug-review.sh` PreToolUse hook
