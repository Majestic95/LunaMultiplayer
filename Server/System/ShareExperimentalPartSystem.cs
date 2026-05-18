using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Agency;
using Server.System.Scenario;

namespace Server.System
{
    public class ShareExperimentalPartSystem
    {
        public static void ExperimentalPartReceived(ClientStructure client, ShareProgressExperimentalPartMsgData data)
        {
            LunaLog.Debug($"Experimental part received: {data.PartName} Count: {data.Count}");

            // [Stage 5.17e-5] Per-agency routing — persist to AgencyState.ExperimentalParts
            // (keyed by PartName, value is count). Count==0 removes the entry (matches
            // the shared-scenario writer). Projector splices the ExpParts block into
            // outgoing R&D scenarios on next scene-load.
            if (AgencyResearchRouter.TryRouteExperimentalPart(client, data))
                return;

            //send the experimental part to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteExperimentalPartDataToFile(data);
        }
    }
}
