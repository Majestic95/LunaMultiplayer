using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Agency;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareStrategySystem
    {
        public static void StrategyReceived(ClientStructure client, ShareProgressStrategyMsgData data)
        {
            LunaLog.Debug($"strategy changed: {data.Strategy.Name}");

            // [Stage 5.17e-6] Per-agency routing — strategies belong to the
            // sender's agency only; peers don't gain a copy.
            if (AgencyProgressRouter.TryRouteStrategy(client, data))
                return;

            //Send the strategy update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteStrategyDataToFile(data.Strategy);
        }
    }
}
