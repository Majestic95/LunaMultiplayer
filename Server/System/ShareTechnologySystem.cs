using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareTechnologySystem
    {
        public static void TechnologyReceived(ClientStructure client, ShareProgressTechnologyMsgData data)
        {
            // [fix:BUG-025] Synchronously check whether this tech is already unlocked
            // server-side. If so, the sender locally deducted science before broadcasting
            // and we tell them to refund. If not, this is the first purchase and we
            // proceed with the historical relay + persist behaviour.
            var (added, costInPayload) = ScenarioDataUpdater.TryAddTechnologyAtomic(data);

            if (!added)
            {
                var rejection = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressTechnologyRejectedMsgData>();
                rejection.TechId = data.TechNode.Id;
                rejection.RefundScience = costInPayload;
                MessageQueuer.SendToClient<ShareProgressSrvMsg>(client, rejection);
                LunaLog.Normal($"[fix:BUG-025] Rejected duplicate tech purchase {data.TechNode.Id} from {client.PlayerName}; refunding {costInPayload} science");
                return;
            }

            LunaLog.Debug($"Technology unlocked: {data.TechNode.Id}");
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
        }
    }
}
