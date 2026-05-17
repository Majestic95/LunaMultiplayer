# Near-Term To-Do List

Working list of fork-master work that isn't yet slotted into a Stage in `CLAUDE.md`. Once an item is picked up and lands, move it into the appropriate Stage in `CLAUDE.md`'s "Stage Roadmap" and strike it through here.

---

## 1. ~~BUG-010 graceful disconnect handshake (Option B)~~ ✅ SHIPPED 2026-05-16 (session 7)

Resolved by commits `3637debe` (Part A — server broadcasts `VesselPinned` for each lock-owned vessel before fanning out lock releases; remaining clients hold the leaver's vessels immortal via `VesselPinnedSys` until any player takes the helm) and `90aeed2f` (Part B — `MainSystem.DisconnectFromGame` synchronously flushes a fresh proto for every locally-owned vessel before `NetworkConnection.Disconnect`, so the server's on-disk snapshot reflects the exact moment-of-disconnect pose for the dock-then-logoff → undock-child-pose case). See [Phase-2 analysis](research/02-analysis/bug-010-disconnect-vessel-handoff.md) for design rationale + the deliberate non-fix paths (no `OnExit` hook, no `onVesselChange` early-unpin). Original planning notes below preserved for historical context.

---

**Source:** Discussed 2026-05-16. BUG-010 is currently `Open` in `docs/research/01-bug-inventory.md` (entry at lines 122-129). No upstream PR exists for issue [#654](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/654). Symptom: when a player disconnects (cleanly or via drop) while another player is rendering their craft, the craft is left unpacked in the remaining player's physics bubble with no fresh proto, and physics integrates over the gap — joints pop, kraken on water especially. Pairs with the dock-then-logoff scenario where the merged vessel inherits the same vulnerability.

**Approach (Option B):** Graceful disconnect handshake. Before the client closes its Lidgren connection, send a new `RelinquishVesselsMsg` (or extend an existing message) listing the local player's authoritative vessels with their canonical latest proto state. Server commits each proto, broadcasts a "pack and freeze" instruction to remaining clients in range, then releases the leaving player's locks. Only after server ack does the client actually close the connection. Pairs with AdmiralRadish's existing send-order swap (`6bb056ff` — scenario sync now sent before disconnect) — same pattern, different payload.

**Scope notes:**
- Client side: hook in `MainSystem.DisconnectFromGame`, *before* `NetworkConnection.Disconnect()`. Likely a new sender under `LmpClient/Systems/PlayerConnection/` or extension of an existing one.
- Server side: new handler that ingests the relinquish payload, calls `VesselDataUpdater.RawConfigNodeInsertOrUpdate` for each vessel (so on-disk universe is current), then broadcasts a pack instruction to clients in range. Lock release moves to after the ack.
- Wire protocol: new message type. Bump check — protocol is already at 0.30.0 from BUG-005/006; adding a new optional message *should not* require a further bump if the server treats missing payload as legacy 0.30.0 client = fall through to old immediate-release path. Verify with the matrix in `LmpCommon/LmpVersioning.cs`.
- Remaining-client side: handler for the pack instruction calls `vessel.GoOnRails()` on the named vessels. KSP refuses to pack vessels "near" the active vessel by default — a Harmony patch likely needed to relax this for explicitly-flagged abandoned vessels.

**Pairs with Option A as safety net.** Option B only handles clean exits (user clicks Disconnect). For ungraceful drops (network died), the leaving client never sends the relinquish payload. Option A (`PlayerConnectionMessageHandler` `Leave` case packs vessels locked by the leaving player) is the fallback for that case and can ship alongside B in the same patch series. Without A, BUG-010 still triggers on every connection drop.

**Acceptance criteria:**
- Two-player repro on a lake: P1 lands a floatplane next to P2, P2 clicks Disconnect — floatplane stays intact on P1's screen (packed, on-rails). P1 should see it as a stationary tracking-station icon, no joint pop, no kraken.
- Same repro but P2 force-quits the process (simulates dropped connection) — Option A fallback kicks in: P1's `PlayerConnectionMessageHandler` packs the floatplane on `Leave` event.
- Server `Universe/Vessels/<id>.txt` reflects the canonical pose at the moment of disconnect, not the stale-from-mid-physics-frame pose.
- Mock-client harness regression test (Stage 4.10 follow-up): simulate disconnect mid-flight, assert server-side proto state matches last-broadcast pose.

**Open questions:**
- Does `vessel.GoOnRails()` actually succeed on a docked, IVA-occupied vessel? May need to evict the IVA camera first.
- Timeout if the server never acks the relinquish — how long does the client wait before forcing the disconnect anyway? (Suggest 2s.)
- Interaction with `WarpSystem.RemoveSubspace(client.Subspace)` on disconnect: our BUG-005/006 `RemoveSubspace` guard refuses to drop a subspace that still holds vessel authority. If the merged vessel is authored by the leaving player's subspace, that subspace will stick around post-disconnect. Need to decide: (a) keep it indefinitely as a "ghost" subspace, (b) rewrite vessel authority to the remaining player's subspace as part of the relinquish handshake. (b) is cleaner.

---

## 2. ~~Document docked-vessel ownership behavior after partner disconnect~~ ✅ DONE 2026-05-16 (session 7)

Folded into [Phase-2 analysis for BUG-010](research/02-analysis/bug-010-disconnect-vessel-handoff.md) as Variant B. The dock-then-logoff handoff is now covered by Part A (immortal-hold on the merged vessel until the remaining player takes the helm) + Part B (fresh proto-flush before disconnect so the undock-child-pose isn't stale). The "vessels have no owning-player field" architectural note still belongs in CLAUDE.md or the per-agency Stage 5 spec — deferred until per-agency work picks up the surface. Original planning notes preserved below.

---

**Source:** Discussed 2026-05-16, paired with item 1. Scenario: P1 docks to P2's station, P1 logs off. Question is what P2 can do and whether they still "own" the station.

**Status:** Behavior known, not documented. P1's craft becomes part of a single merged ProtoVessel at dock time (KSP rule, not LMP-specific). The "P2's station" entity no longer exists as a separate vessel while docked. Authority is currently stamped to P1's subspace by our `HandleVesselCouple` (initiator-wins). After P1 disconnects and P2 grabs Control, P2 can operate the whole merged ensemble; to get "their station" back as a separate entity, P2 must undock, at which point the new child vessel (P1's craft) gets authority via the standard first-proto-update rule.

**To do:**
- Add a section to `CLAUDE.md` under "Architecture Principles" or "Stack Notes" explaining: vessels have no "owning player" field today; ownership is implicit in `Control` lock + `AuthoritativeSubspaceId`; docking merges into a single ProtoVessel with one authority; undock authority is implicit via standard rule (per existing known-limitations note).
- Add a Phase-2 doc `docs/research/02-analysis/bug-010-disconnect-vessel-handoff.md` covering both the explode-on-disconnect mechanism (item 1) and the dock-then-logoff handoff (this item), so the fix design references it.
- Confirm via mock-client harness: dock two vessels, disconnect initiator, take Control with remaining client, undock, assert child vessel `AuthoritativeSubspaceId` settles to remaining client's subspace within one proto-update cycle.

**Stage 5 implication:** Real per-player ownership (an `OwningAgency` field on the vessel) is Stage 5 work. Until then, "P2 owns their station" is a social convention enforced by the lock system, not a hard guarantee. Worth flagging in CLAUDE.md so it's not a surprise.

---

## 3. Stage 5 — Per-agency career

**Status:** Spec written 2026-05-16. See [`docs/research/05-per-agency-spec.md`](research/05-per-agency-spec.md) for the full design.

**Confirmed decisions (Majestic95, 2026-05-16):**
- 1 player = 1 agency.
- Per-agency independent tech tree.
- Per-agency facility upgrade levels, shared physical KSC.
- All vessels visible in tracking station, agency-labelled.

**Defensible defaults to confirm before Stage 5.14 coding starts (see spec §2 and §10):**
- Q1: Other agencies' funds/sci/rep hidden in UI (`PrivateAgencyResources = true` default)?
- Q2: `transferagency` admin command preserves owned vessels?
- Q3: Sentinel `"Unassigned"` agency owns vessels missing `lmpOwningAgency`?
- Q4: Contract rewards route by contract-issuing agency, not by vessel-owning agency?
- Migration: fresh-start-only (no shared→per-agency migration tool in v1) — confirm.
- CommNet: shared infrastructure in v1 — confirm.
- Save migration tool: explicitly deferred — confirm.

**Action items before Stage 5.12 (branch creation):**
- Walk through spec §10 Open Questions with Majestic95 and lock final defaults.
- Decide whether the new `LmpClientTest` project (spec §8 item 10) is created as part of Stage 4.10 follow-on or as Stage 5.15 setup.
- Confirm protocol bump 0.30.0 → 0.31.0 is the right move (vs. additive optional fields that could keep cross-compat with shared-agency 0.30.x clients).

---

## 4. BUG-008 follow-on — landed-vessel jolt on reload (Phase B + companion mitigations)

**Source:** Discussed 2026-05-16. Player reports continuing: when a vessel is landed and the instance is reloaded with the landed vehicle, the entire craft gets jolted, misplaced (sometimes to the point of inoperability), or destroyed outright. This is the [BUG-008] / [BUG-009] / on-runway variant of [BUG-021] symptom class — see Phase-2 doc at [`docs/research/02-analysis/bug-008-pqs-spawn-altitude.md`](research/02-analysis/bug-008-pqs-spawn-altitude.md).

**Status:** Phase A shipped (session 6) — `LmpClient/VesselUtilities/PqsAlignmentRoutine.cs` polls PQS up to 5s after `vesselProto.Load` and snaps the vessel to a stabilised surface height. Decision math is exhaustively covered by `LmpClientTest/PqsAlignmentDecisionTest`. Players still reporting the symptom means Phase A alone isn't enough — either the 5s polling cap fires too early on cold-cache bodies (so the snap uses a still-wrong altitude), or the jolt is driven by a different vector entirely (physics-on-unpack collider race, phantom flight-state force, wrong stored altitude in the first place).

**Approach: ship in priority order, evaluate after each.** Highest leverage first; each item is independently shippable.

### 4a. Pack-on-load + delayed unpack (highest leverage, ship first)
- After `vesselProto.Load` returns inside `LmpClient/VesselUtilities/VesselLoader.cs:LoadVesselIntoGame`, call `vessel.GoOnRails()` immediately. Hold on-rails until BOTH `PqsAlignmentRoutine` reports stable AND at least one physics tick has elapsed with no pending flight-state update for that vessel.
- Only `GoOffRails()` once stable, THEN fire `VesselLoadEvent.onLmpVesselLoaded`.
- DMP-era trick. Collider explosion happens during the high-LOD terrain stream-in if parts have non-zero velocity or overlap unresolved geometry; pinning to rails freezes physics integration so the stream-in completes harmlessly.
- Touch: `VesselLoader.cs` (call site after Load), `PqsAlignmentRoutine.cs` (extend to drive pack/unpack lifecycle, not just position).
- Cheap, no wire change, no protocol bump. Visible side-effect: ~1s "vessel sits frozen post-spawn" — vastly preferable to detonation.
- Active-vessel reload path must also be guarded (Phase A doc risk note item #3) — confirm in code that the active-vessel post-scene-transition reload still flows through `LoadVesselIntoGame`, OR add a parallel hook.

### 4b. Phase B — server-stored `terrainAltitude` in the proto
- Add `lmpTerrainAltitude` field to the vessel ConfigNode (fork-local, `lmp*` prefix per the convention in CLAUDE.md "Stack Notes" — KSP silently ignores unknown top-level fields, so round-trip is safe).
- Stamp it from the placing client's PQS at vessel-creation time (or on first authoritative proto-update). Plumb through `MixedCollection<string, string> Fields` on `Server/System/Vessel/Classes/Vessel.cs` (same vehicle as `lmpAuthSubspace`).
- Receiver consumes it in `PqsAlignmentRoutine` as ground-truth offset instead of polling PQS from cold cache.
- Touch: `LmpClient/Systems/VesselProtoSys/VesselProtoSystem.cs` (write on outbound), `Server/System/Vessel/VesselSanitizer.cs` (preserve/normalise on ingest), `PqsAlignmentRoutine.cs` (consume on inbound — use stored value if present, fall back to PQS poll if absent).
- Wire-level addition. Backward-compat: missing field = legacy client, fall back to Phase A polling. Treat as additive — no protocol bump needed (confirm against `LmpCommon/LmpVersioning.cs` compat matrix; protocol is currently 0.30.0).

### 4c. Phantom-force suppression on first physics frame post-load (defensive)
- After unpack, drop the first ~3 inbound `VesselFlightStateMsgData` updates for the loaded vessel IF the proto `sit == LANDED | SPLASHED | PRELAUNCH` AND the inbound state carries non-zero linear/angular velocity.
- Touch: `LmpClient/Systems/VesselFlightStateSys/`. Per-vessel "just-loaded" timestamp keyed on `vessel.id`; predicate at the head of the inbound handler.
- Belt-and-suspenders for the BUG-009 / BUG-021 symptom class (Stratolauncher-crumple-on-launch). Independently useful even if 4a + 4b close the primary repro.

### 4d. Hard landed-pin for first second of vessel life (last resort, only if 4a-c don't settle it)
- If proto says `LANDED` with stored velocity ≈ 0, pin `vessel.Landed = true` and zero `vessel.srf_velocity` each tick for the first 1s regardless of incoming flight state.
- Heavy-handed; risks masking other physics bugs. Ship only if the soak test after 4a + 4b + 4c still shows jolt reports.

**Acceptance criteria:**
- Upstream [#279](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/279) repro: place stock plane on Kerbin runway with player A, save server-side, fresh player B connects → vessel spawns intact, no polygon scramble, no explosion. Verify on a body LMP hasn't loaded recently (cold PQS cache).
- [BUG-009] repro (Stratolauncher-launch crumple) re-tested after 4a + 4c ships — expected to retire (audit-close in `01-bug-inventory.md` if confirmed).
- [BUG-021] on-runway variant re-tested after 4a ships — expected to retire.
- `[fix:BUG-008]` log lines fire only on legitimate re-alignment events (no spam on clean spawns). Add a `[fix:BUG-008-pack]` tag for the pack/unpack lifecycle so operators can grep them separately.
- Soak: a week of dev-server player sessions with zero "vessel exploded on reconnect" reports.

**Open questions (resolve before coding starts):**
- Q1: Does `vessel.GoOnRails()` succeed unconditionally on a fresh-loaded landed vessel? Are there KSP-side rejects (IVA-occupied, debris-flagged, parts mid-staging)? Check `Vessel.GoOnRails` source and `Vessel.PackVessel` preconditions.
- Q2: For 4b — single canonical place to derive `terrainAltitude` from PQS at vessel-creation time on the placing client? Look at `Vessel.terrainAltitude` getter + when it becomes reliable post-spawn.
- Q3: For 4c — drop-N inbound updates vs. clamp-velocity-to-zero? Drop is simpler; clamp preserves orientation updates. Suggest drop for v1.
- Q4: Does the `GoOnRails()` helper for 4a overlap with item #1's BUG-010 disconnect handshake (which also wants "force vessel to rails on a lifecycle event")? If yes, design the helper so both call sites share it — extract to `LmpClient/VesselUtilities/VesselPackHelper.cs` or similar.
- Q5: Phase-A's existing 5s hard cap — should it be raised, removed, or kept once Phase B lands? Once 4b ships, the polling is the cold-cache fallback only; suggest raising to 10s and adding a per-body adaptive backoff.

**Dependencies / coordination:**
- Pairs with item 1 (BUG-010 disconnect handshake). See Q4 — design the pack-helper for reuse.
- No upstream PR exists for #279 in AdmiralRadish's recent work (confirmed in `bug-008-pqs-spawn-altitude.md`); greenfield turf for the fork.
- Phase B (4b) wire addition: additive only, no protocol bump if `lmpTerrainAltitude` absent falls through to Phase A polling. Confirm against `LmpCommon/LmpVersioning.cs` compat matrix.
- Test coverage: extend `LmpClientTest/PqsAlignmentDecisionTest` with cases for "stored-terrainAltitude present" vs "stored-terrainAltitude absent → poll path". KSP-bound coroutine and pack/unpack lifecycle still need in-KSP soak — no harness reach for PQS or physics.
- Update CLAUDE.md "Stack Notes" with a new entry when 4a or 4b lands, documenting the pack-on-load lifecycle and (for 4b) the `lmpTerrainAltitude` field convention.
