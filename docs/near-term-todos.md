# Near-Term To-Do List

Working list of fork-master work that isn't yet slotted into a Stage in `CLAUDE.md`. Once an item is picked up and lands, move it into the appropriate Stage in `CLAUDE.md`'s "Stage Roadmap" and strike it through here.

---

## 1. BUG-010 graceful disconnect handshake (Option B)

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

## 2. Document docked-vessel ownership behavior after partner disconnect

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
