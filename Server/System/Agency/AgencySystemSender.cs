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
