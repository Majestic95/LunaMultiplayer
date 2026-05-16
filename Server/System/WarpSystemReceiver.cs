using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;

namespace Server.System
{
    public class WarpSystemReceiver
    {
        private static readonly object CreateSubspaceLock = new object();

        public void HandleNewSubspace(ClientStructure client, WarpNewSubspaceMsgData message)
        {
            lock (CreateSubspaceLock)
            {
                if (message.PlayerCreator != client.PlayerName) return;

                //If the client supplied a request-seq and we've already minted for it, re-deliver
                //the original assignment to the requester only. Other clients already saw the
                //original broadcast; minting again would create an orphan subspace. See BUG-051a.
                if (WarpRequestCache.TryGet(client.PlayerName, message.RequestSeq, out var cachedId, out var cachedTimeDiff)
                    && WarpContext.Subspaces.ContainsKey(cachedId))
                {
                    LunaLog.Debug($"[WarpSystem]: dedupe hit for {client.PlayerName} seq={message.RequestSeq} -> subspace {cachedId}");

                    var replay = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
                    replay.PlayerCreator = message.PlayerCreator;
                    replay.SubspaceKey = cachedId;
                    replay.ServerTimeDifference = cachedTimeDiff;
                    replay.RequestSeq = message.RequestSeq;

                    MessageQueuer.SendToClient<WarpSrvMsg>(client, replay);
                    return;
                }

                LunaLog.Debug($"{client.PlayerName} created the new subspace '{WarpContext.NextSubspaceId}'");

                //Create Subspace
                WarpContext.Subspaces.TryAdd(WarpContext.NextSubspaceId, new Subspace(WarpContext.NextSubspaceId, message.ServerTimeDifference, client.PlayerName));

                //Tell all Clients about the new Subspace
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
                msgData.ServerTimeDifference = message.ServerTimeDifference;
                msgData.PlayerCreator = message.PlayerCreator;
                msgData.SubspaceKey = WarpContext.NextSubspaceId;
                msgData.RequestSeq = message.RequestSeq;

                MessageQueuer.SendToAllClients<WarpSrvMsg>(msgData);

                //Cache the (player, seq) -> subspace assignment so a retry returns the same id.
                //No-op when message.RequestSeq == 0 (pre-fix client).
                WarpRequestCache.Add(client.PlayerName, message.RequestSeq, WarpContext.NextSubspaceId, message.ServerTimeDifference);

                WarpContext.NextSubspaceId++;
            }
        }

        public void HandleChangeSubspace(ClientStructure client, WarpChangeSubspaceMsgData message)
        {
            if (message.PlayerName != client.PlayerName) return;

            var oldSubspace = client.Subspace;
            var newSubspace = message.Subspace;

            if (oldSubspace != newSubspace)
            {
                if (newSubspace < 0)
                    LunaLog.Debug($"{client.PlayerName} is warping");
                else if (WarpContext.Subspaces[newSubspace].Creator != client.PlayerName)
                    LunaLog.Debug($"{client.PlayerName} synced with subspace '{message.Subspace}' created by {WarpContext.Subspaces[newSubspace].Creator}");

                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpChangeSubspaceMsgData>();
                msgData.PlayerName = client.PlayerName;
                msgData.Subspace = message.Subspace;

                MessageQueuer.RelayMessage<WarpSrvMsg>(client, msgData);

                if (newSubspace != -1)
                {
                    client.Subspace = newSubspace;

                    //Try to remove their old subspace
                    WarpSystem.RemoveSubspace(oldSubspace);
                }
            }
        }

        public void HandleSubspaceRequest(ClientStructure client)
        {
            lock (CreateSubspaceLock)
            {
                WarpSystemSender.SendAllSubspaces(client);
            }
        }
    }
}
