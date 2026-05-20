# Stage 6 Phase 6.4 — HandleKerbalsRequest per-agency filter — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `20ad1796` (docs(stage6): Phase 6.3 roadmap mark + Stack Notes entry for disk-layout invariants)
**Discipline:** Per `[[feedback-breakage-analysis]]`.
**Motivation:** Phase 6.4 wires the FIRST per-agency-kerbal handler. `HandleKerbalsRequest` (the initial-sync surface, invoked once per connection from the client's first KerbalsRequest) returns the requester's own agency's kerbals under `PerAgencyKerbalRosterEnabled`, the legacy shared `Universe/Kerbals/` set under gate=off. Phase 6.3 already ensures every agency has a seeded `Kerbals/` subdir at this point; Phase 6.4 only chooses the source directory.

`HandleKerbalProto` + `HandleKerbalRemove` stay untouched — Phase 6.5. The asymmetry is deliberate: under Phase 6.4 alone the client receives its own agency's kerbals on connect, but any KerbalProto it then broadcasts still lands in `Universe/Kerbals/` (shared). Production gate=on stays disabled until 6.5 closes the write path.

---

## Scope lock — IS

### 1. `Server/System/KerbalSystem.cs` — new `ResolveKerbalsPathForRequester` pure helper

`internal static string ResolveKerbalsPathForRequester(string playerName)` returns the directory `HandleKerbalsRequest` should enumerate.

Branches (all returning a real string path):

- `!AgencySystem.PerAgencyKerbalRosterEnabled` → returns `KerbalsPath` (legacy shared). Dual-mode silence.
- gate=on AND `AgencyByPlayerName[playerName]` lookup misses → returns `KerbalsPath` + Warning log. Defensive fallback per spec §3 ("Fall back to legacy path if `AgencyByPlayerName` lookup fails."). Unreachable on a healthy server because `PerAgencyEnabled` auto-registers every authed Career player, but the fallback prevents an empty roster on a torn registry state.
- gate=on AND mapping resolves AND `FolderExists(agencyKerbals)` true → returns the agency's `Kerbals/` subdir.
- gate=on AND mapping resolves AND subdir absent → returns `KerbalsPath` + Warning log. Unreachable on a Phase-6.3-shipped universe (mint creates it; load-backfill heals it), but keeps a bricked install limping instead of returning a missing-path that `GetFilesInPath`'s `Directory.GetFiles` would throw on.

`playerName` is the string from `ClientStructure.PlayerName`; the helper takes a string (not the `ClientStructure`) so ServerTest can exercise every branch without constructing a `NetConnection`/`ClientStructure`. Same testability pattern as `EnsureStartTechSeeded(AgencyState)` (Phase pre-6.3) and `AgencyVesselCoupleReconciler.Reconcile` (Mod-compat S1).

### 2. `Server/System/KerbalSystem.cs` — `HandleKerbalsRequest` body

Replace the literal `KerbalsPath` enumeration source with `ResolveKerbalsPathForRequester(client.PlayerName)`. Everything else (KerbalInfo build, `MessageQueuer.SendToClient`) is unchanged.

```csharp
public static void HandleKerbalsRequest(ClientStructure client)
{
    var sourcePath = ResolveKerbalsPathForRequester(client.PlayerName);
    var kerbalFiles = FileHandler.GetFilesInPath(sourcePath);
    var kerbalsData = kerbalFiles.Select(k => { ... });
    ...
}
```

### 3. `ServerTest/PerAgencyKerbalRequestFilterTest.cs` — new unit tests

Direct coverage of `ResolveKerbalsPathForRequester`. Six cases:

1. Gate=off (`PerAgencyKerbalRoster=false`, `PerAgencyCareer=true`, Career) → returns legacy `KerbalsPath`.
2. `PerAgencyCareer=false` (Phase 6.2 combined-gate precondition) → returns legacy.
3. GameMode=Sandbox with both flags on → returns legacy (Career-only product decision).
4. Combined gate on AND mapping exists → returns the agency's per-agency subdir.
5. Combined gate on AND `AgencyByPlayerName` miss → returns legacy + emits Warning.
6. Combined gate on AND mapping exists BUT subdir absent (operator hand-delete) → returns legacy + emits Warning.

Plus one integration-shape test confirming `HandleKerbalsRequest` end-to-end uses the resolved path (via a mock `ClientStructure` substitute or by asserting file-enumeration outcome through a different surface). Actually keep this in MockClientTest where the full handler + wire round-trip is the natural test seam.

### 4. `MockClientTest/PerAgencyKerbalRequestE2eTest.cs` — new e2e tests

Two-client distinct-roster harness. Pattern mirrors `AgencyStartTechProjectionTest` (single-client) + `AgencyHandshakeTest.SecondPlayer_*` (two-client). Three cases:

1. **`TwoClients_GateOn_EachReceivesOwnAgencyRoster`** — Alice + Bob both connect under PerAgencyCareer+PerAgencyKerbalRoster gate, drain Handshake+AgencyHandshake+AgencyState, then each sends `KerbalsRequestMsgData` and asserts the reply contains exactly the 4 stock kerbals (Jeb/Bill/Bob/Val) from their OWN agency's subdir — proved by planting a unique extra kerbal file into Alice's subdir before each request and asserting (a) Alice's reply contains the unique name, (b) Bob's reply does NOT.
2. **`TwoClients_GateOff_BothReceiveSharedRoster`** — both clients connect with `PerAgencyCareer=true` but `PerAgencyKerbalRoster=false`. Seed one unique kerbal into the legacy `Universe/Kerbals/` set. Both Alice and Bob receive the same set including the unique one. Dual-mode silence proof.
3. **`SingleClient_GateOn_NoAgencyMapping_FallsBackToLegacy`** — exercises the defensive fallback path. Hard to set up authentically because every authed Career player auto-registers an agency on handshake, so manually evict the player's `AgencyByPlayerName` entry between handshake and KerbalsRequest. Verify the legacy set is what comes back + a Warning was emitted (via LunaLog scrape isn't reliable; assert behaviourally instead — by checking the legacy file lands in the reply).

### 5. `Server/ForkBuildInfo.cs` — new entry

Append `"per-agency-kerbal-roster-routing"` to `ActiveFixes[]` with a long comment block covering: what Phase 6.4 closes (initial-sync read surface), what's still open (write path until 6.5), the helper's fallback semantics, dual-mode silence, and the spec pointer.

---

## Scope lock — IS NOT

- **No change to `HandleKerbalProto` or `HandleKerbalRemove`.** Phase 6.5 territory. The write paths still go through legacy `Universe/Kerbals/`. Operators flipping `PerAgencyKerbalRoster=true` between 6.4 and 6.5 see a half-implementation where reads diverge but writes still pool — not recommended (the 6.2 boot-refuse already keeps them out of this state unless they opt in).
- **No K1 grief-guard removal.** Spec §Q-K1 keeps it intact under gate=off; under gate=on the scan runs but never trips because the requester only sees their own agency's vessels. Removal is Stage 7.
- **No `AgencyState` field change.** Kerbal data lives in the per-agency `Kerbals/` subdir on disk — never in the `AgencyState` ConfigNode (spec §Q-Disk). Phase 6.4 reads exclusively from the filesystem.
- **No wire-shape change.** `KerbalsRequestMsgData` + `KerbalReplyMsgData` schemas unchanged. Agency-id is implicit-from-sender (resolved server-side via `AgencyByPlayerName`).
- **No protocol bump.** Stays at 0.31.0.
- **No client-side change.** Stage 6 client work is Phase 6.6 (UI annotation). Phase 6.4 is server-only.
- **No external-system change.** WOLF Phase 4 / Mod-compat S1-S4 / MKS Phase 3 routers all read from `AgencyState`-anchored data, not from kerbal files — they don't touch this code path.

---

## Risk assessment

### Race: handshake → registry insert → KerbalsRequest

On a fresh connection, the client sends `HandshakeRequest` → server emits `HandshakeReply` + `AgencyHandshake` + `AgencyState`; client then (on the modern build) sends `KerbalsRequest` after agency state arrives. `AgencyByPlayerName[playerName]` is inserted inside `RegisterAgency` BEFORE `HandshakeSystem` emits the `HandshakeReply` — so by the time the client sees the handshake-ok reply and decides to send the KerbalsRequest, the index already has the entry. Verified via [Server/System/Agency/AgencySystem.cs:1927](Server/System/Agency/AgencySystem.cs) (the `AgencyByPlayerName[playerName] = state.AgencyId;` line, set inside `RegisterAgency` after `SaveAgency`) and [Server/System/HandshakeSystem.cs] (registration before the reply is sent). No race window in production order-of-operations. The defensive fallback in branch (b) protects against future refactors that flip the order.

### Race: `FolderExists` → `GetFilesInPath`

Phase 6.4 calls `FolderExists(agencyKerbals)` and on true falls through to `GetFilesInPath(agencyKerbals)`. Between the two, `/deleteagency` could theoretically remove the subdir — but `/deleteagency` evicts the agency from `AgencyByPlayerName` BEFORE cascading the subdir delete (see Phase 6.3 cascade ordering in `TryDeleteAgency`), so the only way a client with a stale `playerName → agencyId` mapping reaches the FolderExists check is if `AgencyByPlayerName.TryGetValue` returned true racing the eviction. `GetFilesInPath`'s subsequent `Directory.GetFiles` would throw if the dir was deleted in between. Mitigation: wrap the `GetFilesInPath` call in a try/catch in `HandleKerbalsRequest` that falls back to an empty reply if the resolved dir disappears mid-read. Cost is small (one try/catch); benefit is "/deleteagency-during-active-handshake doesn't crash the request thread". **Decided: ADD the try/catch.** Documented at the catch site.

### Race: KerbalProto write to legacy + KerbalsRequest read from agency dir

Operator flips `PerAgencyKerbalRoster=true` mid-session (Phase 6.4 alone, 6.5 not yet shipped). Client's KerbalsRequest goes to per-agency subdir; client's subsequent KerbalProto still writes to legacy `Universe/Kerbals/`. State drift: per-agency subdir contains stock 4 + any kerbal written before flip; legacy dir continues collecting writes. Operator workflow: don't flip mid-session — the boot-refuse predicate from Phase 6.2 already requires opt-in. This is a known transitional hazard until Phase 6.5. Documented in the ForkBuildInfo comment + the spec §Q-Migration ("fresh-start workflow").

### Cross-agency reads

Under gate=on, Alice's KerbalsRequest only enumerates Alice's subdir — Bob's roster is structurally invisible. No file-read leak. The server's `FileHandler.GetFilesInPath` filters to the supplied path; it does not traverse siblings.

### Empty agency subdir

Phase 6.3 ensures the subdir is seeded with 4 files for every minted/loaded agency. If somehow the subdir is empty (e.g., operator hand-deletes all 4 files between server boots without restarting, edge case), `GetFilesInPath` returns an empty array and the client receives `KerbalReplyMsgData` with `KerbalsCount=0`. KSP's `CrewRoster` then starts empty for that agency — playable but visibly broken; the operator gets exactly what they asked for. No new defensive logic needed beyond the existing Phase 6.3 backfill-on-load.

### Resource template / Resources.*_Kerman path

Phase 6.4 doesn't touch `Resources.*_Kerman`. Reads come from disk, not the embedded resources. Phase 6.3 already seeded the disk copy. Phase 6.4's read is a pure passthrough.

---

## Test plan

| Surface | Tests | Coverage |
|---|---|---|
| Helper `ResolveKerbalsPathForRequester` | 6 ServerTest cases (gate states + fallback branches) | All 4 branches |
| End-to-end `HandleKerbalsRequest` flow | 3 MockClientTest cases (distinct-roster + legacy parity + missing-mapping fallback) | Wire-level round-trip |
| ForkBuildInfo string presence | Existing fork banner tests catch any malformed entry | n/a |

**Expected delta:** ServerTest 693 → 699 (+6 helper cases), MockClientTest existing count + 3.

---

## Rollback

Single-commit; revert removes all four edits (KerbalSystem, ServerTest, MockClientTest, ForkBuildInfo). No persisted state changes — kerbal files on disk are read by both gates identically (the gate only chooses which directory to read FROM). Per-agency subdirs from Phase 6.3 remain in place; legacy `Universe/Kerbals/` remains in place. Operators who never opted into `PerAgencyKerbalRoster=true` see zero observable difference; operators who did opt in see a one-time return to the legacy roster on their next KerbalsRequest.

---

## Decisions

- **Pure helper takes `string playerName`, not `ClientStructure`.** Unit testability without NetConnection mocking. Same shape as Phase 6.3's `SeedStockKerbalsForAgency(AgencyState)`.
- **Both fallback branches log Warning at the same level.** Operator visibility for two distinct misconfigurations: torn `AgencyByPlayerName` (defensive code path) vs missing per-agency subdir (filesystem corruption / hand-delete). Each Warning includes the path string so operator can diagnose without server restart.
- **Try/catch around `GetFilesInPath` in `HandleKerbalsRequest` body.** Closes the `/deleteagency` mid-read race. Empty-reply fallback prevents the receive thread from crashing on a fast operator workflow.
- **No fallback when gate=on subdir is present but empty.** Empty array → empty reply. Operator-visible-but-recoverable (operator can manually re-seed by deleting the empty subdir and bouncing the server, which triggers Phase 6.3 load-backfill).
