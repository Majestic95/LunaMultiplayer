# BUG-010 — Craft explodes on disconnect / game exit when within rendering distance of another player

**Phase-2 analysis. Status: Open (design walkthrough pending, no fix shipped yet).**

Upstream tracker: [#654](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/654). High-severity silent progress destruction in a very common play pattern (camped-on-a-lake floatplane sessions). Paired in this doc with the dock-then-logoff handoff scenario per [near-term-todos.md](../../near-term-todos.md) item 2 — they share the same missing-machinery root cause.

## Repro

### Variant A — explode on remote disconnect (the loud failure)

1. Player A and Player B are both on the same celestial body, within rendering distance of each other (~2.5 km, the default unpack threshold). Both have water craft beached on a lake. Easiest stock-only repro is the [#654 reporter's setup](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/654): two floatplanes side-by-side on KSC's beach.
2. Player B clicks **Disconnect** in the LMP status window (or kills the KSP process, or yanks their ethernet — all three triggers reproduce).
3. From Player A's perspective: B's floatplane jolts, the joints visibly stress, and within ~1–3 seconds the craft pops apart with the standard "kraken" overstress chain. The pieces sink, taking any persistent state with them — the vessel is gone from the universe.

### Variant B — dock-then-logoff handoff (the quiet failure)

1. Player A undocks from KSC; Player B owns the orbital station they're rendezvousing with.
2. A docks to B's station. KSP merges into a single `ProtoVessel`. Our `HandleVesselCouple` in [Server/Message/VesselMsgReader.cs:204-245](../../../Server/Message/VesselMsgReader.cs) stamps the merged vessel's `AuthoritativeSubspaceId` to A's subspace (initiator-wins, BUG-005/006).
3. A clicks Disconnect.
4. The merged vessel is now in B's physics bubble (they're on it), but no one explicitly owns it and the Control / Update / UnloadedUpdate locks are about to fan out via the same release-and-reacquire path as Variant A. The visible damage is usually smaller than Variant A — the station is mostly rigid and was stable when A left — but B can occasionally see a joint flex, a docking-port part destroyed, or (if the merged vessel has Breaking Ground deployable bits) one of those structures sheared off.
5. Even if nothing visibly breaks, B has to undock the merged vessel to get "their station" back as a separate entity. There's no ownership-handoff machinery on disconnect today that would split or hand off the leaving player's craft separately from the rest of the merged vessel.

## Root cause

LMP enforces "this client integrates physics on this vessel" through the `LockType.Update` + `LockType.UnloadedUpdate` + `LockType.Control` triple and a derived `Vessel.SetImmortal(...)` flip. The relevant code paths:

| Step | File | What it does |
|---|---|---|
| Lidgren disconnect arrival | [Server/Server/LidgrenServer.cs:145-149](../../../Server/Server/LidgrenServer.cs#L145-L149) | `NetIncomingMessageType.StatusChanged` → `NetConnectionStatus.Disconnected` → `ClientConnectionHandler.DisconnectClient(client, reason)`. |
| Disconnect handler | [Server/Client/ClientConnectionHandler.cs:38-50](../../../Server/Client/ClientConnectionHandler.cs#L38-L50) | Releases ALL of the leaving player's locks via `LockSystem.ReleasePlayerLocks(client)`. Each release fans out to all remaining clients via `LockSrvMsg`. Then `WarpSystem.RemoveSubspace(client.Subspace)`. |
| Lock-release reaction | [LmpClient/Systems/VesselLockSys/VesselLockEvents.cs:174-204](../../../LmpClient/Systems/VesselLockSys/VesselLockEvents.cs#L174-L204) | On each remaining client: `LockReleased(Update)` for a loaded vessel → immediately `AcquireUpdateLock(vesselId)`. `LockReleased(UnloadedUpdate)` → immediately `AcquireUnloadedUpdateLock(vesselId)`. |
| Lock-acquire ack | (server) [Server/System/LockSystem.cs:14-55](../../../Server/System/LockSystem.cs#L14-L55) | Server grants and broadcasts. The remaining client now owns Update + UnloadedUpdate on the vessel that was B's. |
| Immortality flip | [LmpClient/Systems/VesselImmortalSys/VesselImmortalEvents.cs:43-49](../../../LmpClient/Systems/VesselImmortalSys/VesselImmortalEvents.cs#L43-L49) → [SetImmortalStateBasedOnLock](../../../LmpClient/Systems/VesselImmortalSys/VesselImmortalSystem.cs#L74-L83) | `isOurs = controlLockBelongsToPlayer \|\| updateLockBelongsToPlayer \|\| !updateLockExists`. After the acquire, `updateLockBelongsToPlayer == true` → `isOurs == true` → `vessel.SetImmortal(false)`. |
| Vessel goes mortal | [LmpClient/Extensions/VesselExtension.cs:167-212](../../../LmpClient/Extensions/VesselExtension.cs#L167-L212) | Turns `PartBuoyancy`, `CollisionEnhancer`, `FlightIntegrator` back on; sets every part's `crashTolerance` back to its design value. |

The leaving player was driving the vessel through `VesselFlightStateSys` and `VesselPositionSys` updates that pinned its pose every few hundred ms. The instant their lock broadcasts fan out:

1. The vessel becomes mortal.
2. No fresh flightstate/position update is arriving from the leaver — they're gone.
3. The local KSP physics integrator takes over a vessel that:
   - May have been mid-flight with a non-zero `ctrlState.mainThrottle` from B's last broadcast.
   - May be on water with buoyancy resolved against B's last position broadcast, which the local craft's reference frame interpolated slightly differently.
   - Has joints that were being held stable by interpolated/extrapolated positions from B, and now have to find their own equilibrium in one physics tick.

For a floatplane on water this is enough to trigger an oscillation in `PartBuoyancy`'s `enabled = true` step — the wing tip dunks, the moment-arm shoves, the joint at the wing root snaps, the chain reaction destroys the craft. For a rigid station in orbit the symptom is mild because there's no buoyancy and no oscillating moment arm — but a stress spike on the docking port is enough to occasionally destroy it (Variant B).

The mechanism is symmetric for clean disconnects (`PauseMenu → Disconnect → MainSystem.DisconnectFromGame`) and ungraceful drops (Lidgren detects timeout → same `StatusChanged → Disconnected` arrival path on the server). The leaving client today calls [ScenarioSystem.SendScenarioModules](../../../LmpClient/MainSystem.cs#L333) before `NetworkConnection.Disconnect("Quit")` — that's a pattern adapted from upstream's `6bb056ff` send-order swap, but it covers scenario state only, not vessels.

### Why the pre-disconnect proto-broadcast doesn't already cover this

The client's `VesselProtoSystem` broadcasts a proto for every locally-owned vessel on a periodic cadence (default ~30s — see [SettingsStructure.VesselDefinitionSendMsInterval]). The most-recent proto on the server is therefore up to 30 seconds stale relative to the leaving player's actual final pose. For a floatplane that was at rest the staleness rarely matters; the position broadcasts (much higher cadence) keep the in-flight pose current.

But the proto path persists state to disk; the position path doesn't update the on-disk vessel file unless the sender is in `WarpContext.LatestSubspace` (see `VesselDataUpdater.WritePositionDataToFile` at [Server/Message/VesselMsgReader.cs:38-40](../../../Server/Message/VesselMsgReader.cs#L38-L40)). A leaving player in a non-latest subspace whose final pose differs significantly from their last proto broadcast will leave a "rewound to last proto" version of the vessel on disk — fine for landed craft, mildly visible for in-flight craft, and Variant B's dock-then-undock case is the only one where this stale-on-disk pose actually matters because P2 will undock the merged vessel and the child gets created from the on-disk proto.

The dock-then-logoff Variant B compound problem is therefore: (a) the explode-on-disconnect mechanism from Variant A applies to the merged vessel too, AND (b) when P2 eventually undocks, the child vessel is reconstructed from a possibly-stale proto.

## Fix design (two-part, ship in order)

### Part A — server-detect-disconnect → broadcast "pin" → remaining client holds vessel immortal

The smaller, lower-risk fix that addresses both Variant A and the loud half of Variant B. Ship first.

The framing word is **pinned**, not "abandoned": the original pilot may reconnect at any time, and "pin" carries the right "held in place until they return" semantic without implying the vessel is up for grabs.

1. **Server (in `ClientConnectionHandler.DisconnectClient`, before `ReleasePlayerLocks`):**
   - Enumerate the leaving client's `Control` + `Update` + `UnloadedUpdate` locks: `LockSystem.LockQuery.GetAllPlayerLocks(client.PlayerName).Where(l => isVesselScopedLockType(l.Type)).Select(l => l.VesselId).Distinct()`.
   - For each vessel id, broadcast a new wire message `VesselPinnedMsgData` (adds `VesselMessageType.Pinned = 15` — additive enum change, no protocol bump). Payload carries `VesselId` + `AbsentPlayerName` (the leaving player) + `Reason` (free-form string for the diagnostic trace, e.g. `"client disconnected"`).
   - Then proceed with `ReleasePlayerLocks` as today — order matters: clients should see the Pinned message before the lock-release storm so the upstream `SetImmortalStateBasedOnLock` flip is suppressed in time.
   - Server-side log: `[fix:BUG-010] Vessel {vesselId} pinned until {playerName} returns`.

2. **Remaining client (new `LmpClient/Systems/VesselPinnedSys`):**
   - Find vessel by id. If not in `FlightGlobals.Vessels`, ignore (it'll be reloaded fresh on next proto-sync).
   - Maintain a `ConcurrentDictionary<Guid, string> _pinnedVessels` mapping vessel id → absent player name.
   - On message: add to dict, force `vessel.SetImmortal(true)`, log `[fix:BUG-010] Vessel {vesselId} pinned until {playerName} returns`, optionally surface a `LunaScreenMsg.PostScreenMessage($"{playerName}'s craft pinned — will resume on reconnect")`.
   - Patch `VesselImmortalSystem.SetImmortalStateBasedOnLock` to early-return-immortal when the vessel is in the pinned dictionary.

3. **Pin clearance — one unified rule:** when ANY player (local or remote) takes the Control or Update lock on a pinned vessel, clear the pin and let normal `SetImmortalStateBasedOnLock` take over. This covers both clearance paths with a single rule:
   - **Local player explicitly switches to the vessel** (tracking-station click → `VesselLockEvents.OnVesselChange` → `AcquireControlLock` → round-trip → `LockAcquire` broadcast with `PlayerName == local`) — they've taken responsibility.
   - **Original pilot reconnects** (handshake completes → they re-acquire Control on their vessels via the existing rejoin flow → `LockAcquire` broadcast with `PlayerName == AbsentPlayerName`) — they're back driving it.
   - Implementation: hook `LockEvent.onLockAcquire` from inside `VesselPinnedEvents`. No server-side unpin handshake needed.
   - **We deliberately do NOT** hook `GameEvents.onVesselChange` as an early-unpin path. The local-player vessel-switch fires `onVesselChange` BEFORE the lock-acquire round-trip lands; unpinning at switch time would call `SetImmortalStateBasedOnLock` while the lock is still held server-side by the leaver (or by nobody), flipping the vessel mortal for one or more physics ticks while the leaver's last stressed pose is still settling. Immortal-for-RTT is the safer side of that race.

4. **Wire ordering — VesselPinned shares the lock channel.** The whole pin-and-suppress design depends on the client receiving the `VesselPinned` for a vessel BEFORE the matching `LockRelease` broadcasts. Lidgren's reliable-ordered guarantee is **per-channel**; `LockSrvMsg` rides channel 14 and `VesselSrvMsg` defaults to channel 8, so cross-channel reorder is possible. `VesselSrvMsg.DefaultChannel` is special-cased so the `Pinned` subtype rides channel 14 (sharing `LockSrvMsg`'s reliable-ordered queue). All other vessel-subsystem subtypes keep their channel 8 home.

4. **Existing lock-acquire flow runs unchanged** while a vessel is pinned, but the immortal flip is suppressed. Net effect: the vessel sits inert in the remaining player's scene, indestructible, until someone takes the helm.

### Part B — graceful-disconnect handshake → server commits final-pose proto

The bigger, lower-priority fix that addresses Variant B's stale-undock-child-pose problem and tightens Variant A's persisted state to "actual final moment" rather than "last proto, up to 30 s stale". Ship after Part A is soaked.

1. **Client (in `MainSystem.DisconnectFromGame`, after `SendScenarioModules`, before `NetworkConnection.Disconnect`):**
   - For each vessel the local player holds the Control or Update lock on, build a `VesselProtoMsgData` from the current `ProtoVessel.Save(...)` representation. Use the same path `VesselProtoSystem.MessageSender.SendVesselMessage` uses today, with a new `reason: "graceful-disconnect"` to thread through `VesselSyncDiagnostics` and the wire `Reason` field added in Phase B.3.
   - Wait up to ~2 seconds for server ack (a counter incremented on each `VesselProto` confirmation handler). 2 s is the proposed default — generous enough for a typical 100 ms RTT × handful of vessels, short enough that a user clicking Disconnect doesn't hang noticeably. After timeout, proceed with disconnect anyway (better to lose perfect pose than block the user from quitting).

2. **Server (no new handler — re-use `HandleVesselProto`):** the proto is ingested via the existing path. `VesselDataUpdater.RawConfigNodeInsertOrUpdate` writes the canonical pose to `Universe/Vessels/<guid>.txt`. The Part A "freeze" broadcast then goes out (it doesn't need to wait for the proto to be persisted — they're independent on the remaining client's side).

3. **Server side authority handoff:** when the leaving subspace is about to be torn down by `WarpSystem.RemoveSubspace(client.Subspace)`, the BUG-005/006 guard already refuses to drop a subspace that still holds vessel authority (see `WarpSystem.RemoveSubspace`). The leaving player's subspace will linger as a "ghost" subspace. Two follow-up options, both deferred to a future change:
   - (a) Accept the ghost subspace. Cheap; no behavioural surprise for the remaining player; eventually flushed on server restart.
   - (b) Rewrite every leaving-player-authored vessel's `AuthoritativeSubspaceId` to the remaining client's subspace at handshake time. Cleaner long-term, lets `RemoveSubspace` succeed. The per-agency Stage 5 work will likely subsume this with an `OwningAgency` field anyway, so a deeper rewrite now is wasted effort.

### What we explicitly are NOT doing

- **No protocol bump.** Adding `VesselMessageType.Pinned = 15` is purely additive — old clients that don't know the enum value will fall through their reader's `default` arm. Older clients won't receive the pin broadcast (they'll continue to suffer BUG-010), which is acceptable: there are no 0.30.x-with-fix-A peers in the wild yet at time of writing, so the back-compat surface is empty.
- **No Harmony patch on `Vessel.GoOnRails`.** The existing post-patch in [LmpClient/Harmony/Vessel_GoOnRails.cs](../../../LmpClient/Harmony/Vessel_GoOnRails.cs) is a postfix-only hook for an `onVesselGoneOnRails` event; we don't want to fight KSP's distance check at this stage. If a future revision finds that force-packing near-active-vessel craft is the right approach, that's where the Harmony work would land.
- **No `OwningAgency` field on the vessel.** That's Stage 5 work. The Variant B documentation problem (P2's station identity post-undock) is socially-enforced via the lock system today; a real fix is per-agency career.

## Risk

- **Both players holding overlapping locks (impossible today but worth checking):** lock state is server-authoritative and exclusive per lock type. The `LockQuery.GetAllPlayerLocks(playerName)` enumeration runs on the server's authoritative store at the moment of `DisconnectClient`. No race window.
- **Active vessel of remaining player is one of the pinned vessels (couple scenario):** Variant B is exactly this case. The new pin handler must check `FlightGlobals.ActiveVessel?.id == vesselId` and *not* force-immortal the active vessel — that would lock the remaining player out of physics on their own craft. Instead, the merged vessel is left mortal (because the remaining player is on it) and the pin handler logs a `[fix:BUG-010] skipping active vessel` line for diagnostics. The Part B graceful-handshake fixes the stale-undock-child case for this scenario.
- **Vessel was being destroyed by the leaving player at the exact disconnect frame:** the leaving player's pre-disconnect proto would reflect a `VesselRemoveMsgData` flow, not a proto-save. The current code already handles `VesselContext.RemovedVessels` — the pin handler should defer to that registry (don't pin a vessel that's in the removed list).
- **Re-acquire storm on the remaining client:** if the leaving player held locks on 30 vessels (asteroid hunter, station with many subs, etc.), the lock-release storm is 30 messages followed by 30 acquires from the remaining client. This is no worse than today — we're not adding any extra lock churn, only suppressing the immortal flip downstream.
- **Server-side enumeration cost:** `GetAllPlayerLocks` walks the four lock dictionaries via `Where(...)`. O(total locks) per disconnect. Disconnects are infrequent and the disconnect path is already O(locks) via the existing `ReleasePlayerLocks` loop. Negligible.
- **Plugin compat:** `LmpPluginHandler.FireOnClientDisconnect(client)` is called BEFORE the lock release today. Adding the pin-broadcast between fire and release does not change plugin-visible state.

## Verification

- `dotnet build` clean (no new warnings beyond the 30 pre-existing).
- `dotnet test ServerTest/ServerTest.csproj`, `LmpCommonTest/LmpCommonTest.csproj`, `MockClientTest/MockClientTest.csproj`, `LmpClientTest/LmpClientTest.csproj` all pass.
- **New `MockClientTest` regression** (Stage 4.10 follow-up — Part A only, since Part B requires a client-side proto-build path the harness doesn't yet have):
  - Two mock clients in the same subspace, a planted vessel with the leaving client as Control owner. Mock client #2 connects with a listener for `VesselSrvMsg(VesselPinned)`. Mock client #1 disconnects gracefully. Assert: mock client #2 receives a Pinned message naming the planted vessel id within 500 ms, BEFORE the lock-release messages for that vessel id arrive. Reuses the `Bug005SubspaceRejectTest` pattern of planting a vessel in `VesselStoreSystem.CurrentVessels` from `ServerTest/XmlExampleFiles/Others/`.
- **Manual KSP verification (required for client-side fix — the harness can't drive KSP physics):**
  - Variant A: Two-player session, both with floatplanes on KSC's beach. P2 disconnects. P1's floatplane stays intact, immortal until P1 explicitly switches to it from the tracking station.
  - Variant A drop case: P2 kills the KSP process. Same outcome (server detects timeout the same way as clean disconnect; same broadcast path).
  - Variant B: P1 docks to P2's station. P1 disconnects. P2 sees no joint flex; merged vessel stays stable. P2 undocks; child vessel (P1's craft) reconstructs from on-disk proto. On Part A-only this is the broadcast-stale-proto case; on Part A + Part B the undocked child reflects the actual moment-of-disconnect pose.

## Provenance

| Item | Source |
|---|---|
| Upstream issue | [#654](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/654). No upstream PR. |
| Related (DMP era) | DMP #31 "Add a handover system to another client after docking" (2017). |
| Pre-disconnect send-order precedent | Upstream `6bb056ff` (AdmiralRadish) — scenario sync now sent before disconnect; same pattern as Part B's proposed proto-flush. |
| Initiator-wins couple authority | Our [BUG-005/006 fix](../bug-005-006-cross-subspace-lock.md) sets merged-vessel authority at couple time. The dock-then-logoff Variant B inherits this. |
| `VesselSyncDiagnostics` `Reason` field | Phase B.3 of Strategy B (ported from upstream `Release/0_29_2:4733081d`). Reused as the diagnostic carrier for the new graceful-disconnect proto flush. |
| Active fixes registry | [Server/ForkBuildInfo.cs](../../../Server/ForkBuildInfo.cs) — `BUG-010` will be appended when Part A lands. |
| Plan | This doc + [docs/near-term-todos.md](../../near-term-todos.md) item 1 + [memory: project-stages-3-4-campaign](../../../../C:/Users/austi/.claude/projects/f--luna-multiplayer/memory/project_stages_3_4_campaign.md). |
