using LmpCommon.Enums;
using LmpCommon.Message.Interface;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using System.Linq;

namespace Server.Server
{
    public class MessageQueuer
    {
        /// <summary>
        /// [perf:relay-scene Phase 1] Sends a message to all the clients except the
        /// one given as parameter, FILTERED on recipient's current KSP scene.
        /// Recipients whose scene cannot render continuous vessel-state (i.e. not
        /// Flight or TrackingStation) are skipped.
        ///
        /// Pre-Phase-1 clients (Scene=Unknown — they don't ship the tail-byte) are
        /// treated as "relay always" to preserve the baseline broadcast behaviour;
        /// see <see cref="ShouldReceiveVesselUpdate"/>.
        ///
        /// When <c>OptimizationSettings.SceneAwareRelayEnabled</c>=false, falls back
        /// to <see cref="RelayMessage{T}"/> (operator escape hatch — restores the
        /// pre-Phase-1 baseline without a binary downgrade).
        ///
        /// Intended call sites: continuous vessel-STATE updates only —
        /// <c>VesselMsgReader</c> cases for Position / Flightstate / Update /
        /// Resource / PartSync{Field,UiField,Call} / ActionGroup / Fairing
        /// (9 sites). Structural / registry-mutating relays (Proto / Sync /
        /// Couple / Remove / **Decouple / Undock**) MUST stay on
        /// <see cref="RelayMessage{T}"/> — recipients need them in every scene
        /// so that scene entry into Flight finds a populated
        /// <c>FlightGlobals.Vessels</c>. Don't extend this to Decouple/Undock
        /// without breaking docking-handoff sync for non-Flight recipients.
        /// </summary>
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

        /// <summary>
        /// ClientStructure wrapper for <see cref="ShouldRelayToScene"/>. Reads the
        /// recipient's last-reported scene off <c>PlayerStatus.Scene</c> (set by
        /// <see cref="Server.Message.PlayerStatusMsgReader.HandleMessage"/> on every
        /// inbound PlayerStatusSet) and delegates the gate decision.
        ///
        /// Caller already gates on <c>OptimizationSettings.SceneAwareRelayEnabled</c>
        /// so this helper doesn't re-check it.
        /// </summary>
        internal static bool ShouldReceiveVesselUpdate(ClientStructure recipient)
        {
            var scene = recipient?.PlayerStatus?.Scene ?? ClientSceneType.Unknown;
            return ShouldRelayToScene(scene);
        }

        /// <summary>
        /// Pure helper (Phase 1 — server-side-offload). True iff a recipient
        /// reporting <paramref name="scene"/> should receive a continuous vessel-
        /// state relay (Position / Flightstate / Update / Resource / PartSync* /
        /// ActionGroup / Fairing).
        ///
        /// Three branches:
        ///   - <see cref="ClientSceneType.Unknown"/> → true (pre-Phase-1 client or
        ///     post-connect pre-first-SetScene window — relay always for compat).
        ///   - <see cref="ClientSceneType.Flight"/> / <see cref="ClientSceneType.TrackingStation"/>
        ///     → true (recipient will actually render the relayed state).
        ///   - Any other scene (MainMenu / SpaceCenter / Editor / RD / Mission /
        ///     Other) → false. None of these render remote vessel state, so the
        ///     recipient would discard the message after decode.
        ///
        /// Public for ServerTest direct invocation (mirrors the
        /// <c>WarpSystem.DetectSoloTransitions</c> pure-helper pattern from
        /// the per-agency workstream — extract decision math to a no-side-
        /// effects signature so unit tests don't construct full ClientStructures
        /// + NetConnections to exercise it).
        /// </summary>
        public static bool ShouldRelayToScene(ClientSceneType scene)
        {
            //Compat: pre-Phase-1 client (didn't write the Scene byte → server
            //deserialised Unknown) OR post-connect pre-first-SetScene window
            //(server hasn't received the first PlayerStatus.Set yet). Both
            //resolve to "relay always" — gives the recipient at least one tick
            //of full state before any potential filtering kicks in.
            if (scene == ClientSceneType.Unknown) return true;
            //SpaceCenter excluded — KSC's in-scene active-vessel marker is for the
            //LOCAL active vessel on a separate code path, not a relay-driven
            //rendering of remote vessels (spec §3.d Q2). The full list of
            //non-relay scenes is MainMenu / SpaceCenter / Editor / RD / Mission /
            //Other; all of them discard continuous vessel state after decode.
            return scene == ClientSceneType.Flight || scene == ClientSceneType.TrackingStation;
        }

        /// <summary>
        /// Sends a message to all the clients except the one given as parameter that are in the same subspace
        /// </summary>
        public static void RelayMessageToSubspace<T>(ClientStructure exceptClient, IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            RelayMessageToSubspace<T>(exceptClient, data, exceptClient.Subspace);
        }

        /// <summary>
        /// Sends a message to all the clients in the given subspace
        /// </summary>
        public static void SendMessageToSubspace<T>(IMessageData data, int subspace) where T : class, IServerMessageBase
        {
            if (data == null) return;

            foreach (var otherClient in ServerContext.Clients.Values.Where(c => c.Subspace == subspace))
                SendToClient(otherClient, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Sends a message to all the clients except the one given as parameter that are in the subspace given as parameter
        /// </summary>
        public static void RelayMessageToSubspace<T>(ClientStructure exceptClient, IMessageData data, int subspace) where T : class, IServerMessageBase
        {
            if (data == null) return;

            foreach (var otherClient in ServerContext.Clients.Values.Where(c => !Equals(c, exceptClient) && c.Subspace == subspace))
                SendToClient(otherClient, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Sends a message to all the clients except the one given as parameter
        /// </summary>
        public static void RelayMessage<T>(ClientStructure exceptClient, IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            foreach (var otherClient in ServerContext.Clients.Values.Where(c => !Equals(c, exceptClient)))
                SendToClient(otherClient, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Sends a message to all the clients
        /// </summary>
        public static void SendToAllClients<T>(IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            foreach (var otherClient in ServerContext.Clients.Values)
                SendToClient(otherClient, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Sends a message to the given client
        /// </summary>
        public static void SendToClient<T>(ClientStructure client, IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            SendToClient(client, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Disconnects the given client
        /// </summary>
        public static void SendConnectionEnd(ClientStructure client, string reason)
        {
            ClientConnectionHandler.DisconnectClient(client, reason);
        }

        /// <summary>
        /// Disconnect all clients
        /// </summary>
        public static void SendConnectionEndToAll(string reason)
        {
            foreach (var client in ClientRetriever.GetAuthenticatedClients())
                SendConnectionEnd(client, reason);
        }

        #region Private

        private static void SendToClient(ClientStructure client, IServerMessageBase msg)
        {
            if (msg?.Data == null) return;

            client?.SendMessageQueue.Enqueue(msg);
        }

        private static T GenerateMessage<T>(IMessageData data) where T : class, IServerMessageBase
        {
            var newMessage = ServerContext.ServerMessageFactory.CreateNew<T>(data);
            return newMessage;
        }

        #endregion
    }
}