# Server-Side Offload Spec — Reducing Client-Incoming Load via Selective Relay

**Branch:** `feature/server-relay-filtering` (worktree `F:\luna-multiplayer-perf`)
**Status:** PRE-IMPLEMENTATION — needs Q-signoff before any code lands
**Base:** `origin/master` @ `b965e05f` (post auto-updater ship)
**Companion to:** [docs/research/10-stage6-per-agency-kerbals-spec.md](10-stage6-per-agency-kerbals-spec.md) (Stage 6 pattern for multi-phase pre-spec)

---

## §0 Premise

The Stage 5 / Stage 6 / WOLF Phase 4 workstreams added per-agency career features. **This spec is orthogonal** — it's pure perf/stability work targeted at reducing client-incoming network volume and per-message decode CPU.

The motivating observation from [docs/research/09-post-incident-systemic-improvements.md](09-post-incident-systemic-improvements.md) + the 2026-05-22 client-side lag report: the LMP server is currently a **naïve broadcast hub**. Every vessel-state relay path in [Server/Message/VesselMsgReader.cs:39-103](../../Server/Message/VesselMsgReader.cs#L39-L103) is `MessageQueuer.RelayMessage<VesselSrvMsg>` (broadcast to all clients except sender), with the only gates being `RejectIfPastSubspace` (drop, not filter) + `RejectIfCrossAgencyWrite` (drop, not filter). No scene awareness, no spatial awareness, no per-vessel cadence shaping.

This catalogs 9 candidate wins. The first three slices (#1 scene-aware filtering / #2 same-body filter / #4 per-vessel cadence by lock holder) ship in this workstream; the other 6 are documented backlog.

**Out of scope across the workstream:** anything that changes game semantics. Every win in this doc is "server makes the same decisions, just sends fewer redundant bytes." If a client wouldn't have rendered a relayed message anyway, suppressing it server-side is a net-neutral correctness change.

---

## §1 Catalog — all 9 wins

Cross-reference for the table from the 2026-05-22 lag analysis conversation.

| # | Win | Impact | Effort | Risk | Slice in this workstream? |
|---|---|---|---|---|---|
| 1 | **Scene-aware relay filtering** | 20-40% incoming bandwidth | ~1 day | Low | **YES — Phase 1** |
| 2 | **Same-body filter** (cull cross-SOI position relays) | 50-70% in interplanetary cohorts | ~3 days | Low | **YES — Phase 2** |
| 3 | Distance-based downsampling within SOI | 40-60% within-SOI | ~1 week | Medium | Deferred (§10) |
| 4 | **Per-vessel cadence by lock holder** | 30-50% on inactive vessels | ~2 days | Low | **YES — Phase 3** |
| 5 | Position/rotation quantization | 40% per position message | ~3 days | Medium (compat break) | Deferred (§10) |
| 6 | Delta encoding against per-client baseline | 60-80% on slow-changing state | 2-3 weeks | High | Deferred (§10) |
| 7 | Compress position-message batches with QuickLZ | 2-3× wire reduction | ~1 week | Medium | Deferred (§10) |
| 8 | Send-side backpressure / load-shed signal | Catastrophic-load tail latency | ~3 days | Medium | Deferred (§10) |
| 9 | Headless physics phantom-client for KSC | Removes a vessel class from broadcast | Months | Very high | Deferred indefinitely (§10) |

---

## §2 Architecture context — what the server already does

Confirmed via direct file reads (research-first per [[feedback-research-first]]):

### What's already filtered
- **Subspace authority** — [VesselMsgReader.cs:123-133](../../Server/Message/VesselMsgReader.cs#L123-L133) `RejectIfPastSubspace`. Drops (does not filter) stale messages from past-subspace senders.
- **Cross-agency write rejection** — [VesselMsgReader.cs:224-245](../../Server/Message/VesselMsgReader.cs#L224-L245) `RejectIfCrossAgencyWrite`. Same drop semantics.
- **Same-agency kerbal relay** — [Server/System/KerbalSystem.cs:362-385](../../Server/System/KerbalSystem.cs#L362-L385) `RelayToSameAgencyClients<KerbalSrvMsg>`. **This is the only existing per-recipient filter pattern**, and it's used only for kerbals.
- **Subspace-bound relay** exists but is unused for vessels — [Server/Server/MessageQueuer.cs:34-40](../../Server/Server/MessageQueuer.cs#L34-L40) `RelayMessageToSubspace<T>` and [:23-29](../../Server/Server/MessageQueuer.cs#L23-L29) `SendMessageToSubspace<T>`. Used by `WarpSystemSender` only.

### What is NOT filtered (every vessel relay)
- [Server/Message/VesselMsgReader.cs:41, 48, 55, 61, 67, 73, 78, 84, 90, 95, 103, 275, 352, 448](../../Server/Message/VesselMsgReader.cs) — every case uses `MessageQueuer.RelayMessage<VesselSrvMsg>` (broadcast to all except sender). No scene/distance/body/lock-holder gate.

### What `ClientStructure` tracks today (and doesn't)
- [Server/Client/ClientStructure.cs](../../Server/Client/ClientStructure.cs) — confirmed fields: `PlayerName`, `Subspace`, `SubspaceRate`, `Authenticated`, `ConnectionStatus`, `HasReceivedInitialVesselsSync`, `PlayerStatus` (free-form `VesselText` + `StatusText` for the Status window — NOT structured scene info).
- **No `CurrentScene` field.** This is the load-bearing gap Win #1 closes.
- `PlayerStatus.VesselText` is operator-display text via [PlayerStatusMsgReader.cs:25-32](../../Server/Message/PlayerStatusMsgReader.cs#L25-L32) — relayed to other clients verbatim, not parsed.

### What the server stores per vessel
- [Server/System/Vessel/VesselStoreSystem.cs](../../Server/System/Vessel/VesselStoreSystem.cs) — `CurrentVessels` `ConcurrentDictionary<Guid, Vessel>`. Each `Vessel` carries `AuthoritativeSubspaceId` + `OwningAgencyId` (Stage 5.16b) + the ConfigNode `MixedCollection<string,string> Fields`.
- **No current-body cache.** The body is in the inbound `VesselPositionMsgData.BodyName` but is NOT cached on the `Vessel` record. Win #2 needs to add this.

---

## §3 Phase 1 — Scene-aware relay filtering (Win #1)

### §3.a Problem statement
Players at MAINMENU / SPACECENTER / EDITOR / RND scene-types currently receive every `VesselPosition` / `VesselFlightstate` / `VesselUpdate` / `VesselResource` / `VesselPartSync*` / `VesselActionGroup` / `VesselFairing` / `VesselDecouple` / `VesselUndock` relay regardless of whether they'd render it. At a 20-player cohort with 5 active flyers each broadcasting at the default 50ms cadence, a player sitting in the VAB receives ~6,000 msgs/sec they have no use for. Net effect: GC pressure + main-thread queue depth on a client that's just trying to build a rocket.

### §3.b Wire surface
**Decision:** New additive `PlayerStatusMessageType.SetScene = 2` on the existing `PlayerStatusCliMsg` channel. **NO protocol bump.**

**Rationale:** Old clients (pre-`feature/server-relay-filtering`) don't send the new message. The server treats absence-of-SetScene as `Scene == Unknown` and **relay-always** for those clients (backwards-compat — they get current behavior). New clients on old servers send the message; the old server's `PlayerStatusMsgReader` doesn't know the enum value, hits the `default` case which currently throws `NotImplementedException` — **THIS IS A BREAKING CHANGE for new-client → old-server.**

→ **Q-signoff item 1:** Two options:
- (a) **Bump protocol 0.31.0 → 0.32.0.** Clean, unambiguous, follows the established `LmpVersioning` discipline. Cuts off cross-version handshakes between v0.32.0 clients and v0.31.x servers.
- (b) **Make server's PlayerStatusMsgReader skip-not-throw on unknown subtype.** Defensive shape; preserves cross-version handshake compat. Risk: silently masks future legitimate "unknown subtype" bugs.
- (c) **Embed scene in `PlayerStatusSetMsgData` as a tail-field with backward-read-compat** (mirror the `PerAgencyCareerEnabled` tail-bit pattern on [SettingsReplyMsgData](../../LmpCommon/Message/Data/Settings/SetingsReplyMsgData.cs)). No new message type; old clients don't write the tail field; server reads tail-or-default. Cleanest no-bump shape.

**Recommendation: (c)** — matches established tail-bit precedent, no protocol bump, no exception-handling debt.

### §3.c New wire field on PlayerStatusInfo
```csharp
public class PlayerStatusInfo
{
    public string PlayerName;
    public string VesselText;
    public string StatusText;
    public ClientSceneType Scene;  // NEW — read tail-or-default Unknown
}
```

`ClientSceneType` enum in `LmpCommon` (new file `LmpCommon/Enums/ClientSceneType.cs`):
```
Unknown = 0   // pre-feature client OR server has not yet received first SetScene
MainMenu = 1
SpaceCenter = 2
TrackingStation = 3
Editor = 4         // VAB / SPH
Flight = 5
ResearchAndDevelopment = 6
Mission = 7        // Making History
Other = 99
```

Tail-bit-read in `PlayerStatusInfo.Deserialize`:
```csharp
if (lidgrenMsg.PositionInBytes < lidgrenMsg.LengthBytes)
    Scene = (ClientSceneType)lidgrenMsg.ReadByte();
else
    Scene = ClientSceneType.Unknown;
```

### §3.d Server-side filter
New `MessageQueuer.RelayMessageToFlightScene<T>` paralleling `RelayMessageToSubspace`:
```csharp
public static void RelayMessageToFlightScene<T>(ClientStructure exceptClient, IMessageData data) where T : class, IServerMessageBase
{
    if (data == null) return;
    if (!OptimizationSettings.SettingsStore.SceneAwareRelayEnabled)
    {
        RelayMessage<T>(exceptClient, data);
        return;
    }
    foreach (var otherClient in ServerContext.Clients.Values.Where(c => !Equals(c, exceptClient) && ShouldReceiveVesselUpdate(c)))
        SendToClient(otherClient, GenerateMessage<T>(data));
}

internal static bool ShouldReceiveVesselUpdate(ClientStructure recipient)
{
    var scene = recipient.PlayerStatus?.Scene ?? ClientSceneType.Unknown;
    // Compat: Unknown means pre-feature client OR initial state pre-first-SetScene.
    // Always relay to them (preserves baseline behavior).
    if (scene == ClientSceneType.Unknown) return true;
    return scene == ClientSceneType.Flight || scene == ClientSceneType.TrackingStation;
}
```

**Why Flight + TrackingStation only:** these are the only scenes that render remote vessel state. SpaceCenter doesn't render vessels (only the KSC buildings + active-vessel marker on the in-scene area, which is a separate code path). Editor / RND / MainMenu / Mission are obviously irrelevant.

→ **Q-signoff item 2:** Should SpaceCenter be included? KSP shows a small "active vessel" indicator on the SC scene background, which uses Position but is the LOCAL active vessel (not a relayed one). My read: SC = drop. Operator override available via gate.

### §3.e Relay-site changes
Replace **9** `MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData)` calls in [VesselMsgReader.cs](../../Server/Message/VesselMsgReader.cs) with `RelayMessageToFlightScene<VesselSrvMsg>` — Position / Flightstate / Update / Resource / PartSyncField / PartSyncUiField / PartSyncCall / ActionGroup / Fairing. (Original spec drafted as "11 sites" — Decouple + Undock were correctly carved out at implementation time per §3.f and the working count is 9.) Proto + Sync + Couple paths stay on full `RelayMessage` because they're catch-up / state-establishing messages that the client must always receive to populate FlightGlobals.Vessels (otherwise the client enters Flight scene with an empty world). Decouple + Undock also stay on `RelayMessage` because they mutate the vessel registry (recipients in non-Flight scenes need to keep their `FlightGlobals.Vessels` consistent for the eventual scene entry into Flight).

**Important: VesselProto stays on full relay.** A client transitioning MainMenu → SpaceCenter → Flight needs to have all the protos already populated in their `FlightGlobals.Vessels` BEFORE entering Flight, because KSP's scene-load instantiates from that collection. Dropping protos to non-Flight clients would mean the moment they enter Flight, their world is empty until the next `VesselSync` round-trip.

Same logic for Remove / Couple / Decouple / Undock — these mutate the vessel registry; clients need them in every scene.

**Position / Flightstate / Update / Resource / PartSync*** / ActionGroup / Fairing — these are continuous-state updates that the recipient discards if they're not rendering the vessel. SAFE to gate on scene.

### §3.f Client-side trigger
[LmpClient/Systems/Status/StatusMessageSender.cs](../../LmpClient/Systems/Status/StatusMessageSender.cs) extended:
```csharp
public void SendCurrentScene(ClientSceneType scene)
{
    var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<PlayerStatusSetMsgData>();
    msgData.PlayerStatus.PlayerName = SettingsSystem.CurrentSettings.PlayerName;
    msgData.PlayerStatus.StatusText = System.MyPlayerStatus.StatusText;
    msgData.PlayerStatus.VesselText = System.MyPlayerStatus.VesselText;
    msgData.PlayerStatus.Scene = scene;
    SendMessage(msgData);
}
```

Hook into `GameEvents.onLevelWasLoaded` in `StatusSystem.OnEnabled` to fire `SendCurrentScene` on every scene transition. Also fire once on handshake completion (current scene at connect-time).

### §3.g Settings — new OptimizationSettings group
New `Server/Settings/Definition/OptimizationSettingsDefinition.cs`:
```csharp
public class OptimizationSettingsDefinition
{
    [XmlComment("Phase 1 of server-relay-filtering. When true, the server drops vessel position/flightstate/update/resource/part-sync/action-group/fairing relays to clients NOT in Flight or TrackingStation scenes. Reduces incoming network volume by 20-40% in mixed cohorts (some players in editor/main-menu/KSC while others fly). Set false if you suspect a regression — restores the pre-Stage-perf baseline broadcast behavior. Default: true.")]
    public bool SceneAwareRelayEnabled { get; set; } = true;

    [XmlComment("Phase 2 — see SameBodyFilter section.")]
    public bool SameBodyFilterEnabled { get; set; } = true;

    [XmlComment("Phase 3 — see UnpilotedVesselCadenceMultiplier section.")]
    public int UnpilotedVesselCadenceMultiplier { get; set; } = 5;
}
```

Per [[feedback-research-first]] + Stage 5.18d precedent on `PerAgencyCareer` (orthogonal-to-difficulty): new group avoids the `SettingsHandler.HasDifferencesAgainstGivenSetting` reflection auto-flipping `GameDifficulty=Custom`. Wired same way as `WebsiteSettings` (new `OptimizationSettings.SettingsStore` static wrapper).

### §3.h Tests
ServerTest (NUnit) new file `ServerTest/SceneAwareRelayTest.cs`:
- `ShouldReceiveVesselUpdate_SceneFlight_True`
- `ShouldReceiveVesselUpdate_SceneTrackingStation_True`
- `ShouldReceiveVesselUpdate_SceneSpaceCenter_False`
- `ShouldReceiveVesselUpdate_SceneEditor_False`
- `ShouldReceiveVesselUpdate_SceneMainMenu_False`
- `ShouldReceiveVesselUpdate_SceneUnknown_True_CompatBaseline`
- `RelayMessageToFlightScene_GateOff_FallsBackToRelayMessageBehavior`
- `RelayMessageToFlightScene_GateOn_DropsToNonFlightRecipients`

MockClientTest end-to-end (`MockClientTest/SceneAwareRelayTest.cs`):
- 3-client harness: Alice in Flight, Bob in Flight, Carol in MainMenu. Alice broadcasts VesselPosition. Bob receives it, Carol does not.
- Compat path: Carol's `PlayerStatusSetMsgData` is sent WITHOUT the new scene field (pre-feature wire shape). Server reads scene=Unknown; Carol still receives Alice's position (compat preserved).
- Scene transition: Carol moves MainMenu → Flight via SendCurrentScene; her next inbound from Alice arrives normally.

### §3.i Acceptance criteria
1. ✅ Pre-feature client connects to new server: receives every relay (compat path).
2. ✅ New client in Flight: receives every relay (positive path).
3. ✅ New client in MainMenu / SpaceCenter / Editor: receives Proto / Sync / Couple / Remove / Decouple / Undock but NOT Position / Flightstate / Update / Resource / PartSync* / ActionGroup / Fairing.
4. ✅ Scene transition Flight → SpaceCenter: client stops receiving filtered relays on next inbound (no client-side state retention needed).
5. ✅ Gate flipped off mid-session: server immediately reverts to current broadcast behavior.
6. ✅ Operator sees a one-shot "scene-aware relay enabled" boot diagnostic via `LunaLog.Normal`.

---

## §4 Phase 2 — Same-body filter (Win #2)

### §4.a Problem statement
A player flying around Kerbin receives every position update for vessels orbiting Jool, Eeloo, Eve, etc. Even with Phase 1 filtering (recipient must be in Flight/TrackingStation), interplanetary vessel relays are wasteful: the recipient's KSP rendering loop is going to immediately discard updates for vessels not in the local SOI.

### §4.b Server-side state additions (implementation diverged from initial spec)

**Vessel-side body resolution** — NO new field needed on `Server/System/Vessel/Classes/Vessel.cs`. The existing `GetOrbitingBodyName()` method reads from `Orbit.body` / `Orbit.IDENT`, and `VesselDataUpdater.WritePositionDataToFile` → `ApplyOrbitIdent` already round-trips the body name from inbound Position into `Orbit.IDENT`. For non-Position relay decisions, the filter falls back to `Vessel.GetOrbitingBodyName()` via the existing eventually-consistent store path (2.5s throttle on WritePositionDataToFile is acceptable for the worst case: "one extra tick of cross-body relay = same as pre-Phase-2 baseline").

**Position-message fast path** — `VesselPositionMsgData.BodyName` is already on the wire (LmpCommon/Message/Data/Vessel/VesselPositionMsgData.cs:14). Read it directly at relay time without any store round-trip. Position is the dominant message volume (50ms cadence) so this fast path covers the majority of filter decisions.

**Per-client active-vessel cache on `ClientStructure`:**
```csharp
public Guid ActiveVesselId { get; set; } = Guid.Empty;
public string ActiveVesselBodyName { get; set; }
```

→ **Q-signoff item 3 (resolved):** Active vessel id derived from **Flightstate** (not Position). Flightstate is by design the local-active-vessel-only path — see [LmpClient/Systems/VesselFlightStateSys/VesselFlightStateSystem.cs:142](../../LmpClient/Systems/VesselFlightStateSys/VesselFlightStateSystem.cs#L142): `MessageSender.SendCurrentFlightState()` only fires for the active vessel. The Position path sends for ACTIVE + SECONDARY vessels and can't reliably distinguish them on the wire. Capturing `ActiveVesselId = data.VesselId` on inbound Flightstate is unambiguous.

`ActiveVesselBodyName` is then updated synchronously on the receive thread inside the Position case when `messageData.VesselId == client.ActiveVesselId`. No race (Lidgren receive runs single-threaded per `LidgrenServer.StartReceivingMessagesAsync`).

### §4.c Filter rule
```csharp
internal static bool ShouldReceiveVesselUpdate_ByBody(ClientStructure recipient, string vesselBodyName)
{
    if (!OptimizationSettings.SettingsStore.SameBodyFilterEnabled) return true;
    if (string.IsNullOrEmpty(vesselBodyName)) return true;          // unknown — be permissive
    if (string.IsNullOrEmpty(recipient.ActiveVesselBodyName)) return true;  // recipient has no active vessel yet — be permissive
    return string.Equals(vesselBodyName, recipient.ActiveVesselBodyName, StringComparison.Ordinal);
}
```

**Compose with Phase 1:** new `RelayMessageToFlightSceneSameBody<T>` walks the recipient list filtering on BOTH gates.

### §4.d Explicit non-handling of SOI hierarchy
**Decision:** same-body-only. Server does NOT know KSP's CelestialBody graph (no parent/child SOI relations available in headless .NET 10 process). A recipient flying around Kerbin will NOT receive position updates for vessels orbiting Mun (Mun is in Kerbin's SOI; visually relevant from low Kerbin orbit).

**Trade-off:** conservative filter — slightly more drops than ideal, but zero risk of stale-cross-body bugs. Operator can disable via `SameBodyFilterEnabled=false`.

→ **Q-signoff item 4:** Acceptable trade-off, or do we want a hardcoded "same-system" allowlist (Kerbin / Mun / Minmus all considered "Kerbin system"; Jool + 5 moons considered "Jool system"; etc.)? Hardcoding breaks under Real Solar System / Outer Planets Mod / GPP / etc. — modded universes need a different solution.

**Recommendation:** ship same-body-only. Defer SOI-aware filtering until an operator reports the Mun-from-Kerbin-orbit visibility regression.

### §4.e Edge cases
1. **Recipient at SpaceCenter (no active vessel):** Phase 1 already drops the relay; Phase 2 is moot.
2. **Recipient just connected, hasn't broadcast first position yet:** `ActiveVesselBodyName == null` → permissive (relay). Self-corrects within first 50ms of player activity.
3. **Vessel changes SOI (transition across Mun's edge into Kerbin's):** server updates `CurrentBodyName` from next inbound Position. One tick of stale filtering possible (negligible).
4. **Player switches active vessel:** server updates `client.ActiveVesselId` + `client.ActiveVesselBodyName` from the next outbound Position they send. Possible 1-tick lag where they get updates for the OLD body. Acceptable.

### §4.f Tests
ServerTest new file `ServerTest/SameBodyFilterTest.cs`:
- `ShouldReceiveVesselUpdate_ByBody_GateOff_True`
- `ShouldReceiveVesselUpdate_ByBody_RecipientNoBody_True_Permissive`
- `ShouldReceiveVesselUpdate_ByBody_VesselNoBody_True_Permissive`
- `ShouldReceiveVesselUpdate_ByBody_BodiesMatch_True`
- `ShouldReceiveVesselUpdate_ByBody_BodiesDiffer_False`
- `ShouldReceiveVesselUpdate_ByBody_CaseSensitivity_Exact` (Kerbin ≠ kerbin per `StringComparison.Ordinal`)

MockClientTest end-to-end (`MockClientTest/SameBodyFilterTest.cs`):
- 3-client harness: Alice + Bob both flying around Kerbin (Bob's recent Position carries BodyName=Kerbin). Carol flying at Jool (BodyName=Jool).
- Alice broadcasts Position. Bob receives, Carol does not.
- Carol broadcasts. Alice + Bob do not receive.

### §4.g Acceptance criteria
1. ✅ Two players at different bodies do not exchange Position relays (under default gate).
2. ✅ Two players at the same body exchange relays normally.
3. ✅ Gate-off restores pre-Phase-2 behavior.
4. ✅ First position broadcast after connect succeeds (permissive null-body path).
5. ✅ SOI transition mid-flight self-corrects within one inbound tick.

---

## §5 Phase 3 — Per-vessel cadence by lock holder (Win #4)

### §5.a Problem statement
Vessels with no active pilot (debris, abandoned satellites, stranded probes) still broadcast at full cadence — either because their controlling player periodically resyncs them as "secondary vessels" ([LmpClient/Systems/VesselPositionSys/VesselPositionSystem.cs:134-151](../../LmpClient/Systems/VesselPositionSys/VesselPositionSystem.cs#L134-L151) at 150ms cadence), or because they had a Control lock that was released without a corresponding cadence drop server-side. Long-running servers accumulate hundreds of these — every relay is bandwidth wasted on state that hasn't meaningfully changed.

### §5.b Server-side state additions
New per-vessel cache on `Vessel` class:
```csharp
public long LastRelayedPositionMs { get; set; }  // milliseconds since ServerContext.ServerClock start
```

Populated by `MessageQueuer.RelayMessageToFlightSceneSameBodyCadence<T>` (the composed Phase 1 + Phase 2 + Phase 3 entry point) after a successful relay.

### §5.c Filter rule
```csharp
internal static bool ShouldRelayPositionMessage(Vessel vessel, long nowMs, int secondaryIntervalMs, int multiplier)
{
    if (multiplier <= 1) return true;  // gate-off / no-throttling
    if (LockSystem.LockQuery.ControlLockExists(vessel.VesselId)) return true;  // active pilot — full cadence
    var minIntervalMs = secondaryIntervalMs * multiplier;
    return (nowMs - vessel.LastRelayedPositionMs) >= minIntervalMs;
}
```

`secondaryIntervalMs` = `IntervalSettings.SettingsStore.SecondaryVesselUpdatesMsInterval` (default 150). At default multiplier=5, throttled-relay interval = 750ms (vs. 150ms baseline) for any vessel with no Control lock.

**Why Control lock not Update lock:** Control lock = "actively piloting" (the player whose game is the active simulator). Update lock = "I'm the one sending state for this vessel" (the player whose KSP holds this vessel in physics range). Update can be held while spectating; Control requires active control. We want active piloting to dictate cadence.

→ **Q-signoff item 5:** Throttle multiplier default. Options: 3 (modest), 5 (recommended), 8 (aggressive), 10 (very aggressive). Higher = more bandwidth savings, more visible "drift" on unpiloted vessels in a player's tracking station. **Recommendation: 5**, paired with operator override for tuning.

### §5.d Scope
**Applies to:** Position relays only.

**Does NOT apply to:** Flightstate, Update, Resource, PartSync*, ActionGroup, Fairing, Decouple, Undock, Couple, Proto, Sync, Remove. These either fire on user-event boundaries (decouple / undock / couple / action-group) or are infrequent already (Update / Resource at 1.5s / 5s baselines).

Phase 3 is narrowly a position-rate-shaping change.

### §5.e Edge cases
1. **Lock acquired mid-stream:** the next inbound Position immediately relays (full cadence resumes). No state-machine flush needed because the filter consults LockSystem live each time.
2. **Lock released mid-stream:** subsequent inbounds are throttled. The throttle clock starts from `LastRelayedPositionMs`, so the first post-release Position will still relay (the gap to the previous relay was ≤ 50ms, well under 750ms throttle window) and then the cadence drops.
3. **Vessel with no inbound for >> throttle window:** no special handling needed; the first inbound after a long quiet period relays unconditionally (nowMs - LastRelayedPositionMs >> minIntervalMs).
4. **Multi-player same-agency on same vessel (forward-compat for the "multiple players per agency" design hinted in 5.18a):** `LockQuery.ControlLockExists` returns true if ANY player holds Control. Filter sees "active" and relays at full cadence. Correct semantic.

### §5.f Settings (already in §3.g)
- `OptimizationSettings.UnpilotedVesselCadenceMultiplier` (default 5).

### §5.g Tests
ServerTest new file `ServerTest/UnpilotedCadenceTest.cs`:
- `ShouldRelayPositionMessage_MultiplierOne_AlwaysTrue` (gate-off behavior)
- `ShouldRelayPositionMessage_ControlLockHeld_AlwaysTrue`
- `ShouldRelayPositionMessage_NoControlLock_FirstMessageRelays`
- `ShouldRelayPositionMessage_NoControlLock_WithinThrottleWindow_False`
- `ShouldRelayPositionMessage_NoControlLock_AfterThrottleWindow_True`
- `ShouldRelayPositionMessage_LockAcquiredMidStream_NextRelaysImmediately`
- `ShouldRelayPositionMessage_LockReleasedMidStream_ThrottlesSubsequent`

MockClientTest end-to-end:
- 2-client harness, Alice holds Control on vessel V_A (active), V_B is debris with no Control lock. Both broadcast Position at 50ms cadence. Bob receives V_A at full cadence; V_B at ~750ms cadence.

### §5.h Acceptance criteria
1. ✅ Vessel with active Control: cadence unchanged.
2. ✅ Vessel without Control: relay cadence drops to ~SecondaryInterval × Multiplier.
3. ✅ Lock acquired immediately resumes full cadence.
4. ✅ Lock released drops cadence on subsequent inbounds.
5. ✅ Multiplier=1 = current behavior preserved.

---

## §6 Cross-cutting design decisions

### §6.1 Settings group: new OptimizationSettings vs. extend GameplaySettings
**Decision:** new `OptimizationSettings` group ([§3.g](#§3g-settings--new-optimizationsettings-group)).

**Rationale:** per Stage 5.18 precedent on `PerAgencyCareer`, settings that are orthogonal to difficulty must NOT be on a group reflected by `SettingsHandler.HasDifferencesAgainstGivenSetting`. New group avoids the `GameDifficulty=Custom` silent-flip caveat.

### §6.2 Gate-off / dual-mode silence
All three wins have an operator gate that defaults true but can be flipped to restore pre-feature baseline. This is the "stability hedge" — if soak surfaces a regression, operator can disable WITHOUT a binary downgrade.

### §6.3 No protocol bump
Recommended approach (Phase 1 wire compat = tail-bit-read on existing message): no protocol version change required. Pre-feature clients run on new servers without modification; new clients on old servers send the tail bit and old servers' deserializers ignore unread tail bytes (Lidgren default).

### §6.4 ForkBuildInfo + log tags
Following [[reference-fork-build-info]] convention: new entry in `Server/ForkBuildInfo.cs` `ActiveFixes[]` per phase shipped. Runtime log tags `[perf:relay-scene]` / `[perf:relay-body]` / `[perf:relay-cadence]` per filter decision (Debug level — not Warning, this is normal operation).

### §6.5 Compatibility matrix
| Client → Server | Server gates ON | Server gates OFF |
|---|---|---|
| Pre-feature client | Scene=Unknown → relay-always (compat path, Phase 1 §3.b option c) | Identical to current behavior |
| New client in Flight | Phase 1 ✓ Phase 2 ✓ Phase 3 ✓ | Identical to current behavior |
| New client in MainMenu | Phase 1 drops vessel-state relays | Receives everything (current behavior) |

### §6.6 Multi-lens review per phase
Per [[feedback-review-lens-framing]]: each phase ships with parallel lens reviews:
- **server-systems lens** — server-side correctness, lock semantics, race windows
- **consumer lens** — what does a downstream consumer (operator running this fork) experience
- **upgrade lens** — what does an operator upgrading from pre-feature binary experience

Reviews block ship until [MUST FIX] items are addressed; [SHOULD FIX] applied or explicitly deferred-with-note; [CONSIDER] applied as XML or memo.

---

## §7 Acceptance criteria — full workstream

1. ✅ All three phases shipped with multi-lens review per phase.
2. ✅ ServerTest count grows by ≥ 20 (estimated: Phase 1 ~8 + Phase 2 ~6 + Phase 3 ~7).
3. ✅ MockClientTest gains 3+ end-to-end cases (one per phase).
4. ✅ Operator can run `feature/server-relay-filtering` binary against a v0.31.0 client (pre-feature) without any regression — measured via the existing MockClientTest harness simulating an old client.
5. ✅ Operator can flip any single gate off without restarting OR causing client desync.
6. ✅ A 10-player soak (5 in Flight, 3 in TrackingStation, 2 in SpaceCenter/Editor) shows measurable inbound-volume reduction on the SpaceCenter/Editor clients via the existing `[fork] ...` connectionstats logging.
7. ✅ No regression on existing 670+ ServerTest cases, 200+ MockClientTest cases, 200+ LmpClientTest cases.

---

## §8 Out of scope — explicit non-goals

- **Per-agency awareness in relay filtering.** Phase 2 (`SameBodyFilter`) intentionally does NOT layer on agency-based filtering — that would couple this workstream to Stage 5 / 6, which isn't merged to master. Deferred.
- **Quantization / delta-encoding / compression of small messages.** These are wins #5 / #6 / #7 — different effort tier, different risk profile. See §10.
- **Per-vessel interest management beyond same-body.** Distance-within-SOI (Win #3) is deferred.
- **Headless physics phantom-client.** Win #9 — operator-burden change, no work in this workstream.
- **Backpressure / load-shed signal.** Win #8 — protocol change, separate workstream.

---

## §9 Q-signoff items

Five load-bearing decisions need explicit operator signoff before Phase 1 code starts. Numbered for traceability.

1. **§3.b wire compat:** (a) protocol bump 0.31.0 → 0.32.0, (b) skip-not-throw on unknown subtype, **(c) tail-bit-read on existing message (recommended)**.
2. **§3.d filter rule:** SpaceCenter scene — included or excluded? **Recommendation: excluded.**
3. **§4.b active-vessel derivation:** (a) derive from sender-of-Position update (recommended, no wire), (b) new explicit wire surface.
4. **§4.d SOI hierarchy:** ship **same-body-only** (recommended, conservative) or hardcoded "same-system" allowlist (breaks under mod planet packs).
5. **§5.c throttle multiplier default:** 3 / **5 (recommended)** / 8 / 10.

---

## §10 Deferred wins (the other 6) — lightweight backlog

### Win #3 — Distance-based downsampling within SOI
**Concept:** within the same body's SOI, downsample relay cadence based on `distance(sender_vessel, recipient_active_vessel)`. Vessels within 200km of recipient = full cadence; 200km-2000km = half cadence; >2000km = quarter cadence.

**Server-side state:** per-vessel last-known position vector (already on Vessel record via `VesselDataUpdater.WritePositionDataToFile`). Per-client active-vessel position (derivable from §4.b Phase 2 work). Distance compute = vector subtract + magnitude, ~microseconds per relay decision.

**Why deferred:** moderate complexity, want to soak the simpler Phase 1+2+3 wins first to establish that the recipient-state-tracking shape works in production.

**Estimated effort:** ~1 week. Reuses §4 + §5 infrastructure.

### Win #5 — Position/rotation quantization
**Concept:** swap full-precision IEEE 754 doubles for int32 (position, cm precision) + int16×4 (quaternion, normalized fixed-point) in `VesselPositionMsgData`. Cuts ~80 bytes per position message (~40% of payload).

**Why deferred:** wire-format change requires either a protocol bump OR a per-field backward-read-compat path (every receiver needs to know "this is the new format with the new quantization"). Phase 1's tail-bit pattern doesn't apply to changing an EXISTING field's representation.

**Estimated effort:** ~3 days code + 1 week soak per [[feedback-mock-test-targeting]].

### Win #6 — Delta encoding against per-client baseline
**Concept:** server maintains per-client per-vessel "last sent" snapshot; outbound messages send only changed fields with a bitmap. Source-engine pattern. Massive win for slowly-changing state (parked stations, debris).

**Why deferred:** ambitious; needs per-client-per-vessel state on server (O(C×V) memory), lock semantics for snapshot updates, careful handling of message loss (snapshot must rebuild on the next acknowledged delivery — Lidgren UnreliableSequenced has no ack signal). Real 2-3 week effort with a real chance of correctness bugs.

**Estimated effort:** 2-3 weeks + lengthy soak.

### Win #7 — Compress position-message batches with QuickLZ
**Concept:** server collects N outbound position messages over a small window (e.g. 25ms) and sends them as one compressed `VesselPositionBatchMsgData` packet, leveraging QuickLZ's ratio on the structurally similar data.

**Why deferred:** trades latency for bandwidth (25ms window = 25ms additional latency on position updates). Won't be acceptable for dogfighting users. Needs an operator gate + per-cohort tuning. Defer until the dogfighting use case has a clear bandwidth pain.

**Estimated effort:** ~1 week.

### Win #8 — Send-side backpressure / load-shed signal
**Concept:** new wire message `ServerLoadAdvisoryMsgData` that tells clients to throttle their outbound rate temporarily when the server's receive queue is backed up. Client honors by slowing local broadcasts.

**Why deferred:** requires careful design to avoid feedback loops (advise → slow → server catches up → un-advise → flood → re-advise). Protocol change. Defer until catastrophic load is a real production symptom.

**Estimated effort:** ~3 days code, weeks of soak.

### Win #9 — Headless physics phantom-client for KSC
**Concept:** spawn a phantom KSP client process on the server box that owns physics for stationary KSC vessels. Other players see those vessels as "always synced" without needing to "be the active flyer."

**Why deferred:** requires KSP install + license on the server box; KSP's headless-ability is poor; nobody in the DMP/LMP lineage has done this; very high implementation + operator-burden cost.

**Estimated effort:** months. Genuinely an "if a server has been running 6+ months with accumulated KSC debris AND the operator is technical enough to set this up" feature.

---

## §11 Phase plan — slice → ship → review per slice

Mirrors Stage 6 / WOLF Phase 4 cadence. Each phase = one commit (or 2 if review reveals MUST FIX requiring a follow-up).

### Phase 1 — Scene-aware relay filtering
1. Add `ClientSceneType` enum + tail-bit-read on `PlayerStatusInfo`
2. Add `ClientStructure.CurrentScene` field (read from `PlayerStatus.Scene`)
3. Add `OptimizationSettings` group + register in `SettingsHandler`
4. Add `MessageQueuer.RelayMessageToFlightScene<T>`
5. Replace 11 call sites in `VesselMsgReader.cs`
6. Add `ForkBuildInfo` entry + boot diagnostic
7. Add ServerTest cases (~8)
8. Add MockClientTest case (1 end-to-end)
9. Add client-side `StatusMessageSender.SendCurrentScene` + `GameEvents.onLevelWasLoaded` hook
10. Multi-lens review (server-systems + consumer + upgrade in parallel)
11. Address findings, ship

**Estimated:** 1 commit, ~600 lines (code + test + spec doc + ForkBuildInfo entry).

### Phase 2 — Same-body filter
1. Add `CurrentBodyName` to `Vessel` class
2. Populate from `VesselDataUpdater.WritePositionDataToFile`
3. Add `ClientStructure.ActiveVesselBodyName` + populate from inbound Position
4. Add `MessageQueuer.RelayMessageToFlightSceneSameBody<T>` (composes Phase 1 + Phase 2)
5. Update 11 call sites in `VesselMsgReader.cs` (or just the Position case if PHASE-2-only)
6. Boot diagnostic
7. Tests (~6 ServerTest + 1 MockClientTest)
8. Multi-lens review
9. Ship

**Estimated:** 1 commit, ~400 lines.

### Phase 3 — Per-vessel cadence by lock holder
1. Add `LastRelayedPositionMs` to `Vessel` class
2. Add `ShouldRelayPositionMessage` helper
3. Add `MessageQueuer.RelayMessageToFlightSceneSameBodyCadence<T>` (composes all three phases)
4. Update Position case in `VesselMsgReader.cs` to use the composed helper (other vessel-message types stay on Phase 1+2 only — cadence shaping is Position-specific)
5. Boot diagnostic
6. Tests (~7 ServerTest + 1 MockClientTest)
7. Multi-lens review
8. Ship

**Estimated:** 1 commit, ~350 lines.

**Total workstream estimate:** 3 commits, ~6 days work + 3 multi-lens review passes + soak between commits. Soak window between phases lets findings inform the next phase's design.

---

## §12 Open questions for operator before Phase 1 begins

The 5 Q-signoff items from §9 + one logistical:

6. **Soak strategy between phases.** Are we shipping Phase 1 to private cohort + soaking ~48h before Phase 2 (Stage 6 cadence), OR shipping all three then soaking (faster ship, slower bisect if regression)?

7. **Release naming.** This branch ships independent of `feature/per-agency`. When it merges to master + cuts a release, do we call it `v0.31.1-perf-1` (incrementing patch on the per-agency line) or `v0.32.0-perf-1` (new minor on master)? Affects auto-updater grammar in [Tools/PlayerUpdater/Core/VersionParser.cs](../../Tools/PlayerUpdater/Core/VersionParser.cs).

---

_End of spec. Ready for Q-signoff on items §9.1-§9.5 + §12.6-§12.7 before Phase 1 code starts._
