using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Agency;
using Server.System.Scenario;

namespace Server.System
{
    public class SharePartPurchaseSystem
    {
        public static void PurchaseReceived(ClientStructure client, ShareProgressPartPurchaseMsgData data)
        {
            LunaLog.Debug($"Part purchased: {data.PartName} Tech: {data.TechId}");

            // [Stage 5.17e-5] Per-agency routing — persist to AgencyState.PurchasedParts
            // (keyed by TechId, value is the part set). Projector merges these into
            // per-agency Tech blocks during R&D splice so the parts appear under
            // the right tech node on the client's next scene-load.
            if (AgencyResearchRouter.TryRoutePartPurchase(client, data))
                return;

            //send the part purchase to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WritePartPurchaseDataToFile(data);
        }
    }
}
