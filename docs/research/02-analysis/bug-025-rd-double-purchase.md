# BUG-025 — R&D tech node researchable multiple times by separate clients

**Phase-2 analysis. Status: Fixed (2026-05-17, session 9).**

Upstream tracker: [#667](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/667). Game-breaking economy bug in shared-career play — two clients with the R&D details panel open on the same node can both click "Research" and each gets charged the science cost. Only the first purchase actually unlocks anything; the second is silent loss-of-science.

## Repro

1. Player A and Player B are both connected to the same shared-career server.
2. Both open the R&D building and click on the same unresearched node (e.g. `engineering101`). KSP's R&D screen shows the node details panel on the right with the "Research" button enabled for both.
3. Player A clicks Research first. KSP locally subtracts the science cost, calls `ResearchAndDevelopment.Instance.UnlockProtoTechNode(node)`, and fires `GameEvents.OnTechnologyResearched`. The `ShareTechnologyEvents.TechnologyResearched` handler broadcasts a `ShareProgressTechnologyMsgData` to the server.
4. The server relays the broadcast to Player B (and any other clients). B's `ShareTechnologyMessageHandler.HandleMessage` queues a Unity-thread action that calls `UnlockProtoTechNode(node)` + `RefreshTechTreeUI()` + `EditorPartList.Instance.Refresh()`.
5. **`RefreshTechTreeUI` refreshes the tree view (the left-hand node graph) but does not refresh `RDController`'s right-hand details panel.** B's open panel still shows the node as researchable, with the "Research" button still enabled.
6. Player B clicks Research. KSP locally subtracts the science cost again (KSP's R&D button handler does not re-check the node's current unlocked state — the panel state is the source of truth, and the panel is stale). `UnlockProtoTechNode` is called (idempotent — the node is already unlocked, so no functional effect). `OnTechnologyResearched` fires. B broadcasts a `ShareProgressTechnologyMsgData` for the same tech ID.
7. The server relays B's broadcast to A. A's handler calls `UnlockProtoTechNode` again (no-op).
8. Net effect: A spent science once (correct). B spent science once (incorrect — the node was already unlocked at server-canonical level).

The same mechanism applies if A and B click within network-RTT of each other so neither has the other's broadcast yet — both clients deduct science before either broadcast lands at the server.

## Root cause

LMP's tech-share flow is "client commits locally, broadcasts the result, server relays + persists." Specifically:

| Step | File | Behaviour |
|---|---|---|
| KSP local commit | (KSP-internal) | `UnlockProtoTechNode` + science deduction happen inside KSP's R&D button handler, BEFORE any LMP code runs |
| Local fire | (KSP `GameEvents.OnTechnologyResearched`) | LMP picks up after the deduction has already happened |
| Send | [LmpClient/Systems/ShareTechnology/ShareTechnologyMessageSender.cs:19-37](../../../LmpClient/Systems/ShareTechnology/ShareTechnologyMessageSender.cs#L19-L37) | Builds + sends `ShareProgressTechnologyMsgData` |
| Server receive | [Server/System/ShareTechnologySystem.cs:12-19](../../../Server/System/ShareTechnologySystem.cs#L12-L19) | Unconditionally relays to all other clients, then queues an async `WriteTechnologyDataToFile` |
| Server async persist | [Server/System/Scenario/ScenarioTechnologyDataUpdater.cs:14-43](../../../Server/System/Scenario/ScenarioTechnologyDataUpdater.cs#L14-L43) | Inside `Task.Run`, takes per-scenario lock, checks if tech already in scenario, adds if absent. **Existing dedup correctly prevents duplicate disk-state, but does NOT inform the sender that they bought a no-op.** |
| Receiver | [LmpClient/Systems/ShareTechnology/ShareTechnologyMessageHandler.cs:32-48](../../../LmpClient/Systems/ShareTechnology/ShareTechnologyMessageHandler.cs#L32-L48) | `UnlockProtoTechNode` (idempotent) + `RefreshTechTreeUI` + `EditorPartList.Refresh`. **Does NOT touch `RDController`'s open detail panel.** |

The bug has two halves that compound:
- **UI half:** B's open panel keeps a stale "Research" button after A's broadcast lands.
- **Server-trust half:** server-side dedup happens but it's asynchronous AND it doesn't communicate back to the sender. Even if we fix the UI race, two clients clicking within sub-RTT will both deduct science before either broadcast lands.

The receiver-side UI fix alone doesn't close the network-RTT race window. The server-side validation alone doesn't fix the "user can click their own button twice fast" case but is a strict superset because it catches the cross-client race that the UI fix can't.

## Fix design

**Server-side synchronous check-and-claim + sender-targeted rejection message + client-side science refund.** Closes the race completely regardless of UI timing.

### Wire surface (additive, no protocol bump)

1. **New `ShareProgressMessageType.TechnologyRejected = 11`** in [LmpCommon/Message/Types/ShareProgressMessageType.cs](../../../LmpCommon/Message/Types/ShareProgressMessageType.cs). Additive enum value at the tail. **The reason no protocol bump is needed is NOT that `LmpVersioning` tolerates additive enums** — it doesn't; the compat matrix requires exact major+minor match and `CrossCompatibleVersionLines` is empty. The real reason: every 0.30.0 peer on this network is this fork (BUG-005/006 already broke vanilla 0.30.x cross-compat), and same-fork peers all know subtype 11. A pre-fix fork client (somehow lingering on a stale build) receiving the new subtype will actually throw `Exception("Subtype not defined in dictionary!")` from `MessageBase.GetMessageData` — typically swallowed by the receive-loop catch, so the symptom is a silent log line and the old bug behaviour persists for them. Acceptable upgrade-window cost.
2. **New `ShareProgressTechnologyRejectedMsgData`** with `string TechId` + `float RefundScience`. Wire payload: same length-prefixed string serialization as existing `TechNodeInfo.Id`, plus one `float`.
3. **Register the new subtype in `ShareProgressSrvMsg.SubTypeDictionary`** (server-to-client direction only — there is no client-to-server rejection path).

### Server-side change

`ScenarioDataUpdater.TryAddTechnologyAtomic(ShareProgressTechnologyMsgData)` is a new synchronous helper that returns `(bool Added, float CostInPayload)`:
- Takes the per-scenario lock (same `ScenarioDataUpdater.GetSemaphore("ResearchAndDevelopment")` writers already use — BUG-033's helper).
- Parses the incoming `TechNode.Data` payload once. Extracts the `cost` value (the science cost the client claims they paid). If the parse fails, returns `(true, 0f)` — degrade to the pre-fix behaviour (relay, no rejection).
- Scans the existing scenario's `Tech` children for a matching `id`. If found, returns `(false, costInPayload)` — duplicate, sender should refund.
- Otherwise adds the new node and returns `(true, 0f)`.

The existing async `WriteTechnologyDataToFile` is removed — its work is now done synchronously inside `TryAddTechnologyAtomic`. The on-disk persistence path (`ScenarioStoreSystem.BackupScenarios`) is unchanged.

`Server/System/ShareTechnologySystem.cs:TechnologyReceived` becomes a branch:
- Call `TryAddTechnologyAtomic`.
- If `Added == true`: relay as today.
- If `Added == false`: send a `ShareProgressTechnologyRejectedMsgData` to the sender only (via `MessageQueuer.SendToClient<ShareProgressSrvMsg>`), do NOT relay. Log `[fix:BUG-025] Rejected duplicate tech purchase {techId} from {playerName}; refunding {cost}`.

### Client-side change

`ShareTechnologyMessageHandler.HandleMessage` gains a `TechnologyRejected` branch:
- Read `TechId` + `RefundScience` from the message.
- Queue a Unity-thread action that calls `ResearchAndDevelopment.Instance.AddScience(refundScience, TransactionReasons.RnDTechResearch)`.
- Log `[fix:BUG-025] Server rejected duplicate purchase of {techId}; refunded {cost} science`.

No UI refresh is required on the receiving side — the player will see their science count tick back up via the normal HUD update. They can click around the R&D screen to refresh the panel if they're confused.

### Why this design

| Alternative | Rejected because |
|---|---|
| Client-side: close/refresh `RDController` panel on `TechnologyUpdate` arrival | Doesn't close the sub-RTT click race. UI fix is also fragile — KSP's `RDController` API surface is sparsely documented and prone to subtle breakage. |
| Server-side: validate but don't refund (just drop the duplicate silently) | Leaves the sender's science permanently spent for a no-op. The current bug, except quieter. |
| Restructure the whole flow to "request → server-acks-or-rejects" | Major refactor; today's pattern is client-commits-locally + server-relays. Too big for a single-bug fix. |
| Per-agency split (Stage 5) | Stage 5 work; out of scope. The shared-agency fix here is what current servers need. Stage 5 will need to keep this same check pattern but per-agency-keyed. |

### Why not also fix the UI race

Considered as defence-in-depth. Deferred:
- The server-side fix already closes the race completely.
- The UI fix is a separate code domain (KSP `RDController` API) with its own bug surface.
- Players' visible symptom after this fix is "science briefly decreases then increases back, transaction reason shows RnDTechResearch refund" — a noticeable but non-destructive UX glitch. Better than today's silent loss.

If players report the UX as confusing, ship a follow-up that hooks `RDController` on receive and refreshes the open panel.

## Test plan

`MockClientTest/Bug025RejectionTest.cs` (new) — end-to-end coverage:

1. **`DuplicatePurchase_IsRejected_SenderReceivesRefund`** — two mock clients connect + handshake. Pre-populate `CurrentScenarios["ResearchAndDevelopment"]` with a `Tech` node for tech ID `engineering101`. Client A sends a `ShareProgressTechnologyMsgData` for `engineering101` with cost = 45 science. Assert: A receives `ShareProgressTechnologyRejectedMsgData` with `TechId == "engineering101"` and `RefundScience == 45f`. Assert: B does NOT receive a `ShareProgressTechnologyMsgData` relay (waits 500ms then confirms inbox empty for that type).
2. **`FirstPurchase_IsRelayedNotRejected`** — same two-client setup but pre-populated scenario does NOT contain the tech. Client A sends for `start` (different tech id). Assert: B receives the relay. A does NOT receive a rejection.

These are the two halves of the contract: rejection on duplicate, relay on first.

Refund-side client behaviour (the actual `AddScience` call on the player) is KSP-bound and cannot be harness-tested — relies on `ResearchAndDevelopment.Instance` which doesn't exist outside KSP. The wire-level assertion above is the contract that matters; the local refund is a one-line `Instance.AddScience` call covered by the rubric.

## Risks and known limitations

1. **Refund amount is the client's CLAIMED cost.** The server trusts the incoming `ShareProgressTechnologyMsgData.TechNode.Data` payload's `cost` value. If a malicious client lies about the cost, the server refunds the lied amount on rejection. This is consistent with LMP's existing trust model — all `Share*` payloads (funds, science, contracts) are accepted at face value from the sender. Per-agency / Stage 5 might want a tighter validation pass against KSP's canonical R&D tree, but that's a wider conversation.

2. **Cross-version compatibility.** Old (pre-fix) clients will hit `MessageBase.GetMessageData`'s `throw new Exception("Subtype not defined in dictionary!")` when they receive subtype 11. The receive-loop catch swallows it (per existing `ReceiverBase` pattern), so the effective behaviour is "no rejection processing, log line on the floor" — the player retains today's silent-science-loss bug. New-fix clients refund correctly. No protocol bump because vanilla 0.30.x compat was already broken by BUG-005/006, so every same-version peer is this fork. See the "Wire surface" section for the corrected reasoning.

3. **First-tech-ever case.** If the scenario doesn't yet exist (`CurrentScenarios.TryGetValue` returns false), `TryAddTechnologyAtomic` returns `(true, 0f)` — relay as today. The existing async path's behaviour here was to also return without persisting (because of the same TryGetValue miss), so we're maintaining parity. The very first tech to land on a fresh server still routes through whichever code path creates the scenario entry (it does NOT come from `WriteTechnologyDataToFile`; that helper assumed the scenario already existed).

4. **R&D button double-click on a single player.** The local KSP button handler is the client's own responsibility. This fix doesn't address `Player A double-clicks Research within 100ms` because both clicks happen entirely client-side. KSP's button is debounced internally; not a new bug.

5. **`WriteTechnologyDataToFile` deletion** removes a public method. Search for callers turned up zero outside `ShareTechnologySystem` (the call site we modified). Safe to remove. If a future caller needs the "fire and forget tech write" pattern, they should call `TryAddTechnologyAtomic` and ignore the return.

## Cross-cutting effects

- New wire enum value, new message type. Additive. No protocol bump.
- `[fix:BUG-025]` log tag on the server reject path and the client refund path.
- `Server/ForkBuildInfo.cs` `ActiveFixes[]` adds `"BUG-025"`.
- No touched code under `Lidgren/`. Touches `LmpCommon/`, `Server/`, `LmpClient/`.
- Closure of BUG-025 leaves BUG-023 as the last top-10 open bug.

## Stage 5 implications

When per-agency career lands (Stage 5), each agency gets its own scenario state. The `TryAddTechnologyAtomic` helper's "is this tech already in the scenario" check needs to be keyed by agency, not global. The per-agency rewrite will replace the `"ResearchAndDevelopment"` key with `($"ResearchAndDevelopment.{agencyId}")` or similar. The fix shipped here is forward-compatible with that change — the helper signature stays the same, the lookup target shifts.
