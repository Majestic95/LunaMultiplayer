# BUG-045 — Breaking Ground deployable science vanishes on reconnect

**Phase-2 analysis. Status: Fixed (Phase B.1, 2026-05-16).** Ported from upstream `Release/0_29_2:2526e15a` (Drew Banyai, 2026-05-05).

Upstream tracker: [#308](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/308). 22 reactions, 7 comments — the most-thumbsed open issue in the LMP tracker at time of fix.

## Repro

1. Player A is on EVA with a Breaking Ground science kit in inventory.
2. Player A deploys the central station, then any number of science part (thermometer, weather station, etc.). Each placed part spawns its own KSP `Vessel` of type `DeployedScienceController` (for the central station) or `DeployedSciencePart` (for each leaf sensor).
3. Vessels appear in the tracking station for Player A. Experiments collect data; the deployed array works correctly while the session is live.
4. Player A disconnects.
5. Server restarts (or any new client connects fresh).
6. **The deployable array is gone.** Vessels not in the tracking station. No `Universe/Vessels/<guid>.txt` ever existed for them.

## Root cause

`LmpClient/Systems/VesselEvaEditorSys/VesselEvaEditorEvents.VesselCreated` is bound to `GameEvents.onNewVesselCreated` and is the only path on the client that:
1. Acquires the `UpdateLock` + `UnloadedUpdateLock` for a newly-spawned vessel.
2. Calls `VesselProtoSystem.MessageSender.SendVesselMessage` to transmit the proto to the server.

Pre-fix code (whole method):

```csharp
public void VesselCreated(Vessel vessel)
{
    if (System.DetachingPart)
    {
        LockSystem.Singleton.AcquireUpdateLock(vessel.id, true, true);
        LockSystem.Singleton.AcquireUnloadedUpdateLock(vessel.id, true, true);
        VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel);
    }
}
```

`System.DetachingPart` (a bool on `VesselEvaEditorSystem`) is toggled by `EVAConstructionEvent.onDroppingPart` / `onDroppedPart`. It is set ONLY for the EVA Construction Mode part-drop flow — the player attaches new parts to an existing vessel via the EVA construction UI.

Breaking Ground deployable science **does not raise any `EVAConstructionEvent`**. The deployable kit is placed via a separate kerbal action that calls into Squad's deployable-science codepath. KSP still fires `GameEvents.onNewVesselCreated` for each spawned vessel, but `DetachingPart` is false, so the entire `if` body skips. Result:

- `SendVesselMessage` never fires → server is never told the vessel exists → no `Universe/Vessels/<guid>.txt` is ever written.
- The `VesselLockSystem` periodic bulk-lock pass DOES still acquire `UnloadedUpdate` locks for the new vessels (because they're in `FlightGlobals.Vessels`), leaving **orphan locks on the server** with no matching vessel record. The orphans live until the leaving player's subspace tears down.

When Player A reconnects (or any client connects after a server restart), the universe-on-disk has no record of the deployable array. KSP loads what the server hands it; nothing arrives; the array is gone.

## Fix

`LmpClient/Systems/VesselEvaEditorSys/VesselEvaEditorEvents.VesselCreated` is rewritten to gate on either flag:

```csharp
var isEvaConstructionDrop = System.DetachingPart;
var isDeployableScience = vessel.vesselType == VesselType.DeployedSciencePart
                       || vessel.vesselType == VesselType.DeployedScienceController;
if (!isEvaConstructionDrop && !isDeployableScience) return;
```

A `null` / `IsSpectating` guard is added at the top — both because spectating clients should never originate vessel sends, and because `onNewVesselCreated` can fire with a half-instantiated vessel during scene transitions.

A `LunaLog.Log` line tagged `[fix:BUG-045]` distinguishes which branch fired, matching the fork's `[fix:BUG-XXX]` log-grep convention.

The receive path, lock arbitration, server-side persistence (`VesselDataUpdater.RawConfigNodeInsertOrUpdate`), scenario sync, and dekessler are **all unchanged** — those layers handle the deployable `vesselType` values correctly today; only the originating-side gate was wrong. Backward-compatible with unmodified servers and unmodified peer clients (they remain passive receivers and ingest the deployable vessel like any other proto-vessel).

## Risk

- **Both flags true simultaneously:** A deployable spawned during EVA Construction Mode would set both `DetachingPart` and one of the science vesselType values. The new logic uses `||` and a single send path, so the vessel still fires `SendVesselMessage` exactly once. No double-send risk.
- **Future Breaking Ground vessel types:** The current KSP enum has `DeployedSciencePart` + `DeployedScienceController` + `DeployedGroundPart` (the last is for the ground anchor). The fix gate covers the first two. The third (`DeployedGroundPart`) is anchor-only and rarely user-visible, but if a future KSP DLC adds another deployable subtype, the gate would miss it. Low immediate risk; revisit if the deployable family expands.
- **0-part deployable mid-spawn:** Race between `onNewVesselCreated` firing and KSP finishing part instantiation. Existing `SendVesselMessage` path tolerates this (the asteroid-spawn flow is similar). No change needed.
- **Stale subspace stamping:** If the placing kerbal is in a subspace behind the server (BUG-001 / BUG-005/006 territory), the deployable's `lmpAuthSubspace` stamp will be the player's subspace, which is correct per the existing initiator-wins rule. Out of scope for BUG-045.
- **Existing orphan locks server-side:** Players who ran pre-fix sessions have orphan `UnloadedUpdate` locks on the server from deployables that were never persisted. No cleanup needed — they expire on subspace teardown. No migration shim shipped.

## Verification

- `dotnet build LmpClient/LmpClient.csproj -c Release` clean (no new warnings beyond the 30 pre-existing).
- `dotnet build Server/Server.csproj -c Release` clean.
- `dotnet test ServerTest/ServerTest.csproj`, `LmpCommonTest/LmpCommonTest.csproj`, `MockClientTest/MockClientTest.csproj`, `LmpClientTest/LmpClientTest.csproj` all pass.
- **Manual KSP verification (still TODO):** Two-player session, both with Making History + Breaking Ground. Player A places a central station + two science parts. Both players see them in the tracking station. Player A disconnects. Restart server. Both players reconnect — deployable array persists.
- **Mock-harness regression test:** Deferred to optional Phase B.4. Requires extending `MockNetClient` to send a synthesised `VesselProtoMsgData` with `vesselType=DeployedSciencePart`, then asserting the server's `VesselStoreSystem.CurrentVessels` contains the new guid. Feasible but ~half-day of harness extension work.

## Provenance

| Item | Source |
|---|---|
| Original fix | upstream `Release/0_29_2:2526e15a` "Fix: send Breaking Ground deployable science vessels to server" (Drew Banyai, 2026-05-05) |
| Adaptation | Dropped `SendVesselMessage(reason: "...")` arguments — that overload lives on the `VesselSyncLog` infrastructure landed in Phase B.3. Retrofitted in B.3. |
| Branch | `fix/bug-045-and-vesselsynclog` (squash-merged to master). |
| Plan | [docs/strategy-b-implementation-plan.md](../../strategy-b-implementation-plan.md) Phase B.1. |
| Active fixes registry | [Server/ForkBuildInfo.cs](../../../Server/ForkBuildInfo.cs) — `BUG-045` appended in commit-chronological order. |
