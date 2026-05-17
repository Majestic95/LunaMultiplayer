# BUG-023 — Astronaut Complex desyncs with assigned kerbals, breaks hiring

**Phase-2 analysis. Status: Fixed (2026-05-17, session 9), ported from upstream `Release/0_29_2` (Drew Banyai, commits `d3223931` + `138c2b3e`).**

Upstream tracker: [#576](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/576), [#603](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/603). Critical-no-in-game-recovery symptom: the Astronaut Complex (and the Tracking-Station info pane that shares its layout code) reports a kerbal "does not exist in the astronaut complex" for a craft that visibly has them aboard. Hire flow is blocked because the AC's roster view is in an inconsistent state. The reporter's workaround — "terminate the craft" — removes the offending kerbal+vessel pair from the world, which clears the symptom by side effect.

## Repro

Indirect, depends on the timing of `KerbalProto` vs `VesselProto` arrival on the receiving client:

1. Player A creates a crewed vessel V with kerbal K (e.g. Jeb), launches it. A's client broadcasts `KerbalProto` for K, then `VesselProto` for V.
2. Player B is connected. Lidgren delivery on `KerbalCliMsg` (channel 16) and `VesselCliMsg` (channel 8) are reliable-ordered **per channel**, so cross-channel order is not guaranteed. `VesselProto` for V can arrive at B before `KerbalProto` for K.
3. B's `KerbalSystem.KerbalsToProcess` enqueues K but does not merge into `HighLogic.CurrentGame.CrewRoster` until the next `KerbalSystem.LoadKerbals` routine tick (~1 second cadence).
4. B's `VesselProtoSystem.CheckVesselsToLoad` (faster routine) picks up V before that tick fires. `vesselProto.Load(...)` runs. Stock KSP's `ProtoPartSnapshot` ConfigNode constructor resolves each `crew = NAME` value against the **current** `HighLogic.CurrentGame.CrewRoster` — K isn't there yet, so the resolver logs `"[Protocrewmember]: Instance of crewmember Jeb in part X on Y does not exist in the roster"` and **appends a `null` placeholder to `ProtoPartSnapshot.protoModuleCrew`**.
5. The null placeholder sits dormant on the proto. KerbalSystem's tick later merges K into the roster; the part's crew slot now has a fully-named kerbal in the roster BUT a `null` reference inside the proto's `protoModuleCrew` list. There is no automatic reconciliation.
6. The user clicks the vessel in the Tracking Station: `KbApp_VesselCrew.CreateVesselCrewList` walks `protoModuleCrew`, sorts via `KbApp_VesselCrew.CompareSeatIdx` (which dereferences `seatIdx` on each element), NREs on the null entry, and `List<T>.Sort` rethrows as `InvalidOperationException: Failed to compare two elements in the array`. The handler swallows the throw but the info pane is left half-built. AC pane shows the same symptom — same surface code.
7. If the player tries to fly V: `Part.RegisterCrew` dereferences `protoModuleCrew[i]` and NREs out of `FlightDriver.Start`. Black flight scene + every-FixedUpdate NRE in `ModuleCommand.UpdateControlSourceState`.

The repro is rare in the steady state (KerbalProto usually beats VesselProto for the same kerbal/vessel because client-side `KerbalEvents.StatusChange` fires immediately on hire/EVA and the message ships before the vessel changes), but reliable during the **initial-sync burst** when a fresh client receives the existing universe's vessels and kerbals as separate streams.

The autosave path then bakes the null in: `Game.Save` after the next physics tick captures the broken `ProtoPartSnapshot` to disk. Even if the Harmony patches scrub the null on the next read, every subsequent `Save+Load` round trip stock KSP does (which fires on the post-Game.Save autosave path) re-introduces the null because the wire-side name still doesn't resolve, propagating the corruption forward indefinitely until the vessel is terminated.

## Root cause

Three independent code surfaces, one underlying defect:

| Layer | Source | What goes wrong |
|---|---|---|
| **Race** | [LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs](../../../LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs) `CheckVesselsToLoad` | Picks up `vesselProto` before [LmpClient/Systems/KerbalSys/KerbalSystem.cs](../../../LmpClient/Systems/KerbalSys/KerbalSystem.cs) `LoadKerbals` (separate ~1s routine) has merged queued `KerbalProto` payloads into `CrewRoster`. `vesselProto.Load(...)` resolves crew names against an incomplete roster → stock appends nulls. |
| **State** | Stock KSP `ProtoPartSnapshot.LoadCrew` | Appends `null` to `protoModuleCrew` when `crew = NAME` doesn't resolve. Logs a warning, no exception. The null persists across `Game.Save` / `Game.Load`. |
| **Symptom** | Stock KSP `KbApp_VesselCrew.CompareSeatIdx` + `Part.RegisterCrew` + `ModuleCommand.UpdateControlSourceState` | Dereference `protoModuleCrew[i]` without null-checks. NRE escapes via List.Sort → frozen UI / black flight scene / FixedUpdate NRE storm. |

The fix has to address all three layers because each is independently sufficient to manifest a different symptom flavour, and the autosave round-trip can re-introduce the null state even after the race window closes.

## Fix design

Three-part fix ported from Drew Banyai's `origin/Release/0_29_2` work — none of it is in upstream `master` so this is greenfield turf for the fork.

### Part 1 — drain queued `KerbalProto` before each vessel-load batch

[LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs](../../../LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs) `CheckVesselsToLoad` now begins each try-block iteration with:

```csharp
if (KerbalSystem.Singleton != null && !KerbalSystem.Singleton.KerbalsToProcess.IsEmpty)
{
    KerbalSystem.Singleton.LoadKerbalsIntoGame();
}
```

Cheap on the steady state — the queue is normally empty, this is one `IsEmpty` check. When the queue is non-empty, `LoadKerbalsIntoGame()` (a public method that already exists for the same purpose at game-start) runs `ProcessKerbalQueue()` synchronously, merging every queued kerbal into `CrewRoster` before any vessel proto attempts to resolve a crew name.

Closes the receiving-side timing race. Cannot cover the case where the `KerbalProto` arrives AFTER the `CheckVesselsToLoad` tick we're currently in — the load-time scrub (Part 2) is the safety net for that.

### Part 2 — strip nulls from `protoModuleCrew` at vessel-load time

[LmpClient/VesselUtilities/VesselLoader.cs](../../../LmpClient/VesselUtilities/VesselLoader.cs) gains a new `ScrubInvalidProtoCrew(ProtoVessel)` method, called inside `LoadVesselIntoGame` immediately after `vesselProto.Load(...)` returns and before the existing module-sanity validation.

The scrub walks each `ProtoPartSnapshot.protoModuleCrew` and removes null entries. **Critically, it removes the matched `protoCrewNames` slot in lockstep**: stock `KerbalRoster.ValidateAssignments(Game)` walks `protoModuleCrew` and `protoCrewNames` by the same index `i`, and on the missing-from-roster branch calls `SystemUtilities.ExpungeKerbal(protoModuleCrew[i])`. If we stripped nulls from `protoModuleCrew` alone, indices would shift and a subsequent validation pass — fired on every game/scene load by the autosave round-trip — would call `ExpungeKerbal` on an unrelated real kerbal that now lives at the shifted index. That would silently delete real crew. Lockstep removal preserves the parallel-list invariant.

### Part 3 — Harmony patches as defense-in-depth for the autosave round-trip

[LmpClient/Harmony/Part_RegisterCrew.cs](../../../LmpClient/Harmony/Part_RegisterCrew.cs) (prefix) and [LmpClient/Harmony/KnowledgeBase_GetVesselCrewByAvailablePart.cs](../../../LmpClient/Harmony/KnowledgeBase_GetVesselCrewByAvailablePart.cs) (postfix) strip nulls in place right before stock KSP dereferences them.

These cover the case where stock re-introduces nulls through a `Game.Save` → `Game.Load` round trip triggered by autosave (`Flight State Captured`). The Part 2 scrub runs once at LMP's `LoadVesselIntoGame`; if the proto is later re-deserialised from a save file, the wire-side crew name still doesn't resolve (kerbal was never replicated locally, or was replicated under a slightly different name) and the null reappears. The Harmony patches catch it on the next dereference — strip in place, log once, let stock proceed.

Both patches are ungated (no `if (NetworkState >= Connected)` check) because stripping a genuine null from a crew list is strict-improvement behaviour even in single-player. Both are try/catch-wrapped so a scrub-side throw cannot break the stock UI flow.

In-place mutation also cleans the underlying `ProtoPartSnapshot.protoModuleCrew` for downstream consumers (lab transfers, crew enumeration), because stock `ProtoPartSnapshot.ConfigurePart` assigns the snapshot's list reference directly into `Part.protoModuleCrew`.

## Test plan

There is **no harness-reachable regression test for this fix**. The bug surface is KSP-internal:
- The `protoModuleCrew` null injection happens inside stock KSP's `ProtoPartSnapshot` ConfigNode constructor — unreachable without `Assembly-CSharp.dll`.
- The NRE consumers (`KbApp_VesselCrew.CompareSeatIdx`, `Part.RegisterCrew`) are KSP UI / vessel-lifecycle types that have no in-process surface outside the running game.
- The `MockClientTest` harness has no KSP; the existing tests assert wire-level behaviour only.

Validation paths:
1. **Build + existing tests stay green.** Both Server.dll and LmpClient.dll compile clean; all 12 MockClientTest + 87 ServerTest tests pass after the changes.
2. **Boot-banner advertises the fix.** `Server/ForkBuildInfo.cs:ActiveFixes[]` gains `"BUG-023"` and the boot log line confirms.
3. **Runtime log markers grep-discoverable.** All three pieces log `[LMP][fix:BUG-023]` warnings ONLY when they actually scrub something — operators can grep `KSP.log` to confirm the patches are firing in real sessions.
4. **In-game soak validation.** Stage 5 follow-up: have two players cycle through career mode with crewed-vessel cadence, watch `KSP.log` for `[fix:BUG-023]` markers and absence of the `InvalidOperationException: Failed to compare two elements in the array` / `Part.RegisterCrew` NRE. Soak target: one week of mixed sessions with zero kerbal-desync reports.

## Risks and known limitations

1. **`ScrubInvalidProtoCrew` runs on every successful vessel load.** On a clean proto this is one `IsEmpty`-style walk per `protoPartSnapshot` returning zero removals — microsecond-scale cost. Cumulative impact on the load-budget already enforced by `MaxExpensiveReloadsPerTick`: negligible.

2. **Crew-name resolution failures are still LOGGED by stock KSP.** Stock's `"[Protocrewmember]: Instance of crewmember <NAME> in part X on Y does not exist in the roster"` warning fires BEFORE our scrub gets a chance to run — KSP.log will continue to show those lines for the same wire-side conditions. Our `[fix:BUG-023]` warnings document the recovery; the stock warnings document the original race. Both are useful for post-mortem analysis.

3. **The Part-3 Harmony patches don't prevent autosave from persisting the null.** If a session experiences the race, the `protoModuleCrew[i] = null` state can survive in the save file. On every subsequent load (single-player or multiplayer), the Harmony patches re-scrub at the dereference point — but the on-disk save still carries the bad data until something rewrites it. This is acceptable because (a) the symptom is suppressed regardless and (b) any future LMP version that learns to reconcile resolved-but-late kerbals can update the saved proto.

4. **Per-agency Stage 5 implications.** When per-agency career arrives, each agency has its own roster slice. The cross-resolution problem may expand: a vessel from agency A with crew K can land at a client viewing agency B's roster, where K is invisible. The Stage 5 roster model needs to either (a) replicate all rosters to all clients (today's behaviour), (b) replicate only the agency-owning client's roster slice and rely on the same scrub-and-tolerate pattern when cross-agency vessels render, or (c) per-agency roster lookup keyed by the vessel's owning agency. The fix shipped today is forward-compatible with all three.

5. **Hostile/malformed wire payloads.** A peer that sends a vessel proto with deliberately-bad crew names (e.g. all-empty string) will still trip stock KSP's logger but the scrubs all run successfully. No code path now fails-loud on this kind of malformed payload — that's actually correct behaviour given LMP's "trust the wire" model, but a security-hardening pass could add reject-on-bad-crew-names as a defensive measure.

## Cross-cutting effects

- Touches `LmpClient/` only (Part 1 + Part 2 + Part 3). No `LmpCommon/` or `Server/` code changes beyond `ForkBuildInfo.cs` (string append).
- No wire change. No protocol bump.
- Three new files: two Harmony patches + the `ScrubInvalidProtoCrew` method body inside an existing file.
- `Server/ForkBuildInfo.cs` `ActiveFixes[]` gains `"BUG-023"`.
- All three pieces log `[LMP][fix:BUG-023]` markers when they fire.
- Closure of BUG-023 brings the campaign top-10 to **0 remaining open**. Stages 3 and 4 close after Stage 4.10 client-internal regression tests land.

## Porting provenance

| Drew commit | What it ported as | Notes |
|---|---|---|
| `d3223931` | `LmpClient/Harmony/Part_RegisterCrew.cs` + `LmpClient/Harmony/KnowledgeBase_GetVesselCrewByAvailablePart.cs` + `LmpClient.csproj` registration | Cherry-picked verbatim (clean apply — no conflicts). |
| `138c2b3e` | `ScrubInvalidProtoCrew` method body + invocation site in [LmpClient/VesselUtilities/VesselLoader.cs](../../../LmpClient/VesselUtilities/VesselLoader.cs); `KerbalsToProcess` drain in [LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs](../../../LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs) `CheckVesselsToLoad` | Manual backport. Drew's `LogPostLoadVesselSanity` / `WalkVesselForCorruption` helpers don't exist in our master, so the corruption-detection extension half of `138c2b3e` was not ported — only the scrub itself + its invocation. The scrub invocation moved from right-after-`vesselProto.Load(...)` (inside the try-block, same line) to the same conceptual spot in our master's structurally-different post-load validation. |

The Strategy B lesson applies again ([[project-strategy-b-phasing]]): cherry-picking Drew's full commits often fails because his commits reference symbols from earlier commits in his branch that we never adopted. Manual backport of the load-bearing portions plus careful integration with our master's post-load validation structure is the working pattern.
