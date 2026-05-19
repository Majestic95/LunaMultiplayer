using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// Outbound side of the per-agency career wire surface (Stage 5.15c). Builds the
    /// payloads defined in Stage 5.15b and routes them through <see cref="MessageQueuer"/>.
    ///
    /// **Privacy rule (spec §10 Q1 — PrivateAgencyResources = true).**
    /// <see cref="AgencyStateMsgData"/> carries Funds / Science / Reputation and is
    /// therefore OWNER-ONLY. We never <c>SendToAllClients</c> an AgencyStateMsgData.
    /// Cross-agency awareness is conveyed through the public-only <see cref="AgencyInfo"/>
    /// summaries embedded in <see cref="AgencyHandshakeMsgData"/> — id + owner +
    /// display name, no resource fields. The Stage 5.18c <c>AgencyVisibilityMsgData</c>
    /// will extend that surface for tracking-station labels when it lands.
    ///
    /// **Dual-mode gate (spec §11).** Every entry point early-returns when
    /// <see cref="AgencySystem.PerAgencyEnabled"/> is false — combined check for
    /// <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>=true AND
    /// <see cref="LmpCommon.Enums.GameMode"/>=Career (Stage 5.17e-1, spec §10 Q-Mode).
    /// Same shape as the lifecycle methods on <see cref="AgencySystem"/>.
    /// </summary>
    public static class AgencySystemSender
    {
        /// <summary>
        /// Sends the connecting client their assigned-agency id plus a public-only
        /// summary of every other agency known to the server. Called from
        /// <see cref="HandshakeSystem"/> after <see cref="AgencySystem.OnPlayerAuthenticated"/>
        /// has populated the registry for this player.
        ///
        /// The "OtherAgencies" array deliberately excludes the assigned agency itself —
        /// the client already learns its own data through <see cref="SendStateTo"/>.
        /// Bloating the handshake with a self-entry would double-count private data on
        /// the wire (the AgencyInfo summary intentionally elides scalars; sending the
        /// scalars through a separate State message is cleaner).
        /// </summary>
        public static void SendHandshakeTo(ClientStructure client, Guid assignedAgencyId)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (client == null)
                return;

            var others = new List<AgencyInfo>(AgencySystem.Agencies.Count);
            foreach (var kvp in AgencySystem.Agencies)
            {
                if (kvp.Key == assignedAgencyId)
                    continue;

                var state = kvp.Value;
                others.Add(new AgencyInfo
                {
                    AgencyId = state.AgencyId,
                    OwningPlayerName = state.OwningPlayerName ?? string.Empty,
                    DisplayName = state.DisplayName ?? string.Empty,
                });
            }

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyHandshakeMsgData>();
            msgData.AssignedAgencyId = assignedAgencyId;
            msgData.OtherAgencyCount = others.Count;
            msgData.OtherAgencies = others.ToArray();

            MessageQueuer.SendToClient<AgencySrvMsg>(client, msgData);
        }

        /// <summary>
        /// Sends the full agency state (id + owner + display name + funds + science +
        /// reputation) to the OWNING client only. Other agencies must never receive
        /// this — see the class-level privacy rule.
        /// </summary>
        public static void SendStateTo(ClientStructure client, AgencyState state)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (client == null || state == null)
                return;

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyStateMsgData>();
            msgData.AgencyId = state.AgencyId;
            msgData.OwningPlayerName = state.OwningPlayerName ?? string.Empty;
            msgData.DisplayName = state.DisplayName ?? string.Empty;
            msgData.Funds = state.Funds;
            msgData.Science = state.Science;
            msgData.Reputation = state.Reputation;

            MessageQueuer.SendToClient<AgencySrvMsg>(client, msgData);
        }

        /// <summary>
        /// Convenience wrapper that resolves the owning client by player name and routes
        /// <see cref="SendStateTo"/>. No-op if the player isn't currently connected
        /// (offline owners receive their state on their next handshake via
        /// <see cref="SendHandshakeTo"/> + the assigned id).
        /// </summary>
        public static void SendStateToOwner(AgencyState state)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (state == null || string.IsNullOrEmpty(state.OwningPlayerName))
                return;

            var owner = ClientRetriever.GetClientByName(state.OwningPlayerName);
            if (owner == null)
                return;

            SendStateTo(owner, state);
        }

        /// <summary>
        /// Sends the reply to a <see cref="AgencyCreateRequestMsgData"/> back to the
        /// originating client. <paramref name="success"/> is false when the requested
        /// display name was invalid (empty / too long / etc); <paramref name="reason"/>
        /// is the human-readable rejection string surfaced to the client UI.
        /// </summary>
        public static void SendCreateReplyTo(ClientStructure client, Guid agencyId, string displayName, bool success, string reason)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (client == null)
                return;

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyCreateReplyMsgData>();
            msgData.AgencyId = agencyId;
            msgData.DisplayName = displayName ?? string.Empty;
            msgData.Success = success;
            msgData.Reason = reason ?? string.Empty;

            MessageQueuer.SendToClient<AgencySrvMsg>(client, msgData);
        }

        /// <summary>
        /// Stage 5.17d — owner-only echo of a per-agency contract batch (Q6 hybrid,
        /// post-Accept states). Routed exclusively to the agency owner; peers never
        /// receive another agency's per-agency contracts (spec §10 Q1
        /// PrivateAgencyResources=true). No-op when:
        /// <list type="bullet">
        ///   <item><see cref="AgencySystem.PerAgencyEnabled"/> is false — gate off OR
        ///        non-Career game mode (Stage 5.17e-1 Career-only gate, spec §10 Q-Mode).
        ///        Dual-mode silence preserved across the entire wire surface.</item>
        ///   <item>The owner is null (called from a code path where the source client
        ///        is unknown).</item>
        ///   <item>The batch is empty / null (nothing to send).</item>
        /// </list>
        /// </summary>
        public static void SendContractsToOwner(ClientStructure owner, Guid agencyId, IReadOnlyList<ContractInfo> contracts)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (owner == null || contracts == null || contracts.Count == 0)
                return;

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyContractMsgData>();
            msgData.AgencyId = agencyId;
            msgData.ContractCount = contracts.Count;
            msgData.Contracts = new ContractInfo[contracts.Count];
            for (var i = 0; i < contracts.Count; i++)
                msgData.Contracts[i] = contracts[i];

            MessageQueuer.SendToClient<AgencySrvMsg>(owner, msgData);
        }

        /// <summary>
        /// Stage 5.17d — catch-up send of the OWNER's persisted per-agency contracts on
        /// connect. Called from <see cref="HandshakeSystem"/> after the agency
        /// handshake + state push, so a returning player whose contracts persisted to
        /// <see cref="AgencyState.Contracts"/> across a server restart receives them
        /// without having to send any mutation first.
        ///
        /// Without this, the consumer-lens review noted: a 5.18a client author would see
        /// <see cref="AgencyState.Contracts"/> persisted on disk, write a handler for
        /// <see cref="AgencyContractMsgData"/>, test against a fresh session (works),
        /// then ship a build where a returning player loses their entire Active+Finished
        /// list until they mutate something. Catch-up closes that gap server-side so the
        /// 5.18a handler can rely on a deterministic "state arrives once on connect" contract.
        /// </summary>
        public static void SendContractCatchupTo(ClientStructure client, AgencyState state)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (client == null || state == null || state.Contracts == null || state.Contracts.Count == 0)
                return;

            var infos = new List<ContractInfo>(state.Contracts.Count);
            foreach (var entry in state.Contracts)
            {
                if (entry == null) continue;
                var lenSafe = Math.Min(entry.NumBytes, entry.Data?.Length ?? 0);
                var copy = lenSafe > 0 ? new byte[lenSafe] : new byte[0];
                if (lenSafe > 0)
                    Buffer.BlockCopy(entry.Data, 0, copy, 0, lenSafe);
                infos.Add(new ContractInfo
                {
                    ContractGuid = entry.ContractGuid,
                    Data = copy,
                    NumBytes = lenSafe,
                });
            }

            if (infos.Count == 0)
                return;

            SendContractsToOwner(client, state.AgencyId, infos);
        }

        /// <summary>
        /// Stage 5.18d — broadcast a batch of vessel-ownership transitions to every
        /// connected client. Called from the admin commands that mutate
        /// <c>Vessel.OwningAgencyId</c> in the canonical store (5.18d slice (e)
        /// <c>transferagency</c>, slice (g) <c>deleteagency</c> cascade).
        ///
        /// <para><b>Broadcast (not owner-only).</b> Vessel ownership is public state —
        /// every client needs the transition for Stage 5.18c UI labels and Stage 5.18d
        /// economy guards (recovery-of-cross-agency-vessel rejection). See
        /// <see cref="AgencyVisibilityMsgData"/> XML for the rationale; spec §10 Q1's
        /// <c>PrivateAgencyResources=true</c> applies to resource scalars, not to
        /// ownership identity.</para>
        ///
        /// <para><b>No-op cases:</b>
        /// <list type="bullet">
        ///   <item><see cref="AgencySystem.PerAgencyEnabled"/> is false. Dual-mode silence
        ///         (spec §11) — under gate=off the admin commands themselves should
        ///         refuse to run; the no-op here is belt-and-braces.</item>
        ///   <item><paramref name="changes"/> is null or empty. No-op silently; callers
        ///         don't need a pre-flight check.</item>
        /// </list></para>
        ///
        /// <para><b>Mutation ordering contract for callers.</b> The canonical
        /// <c>Vessel.OwningAgencyId</c> on the server MUST be updated BEFORE this
        /// broadcast fires. The race window is benign on the CLIENT side — see
        /// <see cref="AgencyVisibilityMsgData"/>'s cross-channel hazard paragraph for
        /// the full analysis: <see cref="LmpClient.Systems.Agency.AgencyMembership.ForceRecordOwnership"/>
        /// is unconditional and <see cref="LmpClient.Systems.Agency.AgencyMembership.RecordOwnership"/>'s
        /// preservation rule keeps a relay-stripped resend from clobbering Y. But on
        /// the SERVER side, an inverted order (broadcast fires while the canonical
        /// store still holds X) means the server's own relay path would forward a
        /// proto carrying the stale stamp AFTER the broadcast — peers would see Y,
        /// then the relay would deliver an unrelated authoritative proto whose
        /// embedded <c>lmpOwningAgency</c> is still X (only stripped on resends from
        /// the new owner, not on the server-rebuilt-then-relayed proto from the prior
        /// owner). The 5.18d admin commands hold the vessel-store lock anchor across
        /// the mutation AND the broadcast inside the same critical section — see
        /// slice (e) / (g) for the concrete pattern.</para>
        ///
        /// <para><b>Caller contract on <paramref name="changes"/>.</b> The sender does
        /// NOT defensively snapshot the input list; values are read out via the
        /// indexer during the synchronous build of <c>AgencyVisibilityMsgData.Changes</c>.
        /// Callers MUST NOT mutate the list while this method is in flight. The
        /// recommended call shape — build a local <c>List&lt;VesselOwnershipChange&gt;</c>
        /// inside the lock-anchored critical section and pass it once — naturally
        /// avoids the hazard.</para>
        ///
        /// <para><b>Chunking.</b> Batches larger than
        /// <see cref="AgencyVisibilityMsgData.MaxChangeCount"/> are split into multiple
        /// consecutive messages on channel 22. Lidgren's per-channel
        /// <c>ReliableOrdered</c> guarantee preserves across-batch apply order on the
        /// client side — the receiver applies chunks in the same order the sender
        /// emitted them, so a deleteagency cascade against an agency with thousands
        /// of vessels lands deterministically.</para>
        /// </summary>
        /// <summary>
        /// [Phase 3 Slice B] Owner-only echo of a per-agency kolony entry batch.
        /// Emitted by <see cref="AgencyKolonyRouter.TryRoute"/> after a successful
        /// upsert + persist. Peers never receive another agency's per-agency kolony
        /// entries (spec §10 Q1 PrivateAgencyResources=true) — projection through
        /// <see cref="AgencyScenarioProjector"/>'s <c>KolonizationScenario</c> case
        /// (Slice B) is the only path by which cross-agency awareness leaks into
        /// the read-side, and projection happens per-target-client at scene-load
        /// time.
        ///
        /// <para>No-op when:</para>
        /// <list type="bullet">
        ///   <item><see cref="AgencySystem.PerAgencyEnabled"/> is false — gate=off
        ///        OR non-Career game mode. Dual-mode silence preserved.</item>
        ///   <item>The owner client is null (call from a code path with no source
        ///        client — should not happen for the router, defensive).</item>
        ///   <item>BOTH <paramref name="entries"/> AND
        ///        <paramref name="removedKeys"/> are null/empty — sending a fully
        ///        empty payload is wasted wire traffic. The early-return preserves
        ///        the pre-Slice-E-1 behaviour for the upsert-echo call sites that
        ///        pass <c>entries</c> alone (removedKeys defaults to null).</item>
        /// </list>
        ///
        /// <para><b>Caller contract on <paramref name="entries"/>:</b> the sender
        /// filters out null entries (round-1 general-lens SHOULD-FIX S2) so a
        /// caller building a list across a fallible upsert loop doesn't desync the
        /// wire's <c>EntryCount</c> vs the non-null slot count. Slice E migration
        /// callers passing pre-snapshotted lists from inside their per-agency lock
        /// critical section satisfy the non-null contract by construction, but the
        /// defensive filter here is the load-bearing line for ad-hoc callers.</para>
        ///
        /// <para><b>[Phase 3 Slice E-1] <paramref name="removedKeys"/></b> (default
        /// null): optional list of <c>$"{vesselId:N}|{bodyIndex}"</c> dict-keys
        /// the receiver should treat as removed from its in-memory mirror. Only
        /// populated by the per-router migration helper
        /// (<see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/>'s
        /// <c>RemovedKeys</c>) for the source agency's owner-only echo when a
        /// vessel transfers out of the source agency. Null/empty for the
        /// per-mutation upsert-echo call sites + catch-up. Null + empty-string
        /// entries are filtered out before emit — same protective filter as
        /// the entries list.</para>
        ///
        /// <para><b>Sender-naming convention</b> (consumer-lens Lens-2 SF1):
        /// Slice C's <c>AgencyPlanetarySender</c> and Slice D's
        /// <c>AgencyOrbitalSender</c> are client-side siblings of
        /// <see cref="LmpClient.Systems.Agency.AgencyKolonySender"/> — one
        /// per-mutation-surface. Do NOT consolidate them into a single
        /// <c>AgencyMessageSender</c> — per-router profiling + future per-batch
        /// coalescing (pre-spec §11 Q6) need the per-surface boundary. The
        /// server-side outbound (this class) appends per-router echo + catch-up
        /// methods directly here — one class, multiple <c>Send*StateToOwner</c>
        /// methods.</para>
        /// </summary>
        public static void SendKolonyStateToOwner(
            ClientStructure owner,
            Guid agencyId,
            IReadOnlyList<AgencyKolonyEntry> entries,
            IReadOnlyList<string> removedKeys = null)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (owner == null)
                return;

            var entriesEmpty = entries == null || entries.Count == 0;
            var removedEmpty = removedKeys == null || removedKeys.Count == 0;
            if (entriesEmpty && removedEmpty)
                return;

            // [Round-1 general-lens SHOULD-FIX S2] Filter nulls so EntryCount
            // matches the non-null slot count on the wire. The router's `accepted`
            // list is null-free in practice (line 105 skips nulls before append),
            // but the public API contract must not trust the caller — Slice E-1
            // migration callers pass pre-snapshotted lists from inside their
            // per-agency lock critical section, so they satisfy the non-null
            // contract by construction, but the defensive filter remains
            // load-bearing for ad-hoc callers.
            var nonNullEntries = entriesEmpty
                ? null
                : new List<AgencyKolonyEntry>(entries.Count);
            if (!entriesEmpty)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i] != null) nonNullEntries.Add(entries[i]);
                }
            }

            // [Phase 3 Slice E-1] Same protective filter for the removal-keys
            // tail — skip nulls + empty strings so the wire RemovedKolonyKeyCount
            // matches the populated slot count.
            var nonNullRemoved = removedEmpty
                ? null
                : new List<string>(removedKeys.Count);
            if (!removedEmpty)
            {
                for (var i = 0; i < removedKeys.Count; i++)
                {
                    if (!string.IsNullOrEmpty(removedKeys[i])) nonNullRemoved.Add(removedKeys[i]);
                }
            }

            var emitEntries = nonNullEntries != null && nonNullEntries.Count > 0;
            var emitRemoved = nonNullRemoved != null && nonNullRemoved.Count > 0;
            if (!emitEntries && !emitRemoved)
                return;

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyKolonyStateMsgData>();
            msgData.AgencyId = agencyId;
            msgData.EntryCount = emitEntries ? nonNullEntries.Count : 0;
            msgData.Entries = emitEntries ? nonNullEntries.ToArray() : new AgencyKolonyEntry[0];
            msgData.RemovedKolonyKeyCount = emitRemoved ? nonNullRemoved.Count : 0;
            msgData.RemovedKolonyKeys = emitRemoved ? nonNullRemoved.ToArray() : new string[0];

            MessageQueuer.SendToClient<AgencySrvMsg>(owner, msgData);
        }

        /// <summary>
        /// [Phase 3 Slice B] Connect-time catch-up: ships the owner's persisted
        /// <c>AgencyState.KolonyEntries</c> dictionary as a single batch
        /// <see cref="AgencyKolonyStateMsgData"/>. Wired into
        /// <c>HandshakeSystem.HandleHandshakeRequest</c> immediately after the
        /// Stage 5.17d <see cref="SendContractCatchupTo"/> call so the
        /// pre-5.18-series client mirror lands with a complete per-agency kolony
        /// view before any mid-session mutation arrives.
        ///
        /// <para><b>Sends unconditionally under gate=on, even for empty
        /// dictionaries.</b> A pre-Slice-B client mirror author needs the empty
        /// state to distinguish "no per-agency kolony yet" from "server didn't
        /// send catch-up." Same shape as the Stage 5.17d contract catch-up
        /// pre-spec contract.</para>
        ///
        /// <para><b>Client apply: REPLACE, not merge</b> (Round-1
        /// integration-logic SHOULD FIX, Phase 3 Slice E-2 review). A
        /// returning owner whose server-side dict was mutated while offline
        /// (e.g. an admin /setvesselagency migrated some of their entries
        /// OUT to a different agency) must see the post-mutation state
        /// authoritatively. The client's 5.18b-style mirror MUST apply this
        /// message as a full REPLACE of its per-agency kolony dict — not a
        /// merge against stale cached entries. Without REPLACE semantics,
        /// an entry the server removed (migrated out) survives in the
        /// client mirror as orphan UI state until the next mid-session
        /// removal echo (which may never come if the command landed during
        /// the offline window).</para>
        ///
        /// <para><b>No defensive copy needed</b> — unlike contract entries,
        /// kolony entries have no mutable byte-array fields. We snapshot the
        /// dict's <c>.Values</c> array under the per-agency lock so a concurrent
        /// router upsert can't tear our iteration, then ship the snapshot.</para>
        /// </summary>
        public static void SendKolonyCatchupTo(ClientStructure client, AgencyState state)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (client == null || state == null)
                return;

            AgencyKolonyEntry[] snapshot;
            lock (AgencySystem.GetAgencyLock(state.AgencyId))
            {
                // .Values.ToArray() under the lock — concurrent router mutations
                // are serialized against this read so the snapshot is coherent.
                // The per-agency lock contract on AgencyState.KolonyEntries
                // (AgencyState.cs:166-169) requires this read pattern.
                snapshot = new AgencyKolonyEntry[state.KolonyEntries.Count];
                var i = 0;
                foreach (var kvp in state.KolonyEntries)
                {
                    if (kvp.Value == null) continue;
                    snapshot[i++] = kvp.Value;
                }
                // Resize down if any null values were skipped (defensive — the
                // dict should not hold null values under the router's contract).
                if (i < snapshot.Length)
                    Array.Resize(ref snapshot, i);
            }

            // Always send under gate=on, even with zero entries. The pre-Slice-B
            // client mirror author note in AgencyKolonyStateMsgData XML calls this
            // out — empty state distinguishes "no per-agency kolony yet" from
            // "unsynced".
            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyKolonyStateMsgData>();
            msgData.AgencyId = state.AgencyId;
            msgData.EntryCount = snapshot.Length;
            msgData.Entries = snapshot;

            MessageQueuer.SendToClient<AgencySrvMsg>(client, msgData);
        }

        /// <summary>
        /// [Phase 3 Slice C] Owner-only echo of a per-agency planetary entry
        /// batch. Emitted by <see cref="AgencyPlanetaryRouter.TryRoute"/> after
        /// a successful upsert + persist. Peers never receive another agency's
        /// per-agency planetary entries (spec §10 Q1 PrivateAgencyResources=true)
        /// — projection through <see cref="AgencyScenarioProjector"/>'s
        /// <c>PlanetaryLogisticsScenario</c> case is the only path by which
        /// cross-agency awareness leaks into the read-side, and projection
        /// happens per-target-client at scene-load time.
        ///
        /// <para>No-op when:</para>
        /// <list type="bullet">
        ///   <item><see cref="AgencySystem.PerAgencyEnabled"/> is false — gate=off
        ///        OR non-Career game mode. Dual-mode silence preserved.</item>
        ///   <item>The owner client is null (call from a code path with no source
        ///        client — should not happen for the router, defensive).</item>
        ///   <item>The batch is null or empty.</item>
        /// </list>
        ///
        /// <para><b>Caller contract on <paramref name="entries"/>:</b> the sender
        /// filters out null entries so a caller building a list across a
        /// fallible upsert loop doesn't desync the wire's <c>EntryCount</c> vs
        /// the non-null slot count. Same protective filter as
        /// <see cref="SendKolonyStateToOwner"/>.</para>
        /// </summary>
        public static void SendPlanetaryStateToOwner(
            ClientStructure owner,
            Guid agencyId,
            IReadOnlyList<AgencyPlanetaryEntry> entries,
            IReadOnlyList<string> removedKeys = null)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (owner == null)
                return;

            var entriesEmpty = entries == null || entries.Count == 0;
            var removedEmpty = removedKeys == null || removedKeys.Count == 0;
            if (entriesEmpty && removedEmpty)
                return;

            // Filter nulls so EntryCount matches the non-null slot count on the
            // wire. Mirrors SendKolonyStateToOwner's protective filter.
            var nonNullEntries = entriesEmpty
                ? null
                : new List<AgencyPlanetaryEntry>(entries.Count);
            if (!entriesEmpty)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i] != null) nonNullEntries.Add(entries[i]);
                }
            }

            // [Phase 3 Slice E-1] Removal-tail filter. No Slice E-1 producer
            // emits to this path (Q2 NO-MIGRATE for planetary on
            // transferagency); the parameter exists for the future
            // cleanplanetaryentries admin command symmetry with the kolony +
            // orbital siblings.
            var nonNullRemoved = removedEmpty
                ? null
                : new List<string>(removedKeys.Count);
            if (!removedEmpty)
            {
                for (var i = 0; i < removedKeys.Count; i++)
                {
                    if (!string.IsNullOrEmpty(removedKeys[i])) nonNullRemoved.Add(removedKeys[i]);
                }
            }

            var emitEntries = nonNullEntries != null && nonNullEntries.Count > 0;
            var emitRemoved = nonNullRemoved != null && nonNullRemoved.Count > 0;
            if (!emitEntries && !emitRemoved)
                return;

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyPlanetaryStateMsgData>();
            msgData.AgencyId = agencyId;
            msgData.EntryCount = emitEntries ? nonNullEntries.Count : 0;
            msgData.Entries = emitEntries ? nonNullEntries.ToArray() : new AgencyPlanetaryEntry[0];
            msgData.RemovedPlanetaryKeyCount = emitRemoved ? nonNullRemoved.Count : 0;
            msgData.RemovedPlanetaryKeys = emitRemoved ? nonNullRemoved.ToArray() : new string[0];

            MessageQueuer.SendToClient<AgencySrvMsg>(owner, msgData);
        }

        /// <summary>
        /// [Phase 3 Slice C] Connect-time catch-up: ships the owner's persisted
        /// <c>AgencyState.PlanetaryEntries</c> dictionary as a single batch
        /// <see cref="AgencyPlanetaryStateMsgData"/>. Wired into
        /// <c>HandshakeSystem.HandleHandshakeRequest</c> immediately after the
        /// Slice B <see cref="SendKolonyCatchupTo"/> call so the pre-5.18-series
        /// client mirror lands with a complete per-agency planetary view before
        /// any mid-session mutation arrives.
        ///
        /// <para><b>Sends unconditionally under gate=on, even for empty
        /// dictionaries.</b> Same shape as
        /// <see cref="SendKolonyCatchupTo"/> — a pre-Slice-C client mirror
        /// author needs the empty state to distinguish "no per-agency
        /// planetary balances yet" from "server didn't send catch-up."</para>
        ///
        /// <para><b>Client apply: REPLACE, not merge</b> — same rationale
        /// as <see cref="SendKolonyCatchupTo"/>. Offline owners may have had
        /// their planetary partition mutated by admin commands (or by
        /// hand-edit on disk); the mirror must replace its cached dict
        /// wholesale, not merge.</para>
        ///
        /// <para><b>No defensive copy needed</b> — planetary entries have no
        /// mutable byte-array fields (4 small fields: 1 Guid + 1 int + 1
        /// string + 1 double). We snapshot the dict's <c>.Values</c> array
        /// under the per-agency lock so a concurrent router upsert can't tear
        /// our iteration, then ship the snapshot.</para>
        /// </summary>
        public static void SendPlanetaryCatchupTo(ClientStructure client, AgencyState state)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (client == null || state == null)
                return;

            AgencyPlanetaryEntry[] snapshot;
            lock (AgencySystem.GetAgencyLock(state.AgencyId))
            {
                snapshot = new AgencyPlanetaryEntry[state.PlanetaryEntries.Count];
                var i = 0;
                foreach (var kvp in state.PlanetaryEntries)
                {
                    if (kvp.Value == null) continue;
                    snapshot[i++] = kvp.Value;
                }
                if (i < snapshot.Length)
                    Array.Resize(ref snapshot, i);
            }

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyPlanetaryStateMsgData>();
            msgData.AgencyId = state.AgencyId;
            msgData.EntryCount = snapshot.Length;
            msgData.Entries = snapshot;

            MessageQueuer.SendToClient<AgencySrvMsg>(client, msgData);
        }

        /// <summary>
        /// [Phase 3 Slice D] Owner-only echo of a per-agency orbital transfer
        /// batch. Emitted by <see cref="AgencyOrbitalRouter.TryRoute"/> after
        /// a successful upsert + persist. Peers never receive another agency's
        /// per-agency orbital transfers (spec §10 Q1
        /// PrivateAgencyResources=true) — projection through
        /// <see cref="AgencyScenarioProjector"/>'s
        /// <c>ScenarioOrbitalLogistics</c> case is the only path by which
        /// cross-agency awareness leaks into the read-side, and projection
        /// happens per-target-client at scene-load time.
        ///
        /// <para>No-op when:</para>
        /// <list type="bullet">
        ///   <item><see cref="AgencySystem.PerAgencyEnabled"/> is false — gate=off
        ///        OR non-Career game mode. Dual-mode silence preserved.</item>
        ///   <item>The owner client is null (call from a code path with no source
        ///        client — should not happen for the router, defensive).</item>
        ///   <item>The batch is null or empty.</item>
        /// </list>
        ///
        /// <para><b>Caller contract on <paramref name="entries"/>:</b> the
        /// sender filters out null entries so a caller building a list across
        /// a fallible upsert loop doesn't desync the wire's
        /// <c>EntryCount</c> vs the non-null slot count. Same protective
        /// filter as <see cref="SendKolonyStateToOwner"/> +
        /// <see cref="SendPlanetaryStateToOwner"/>.</para>
        ///
        /// <para><b>No defensive copy of PayloadBytes</b> — the router has
        /// already <c>Buffer.BlockCopy</c>'d each entry's buffer into a stable
        /// per-agency-state buffer before calling here (per
        /// <see cref="AgencyOrbitalRouter.TryRoute"/>'s defensive-copy step).
        /// The wire serializer reads from those same buffers — no aliasing
        /// hazard between dict storage and wire emit on the same thread under
        /// the per-agency lock that the router has already released by the
        /// time this method is called (post-Send-and-return path).</para>
        /// </summary>
        public static void SendOrbitalStateToOwner(
            ClientStructure owner,
            Guid agencyId,
            IReadOnlyList<AgencyOrbitalTransferEntry> entries,
            IReadOnlyList<Guid> removedTransferGuids = null)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (owner == null)
                return;

            var entriesEmpty = entries == null || entries.Count == 0;
            var removedEmpty = removedTransferGuids == null || removedTransferGuids.Count == 0;
            if (entriesEmpty && removedEmpty)
                return;

            // Filter nulls so EntryCount matches the non-null slot count on the
            // wire. Mirrors SendKolonyStateToOwner / SendPlanetaryStateToOwner.
            var nonNullEntries = entriesEmpty
                ? null
                : new List<AgencyOrbitalTransferEntry>(entries.Count);
            if (!entriesEmpty)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i] != null) nonNullEntries.Add(entries[i]);
                }
            }

            // [Phase 3 Slice E-1] Removal-tail filter. Guid keys can't be
            // "null", but Guid.Empty is the sentinel + would be undefined
            // semantically (the dict is never keyed by Empty under normal
            // operation). Skip Empty entries defensively — a caller that
            // passes Empty has confused the transfer-guid with the
            // Unassigned-vessel sentinel.
            var nonNullRemoved = removedEmpty
                ? null
                : new List<Guid>(removedTransferGuids.Count);
            if (!removedEmpty)
            {
                for (var i = 0; i < removedTransferGuids.Count; i++)
                {
                    if (removedTransferGuids[i] != Guid.Empty) nonNullRemoved.Add(removedTransferGuids[i]);
                }
            }

            var emitEntries = nonNullEntries != null && nonNullEntries.Count > 0;
            var emitRemoved = nonNullRemoved != null && nonNullRemoved.Count > 0;
            if (!emitEntries && !emitRemoved)
                return;

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
            msgData.AgencyId = agencyId;
            msgData.EntryCount = emitEntries ? nonNullEntries.Count : 0;
            msgData.Entries = emitEntries ? nonNullEntries.ToArray() : new AgencyOrbitalTransferEntry[0];
            msgData.RemovedTransferCount = emitRemoved ? nonNullRemoved.Count : 0;
            msgData.RemovedTransferGuids = emitRemoved ? nonNullRemoved.ToArray() : new Guid[0];

            MessageQueuer.SendToClient<AgencySrvMsg>(owner, msgData);
        }

        /// <summary>
        /// [Phase 3 Slice D] Connect-time catch-up: ships the owner's persisted
        /// <c>AgencyState.OrbitalTransfers</c> dictionary as a single batch
        /// <see cref="AgencyOrbitalStateMsgData"/>. Wired into
        /// <c>HandshakeSystem.HandleHandshakeRequest</c> immediately after the
        /// Slice C <see cref="SendPlanetaryCatchupTo"/> call so the
        /// pre-5.18-series client mirror lands with a complete per-agency
        /// orbital view before any per-frame
        /// <c>ScenarioOrbitalLogistics.Update</c> cycle.
        ///
        /// <para><b>Sends unconditionally under gate=on, even for empty
        /// dictionaries.</b> Same shape as <see cref="SendKolonyCatchupTo"/> +
        /// <see cref="SendPlanetaryCatchupTo"/> — a pre-Slice-D client mirror
        /// author needs the empty state to distinguish "no per-agency orbital
        /// transfers yet" from "server didn't send catch-up."</para>
        ///
        /// <para><b>Client apply: REPLACE, not merge</b> — same rationale
        /// as <see cref="SendKolonyCatchupTo"/>. An offline owner whose
        /// orbital partition lost entries (admin /setvesselagency migrated
        /// them to a different agency) or gained entries (the reverse)
        /// must see the post-mutation state authoritatively; merging
        /// against the stale cache leaves orphan transfers in the UI.</para>
        ///
        /// <para><b>Defensive copy of PayloadBytes per entry</b> — orbital is
        /// the only Phase 3 entry with a mutable byte-array field. Each
        /// snapshot entry's PayloadBytes is <c>Buffer.BlockCopy</c>'d into a
        /// fresh buffer so a concurrent router upsert (which would replace
        /// the dict value) can't tear the wire send. The
        /// <see cref="AgencyState.OrbitalTransfers"/> dict value itself is
        /// stable under the per-agency lock during the snapshot, but the
        /// post-snapshot wire serialization happens OUTSIDE the lock — the
        /// per-entry copy isolates the wire pass from a concurrent
        /// dict-replace.</para>
        /// </summary>
        public static void SendOrbitalCatchupTo(ClientStructure client, AgencyState state)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (client == null || state == null)
                return;

            AgencyOrbitalTransferEntry[] snapshot;
            int dictCount;
            lock (AgencySystem.GetAgencyLock(state.AgencyId))
            {
                dictCount = state.OrbitalTransfers.Count;
                snapshot = new AgencyOrbitalTransferEntry[dictCount];
                var i = 0;
                foreach (var kvp in state.OrbitalTransfers)
                {
                    var src = kvp.Value;
                    if (src == null) continue;

                    // Defensive copy of PayloadBytes under the lock. Today there
                    // is no concurrent mutator of an entry's PayloadBytes in
                    // place — the router replaces the dict value with a fresh
                    // entry on every Upsert (AgencyOrbitalRouter.cs:243-260), so
                    // the prior entry's buffer is GC-pinned by our snapshot
                    // reference and a concurrent router-thread does not tear
                    // our wire send. The per-entry copy here is forward-
                    // looking: a future Slice E migration that performs an
                    // in-place PayloadBytes mutation (e.g. partial-transfer
                    // splice during transferagency-MKS) WOULD race the catchup
                    // wire emit; this copy defends against that shape change
                    // before it ships. ~hundreds of bytes per transfer, negligible.
                    var srcBytes = src.PayloadBytes ?? Array.Empty<byte>();
                    var srcLen = Math.Max(0, Math.Min(src.NumBytes, srcBytes.Length));
                    var copyBytes = srcLen > 0 ? new byte[srcLen] : Array.Empty<byte>();
                    if (srcLen > 0)
                        Buffer.BlockCopy(srcBytes, 0, copyBytes, 0, srcLen);

                    snapshot[i++] = new AgencyOrbitalTransferEntry
                    {
                        TransferGuid = src.TransferGuid,
                        OriginVesselId = src.OriginVesselId,
                        DestinationVesselId = src.DestinationVesselId,
                        Status = src.Status,
                        StartTime = src.StartTime,
                        Duration = src.Duration,
                        PayloadBytes = copyBytes,
                        NumBytes = srcLen,
                    };
                }
                if (i < snapshot.Length)
                    Array.Resize(ref snapshot, i);
            }

            // [Upgrade-lens MF3] Operability signal when a Slice E migration bug
            // leaves null values in the dict. Today the dict should never
            // contain nulls (router upsert path always assigns a fresh entry),
            // but a future migration path with a botched intermediate state
            // would silently lose entries here. Log the count discrepancy so
            // the operator has a grep target.
            if (snapshot.Length != dictCount)
            {
                LunaLog.Debug($"[fix:MKS-R2] SendOrbitalCatchupTo: dict count {dictCount} vs non-null snapshot count {snapshot.Length} for agency {state.AgencyId:N} — {dictCount - snapshot.Length} null entries skipped.");
            }

            // [Upgrade-lens MF1] Chunk batches larger than MaxEntryCount so the
            // receiver never throws InvalidDataException at the wire boundary
            // and disconnects the player at handshake completion. Same
            // chunking pattern as BroadcastVisibilityChange below (lines
            // 752-771 in this file). Lidgren's per-channel ReliableOrdered
            // guarantee preserves apply order across the chunk batches.
            var total = snapshot.Length;
            if (total == 0)
            {
                // Empty-batch catchup is part of the gate=on contract per the
                // AgencyOrbitalStateMsgData XML — pre-Slice-D client mirrors
                // need to distinguish "no per-agency transfers yet" from
                // "unsynced". Ship one zero-entry message.
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
                msgData.AgencyId = state.AgencyId;
                msgData.EntryCount = 0;
                msgData.Entries = Array.Empty<AgencyOrbitalTransferEntry>();
                MessageQueuer.SendToClient<AgencySrvMsg>(client, msgData);
                return;
            }

            const int chunkLimit = AgencyOrbitalStateMsgData.MaxEntryCount;
            var batchCount = 0;
            for (var start = 0; start < total; start += chunkLimit)
            {
                var chunkSize = Math.Min(chunkLimit, total - start);
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
                msgData.AgencyId = state.AgencyId;
                msgData.EntryCount = chunkSize;
                msgData.Entries = new AgencyOrbitalTransferEntry[chunkSize];
                Array.Copy(snapshot, start, msgData.Entries, 0, chunkSize);
                MessageQueuer.SendToClient<AgencySrvMsg>(client, msgData);
                batchCount++;
            }

            if (batchCount > 1)
            {
                LunaLog.Normal($"[fix:MKS-R2] SendOrbitalCatchupTo: agency {state.AgencyId:N} has {total} transfers — split into {batchCount} chunks of up to {chunkLimit} for catchup.");
            }
        }

        public static void BroadcastVisibilityChange(IReadOnlyList<VesselOwnershipChange> changes)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return;
            if (changes == null || changes.Count == 0)
                return;

            var total = changes.Count;
            var batchCount = 0;
            for (var start = 0; start < total; start += AgencyVisibilityMsgData.MaxChangeCount)
            {
                var chunkSize = Math.Min(AgencyVisibilityMsgData.MaxChangeCount, total - start);
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyVisibilityMsgData>();
                msgData.ChangeCount = chunkSize;
                msgData.Changes = new VesselOwnershipChange[chunkSize];
                for (var i = 0; i < chunkSize; i++)
                    msgData.Changes[i] = changes[start + i];

                MessageQueuer.SendToAllClients<AgencySrvMsg>(msgData);
                batchCount++;
            }

            // Operator-visible signal for the Stage 5.18+ GUI launcher (which parses
            // server stdout). Without this, an admin command's broadcast is invisible
            // unless the operator correlates against the per-client receive logs.
            var batchSuffix = batchCount > 1 ? $" in {batchCount} batches" : string.Empty;
            LunaLog.Normal($"[fix:per-agency-career] Broadcast ownership-visibility — {total} change(s){batchSuffix}");
        }
    }
}
