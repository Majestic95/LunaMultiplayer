using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Agency;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareScienceSystem
    {
        public static void ScienceReceived(ClientStructure client, ShareProgressScienceMsgData data)
        {
            LunaLog.Debug($"Science received: {data.Science} Reason: {data.Reason}");

            // [Stage 5.17e-3] When per-agency career is active, route the mutation to
            // the sender's AgencyState (Science field) + owner-only state echo + skip
            // the shared-scenario broadcast/write entirely. The router returns false
            // when the gate is off OR the sender has no agency mapping, in which case
            // we fall through to the unchanged shared-agency path. Closes the same leak
            // pattern as ShareFundsSystem (audit:
            // docs/research/05b-ksp-career-surface-audit.md).
            if (AgencyCurrencyRouter.TryRouteScience(client, data))
                return;

            //send the science update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteScienceDataToFile(data.Science);
        }
    }
}
