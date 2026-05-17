using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Locks;
using LmpCommon.Message.Data.PlayerConnection;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server;
using Server.Context;
using Server.Log;
using Server.Plugin;
using Server.Server;
using Server.System;
using System;
using System.Collections.Generic;

namespace Server.Client
{
    public class ClientConnectionHandler
    {
        public static void ConnectClient(NetConnection newClientConnection)
        {
            var newClientObject = new ClientStructure(newClientConnection);

            LmpPluginHandler.FireOnClientConnect(newClientObject);

            ServerContext.Clients.TryAdd(newClientObject.Endpoint, newClientObject);
            LunaLog.Debug($"Online Players: {ServerContext.PlayerCount}, connected: {ServerContext.Clients.Count}");
        }

        public static void DisconnectClient(ClientStructure client, string reason = "")
        {
            if (!string.IsNullOrEmpty(reason))
                LunaLog.Debug($"{client.PlayerName} sent Connection end message, reason: {reason}");

            //Remove Clients from list
            if (ServerContext.Clients.ContainsKey(client.Endpoint))
            {
                ServerContext.Clients.TryRemove(client.Endpoint, out client);
                LunaLog.Debug($"Online Players: {ServerContext.PlayerCount}, connected: {ServerContext.Clients.Count}");
            }

            if (client.ConnectionStatus != ConnectionStatus.Disconnected)
            {
                client.ConnectionStatus = ConnectionStatus.Disconnected;
                LmpPluginHandler.FireOnClientDisconnect(client);
                if (client.Authenticated)
                {
                    var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<PlayerConnectionLeaveMsgData>();
                    msgData.PlayerName = client.PlayerName;

                    MessageQueuer.RelayMessage<PlayerConnectionSrvMsg>(client, msgData);

                    //BUG-010: tell every remaining client which vessels were under the leaving
                    //player's locks BEFORE we release those locks. Recipients pin the named
                    //vessels immortal so the lock-release storm that follows does not flip
                    //SetImmortalStateBasedOnLock and hand a stressed-physics vessel to KSP's
                    //integrator. The pin self-clears when any player takes Control/Update on
                    //the vessel — including the original pilot on reconnect.
                    //See docs/research/02-analysis/bug-010-disconnect-vessel-handoff.md.
                    BroadcastPinnedVessels(client, reason);

                    LockSystem.ReleasePlayerLocks(client);
                    WarpSystem.RemoveSubspace(client.Subspace);
                }

                try
                {
                    client.Connection?.Disconnect(reason);
                }
                catch (Exception e)
                {
                    LunaLog.Error($"Error closing client Connection: {e.Message}");
                }
            }

            //As this is the last client that is connected to the server, run a safety backup once they disconnect
            if (ServerContext.Clients.Count == 0)
            {
                BackupSystem.RunBackup();
            }
        }

        private static void BroadcastPinnedVessels(ClientStructure leavingClient, string disconnectReason)
        {
            HashSet<Guid> vesselIds = null;
            foreach (var lockDef in LockSystem.LockQuery.GetAllPlayerLocks(leavingClient.PlayerName))
            {
                if (lockDef.VesselId == Guid.Empty) continue;
                if (lockDef.Type != LockType.Control && lockDef.Type != LockType.Update && lockDef.Type != LockType.UnloadedUpdate)
                    continue;

                if (vesselIds == null) vesselIds = new HashSet<Guid>();
                vesselIds.Add(lockDef.VesselId);
            }

            if (vesselIds == null) return;

            //Vessels in the dekessler/recovered kill-list should not be pinned — they are
            //about to be deleted from the universe and a pin would re-immortalise a corpse.
            var deadVessels = VesselContext.RemovedVessels;

            //The disconnect reason is operator-supplied (clean quit message) or
            //Lidgren-supplied (timeout / protocol-error string) — neither is bounded.
            //Cap before broadcasting so a 4 KB reason doesn't amplify into N clients ×
            //M pinned vessels of wire traffic + log spam.
            var pinReason = string.IsNullOrEmpty(disconnectReason) ? "client disconnected" : disconnectReason;
            if (pinReason.Length > 256) pinReason = pinReason.Substring(0, 256);

            foreach (var vesselId in vesselIds)
            {
                if (deadVessels != null && deadVessels.ContainsKey(vesselId)) continue;

                var pinMsg = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselPinnedMsgData>();
                pinMsg.VesselId = vesselId;
                pinMsg.AbsentPlayerName = leavingClient.PlayerName;
                pinMsg.Reason = pinReason;

                MessageQueuer.RelayMessage<VesselSrvMsg>(leavingClient, pinMsg);

                LunaLog.Debug($"[fix:BUG-010] Vessel {vesselId} pinned until {leavingClient.PlayerName} returns (reason: {pinReason})");
            }
        }
    }
}
