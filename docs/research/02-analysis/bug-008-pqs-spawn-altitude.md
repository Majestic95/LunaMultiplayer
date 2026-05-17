# BUG-008 — Polygons scramble and craft teleport underground on spawn

**Phase-2 analysis. Status: open. Picked from the post-Stage-2 top-10.**

## Repro (from upstream issue [#279](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/279) and the DMP ancestor)

> "Whenever I spawn I get shot under the ground and every polygon in render
> range is randomly scrambled … the vehicle just stops in place and a bunch
> of parts vanish all at once before it explodes."

Reliably reproduces when:

1. Player A places a landed vessel on Kerbin (e.g., a runway base or a
   parked rover).
2. Player B connects, scene loads, the vessel proto arrives, KSP places
   it from the proto's stored `latitude` / `longitude` / `altitude`.
3. The local terrain at that lat/lng is **not yet streamed in** at the
   highest-detail PQS level — KSP positioned the vessel relative to a
   lower-LOD spherical approximation, but render-time uses the
   highest-LOD mesh. The two disagree by metres to tens of metres.
4. Once the PQS streams in, the high-LOD terrain intersects the vessel
   geometry; KSP's collider resolution explodes parts and tears polygons.

DMP-era root-cause documentation: `godarklight/DarkMultiPlayer#373` flags
this as the "PQSAltitude" problem — "PQSTerrain does not seem to spawn
accurately enough for our needs." LMP inherited the same spawn-altitude
logic (see decision log below) and has carried the bug since.

Same family of symptoms appears in [BUG-009] ("vessel explodes / terrain
shakes for no reason on the runway") and in some [BUG-021] reports
("Stratolauncher crumples on launch"). Closing BUG-008 by addressing
the underlying spawn-time terrain race retires those symptom classes as
well.

## Code path on current master

Client-side ingest:

```
VesselProtoSystem.CheckVesselsToLoad         // LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs:183
  -> VesselLoader.LoadVessel(protoVessel)    // LmpClient/VesselUtilities/VesselLoader.cs:14
       -> protoVessel.Validate(true)         // KSP-side
       -> LoadVesselIntoGame(protoVessel)
            -> SanitizePersistentIds(protoVessel)
            -> vesselProto.Load(HighLogic.CurrentGame.flightState)   // KSP-side; LMP hands off here
```

KSP's `ProtoVessel.Load` walks the proto and constructs the live `Vessel`
using the stored `latitude` / `longitude` / `altitude` to place it.
**LMP never adjusts altitude after the load** — it trusts that the
proto's stored altitude is consistent with the local PQS at the moment
of load. That's the load-bearing wrong assumption.

There is no PQS-aware post-load step in LMP today. The audit found:

- `Systems/SafetyBubble/SpawnPointLocation.cs` uses
  `Body.GetWorldSurfacePosition(lat, lng, alt)` for its own purposes
  (spawn-point bookkeeping) but does not feed back into proto-load.
- No call to `vessel.PQSAltitude`, `Body.pqsController.GetSurfaceHeight`,
  `Body.TerrainAltitude`, `body.GetAltitude`, or any PQS readiness check
  exists under `LmpClient/Systems/VesselProtoSys` or
  `LmpClient/VesselUtilities`.

So the gap is real, not a "we already do this and it has a bug" — we
don't do it at all.

## Why upstream PRs didn't fix this

[#608](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/608)
validates orbit/body indices and blocks bad messages (covers
[BUG-011] variant 1). [#628](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/628)
fixes interpolation rotation (covers [BUG-014]). [#633](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/633)
/ [#649](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/649)
zero throttle on lock acquisition (covers [BUG-015]). None of these
touch spawn-altitude vs PQS readiness.

AdmiralRadish's active-areas log (docking, coupling, scenario sync, lock
handoff) hasn't included a spawn-flow PR since the revival. Greenfield
turf for the fork.

## Proposed fix design

**Two-phase approach, ship in this order:**

### Phase A — block the spawn until PQS is ready (defensive)

After `vesselProto.Load` returns, the new `Vessel` reference is
available via `protoVessel.vesselRef`. Before LMP fires
`VesselLoadEvent.onLmpVesselLoaded` (the signal the rest of the systems
listen on to start processing the vessel):

1. Query `vessel.mainBody.pqsController` for the surface height at
   `vessel.latitude` / `vessel.longitude`:
   `pqs.GetSurfaceHeight(pqs.GetRelativePosition(latitude, longitude))`.
2. Compare against the value KSP placed the vessel at
   (`vessel.altitude - vessel.terrainAltitude` after Load).
3. If the absolute delta exceeds a small threshold (suggest 1.0 m), the
   vessel is mis-placed; queue a re-position pass. Don't fire the load
   event yet.
4. The re-position pass runs on a `RoutineExecution.Update` coroutine
   that polls `vessel.PQSAltitude` until it stabilises (two consecutive
   ticks within 0.1 m). Once stable, snap the vessel to
   `pqs surface height + (proto altitude - proto terrainAltitude)`,
   THEN fire the load event.

This is conservative — it doesn't change the proto's stored altitude or
any wire payload; it only adjusts the local Unity placement.

### Phase B — record terrainAltitude in the proto (load-time correctness)

Phase A is reactive. The deeper fix records the PQS-resolved
`terrainAltitude` on the server's stored proto so that future loads
have a ground-truth offset. This is server-side work plus a wire
addition; deferring to Phase B until Phase A is soak-validated.

If Phase A proves sufficient in the wild we may not need Phase B at
all — the per-client re-positioning step is cheap.

## Decision points (Q1–Q4)

To be answered before any code in either phase. Resolve in the
existing `bug-008-pqs-spawn-altitude.md` followup or inline below as
the answers are pinned down.

- **Q1: Threshold for "needs re-positioning".** Suggest 1.0 m absolute
  PQS-vs-stored-altitude delta. Smaller catches near-correct cases and
  fires the routine constantly; larger lets visibly-buried vessels
  through. 1.0 m is the smallest value the actual repro reports (the
  bug typically presents at 5–50 m).
- **Q2: How long is "PQS is ready"?** Maximum 5 seconds polling; after
  that, place the vessel at the queried altitude anyway and log a
  `[fix:BUG-008]` warning. Operators should see the warning to know
  Phase B may be needed for that body / lat-lng.
- **Q3: Should the routine run for in-flight ProtoUpdate too?** No.
  In-flight updates carry an authoritative altitude from the player who
  owns the vessel; only spawn-from-stored-proto needs the local re-check.
  Add a flag on `VesselProto` (something like `RequirePqsAlignment`,
  default true for first load, false for subsequent updates).
- **Q4: Where does the re-positioning logic live?** Suggest a new
  `LmpClient/VesselUtilities/PqsAlignmentRoutine.cs` that owns the
  poll loop, invoked from `VesselLoader.LoadVesselIntoGame` after
  `vesselProto.Load` returns. Keeps `VesselLoader` flat.

## Test plan

Harness-only tests can't reach the KSP PQS subsystem (no terrain
rendering in `MockClientTest`). The verification ladder is:

1. **Unit test** — extract the threshold check + queue decision into a
   pure function (`PqsAlignmentDecision.NeedsRealignment(stored, pqs,
   threshold)`) and exhaustively test it in `LmpClientTest` (the
   prospective net472 sister project; not yet created — flagged in
   CLAUDE.md Known Limitations).
2. **In-KSP repro** — load a session with the existing repro vessel
   from issue [#279](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/279)
   (or fabricate one: stock plane parked on Kerbin runway, save proto
   server-side, then have a fresh client connect and observe spawn).
3. **Soak** — operators on the dev server confirm the
   `[fix:BUG-008]` log lines fire on plausible vessels for a week
   without false positives.

## Followup tickets

- **BUG-008-A** (this Phase A scope): poll PQS, re-position, fire load
  event. ~200 LOC of client work. Touch `VesselLoader.cs` +
  `VesselProtoSystem.cs` + new `PqsAlignmentRoutine.cs`.
- **BUG-008-B** (Phase B, deferred): record `terrainAltitude` on server
  proto, transmit, use as ground truth. Wire change → protocol-version
  consideration.
- **BUG-009** review after Phase A ships — likely retires.
- **BUG-021** review after Phase A ships — likely retires for the
  on-runway variant.

## Risk notes

- KSP's PQS controller API has changed at least once between KSP 1.10
  and 1.12. Pin against the targeting pack we already build for
  (`net472` + KSP 1.12.5 DLLs in `External/KSPLibraries/`).
- The polling routine adds latency to first-spawn for a vessel; the
  visible "vessel pops in" delay is the user-facing tradeoff for not
  having the polygon-scramble explosion. Budget for ≤1 second of
  perceived delay in the common case (PQS streams fast); the 5-second
  hard cap from Q2 only fires on cold-cache bodies.
- The active vessel takes a different code path (KSP loads it eagerly
  on scene transition rather than via the proto queue). Phase A must
  also guard the active-vessel reload path or the bug will still hit
  the first-spawn-after-reconnect case for the owner. Verify in code.
