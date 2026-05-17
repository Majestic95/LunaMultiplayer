using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
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
    /// <see cref="GameplaySettingsDefinition.PerAgencyCareer"/> is false. Same shape
    /// as the lifecycle methods on <see cref="AgencySystem"/>.
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
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
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
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
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
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
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
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
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
    }
}
