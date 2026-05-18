using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Agency;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareUpgradeableFacilitiesSystem
    {
        public static void UpgradeReceived(ClientStructure client, ShareProgressFacilityUpgradeMsgData data)
        {
            LunaLog.Debug($"{client.PlayerName} Upgraded facility {data.FacilityId} To level: {data.Level}");

            // [Stage 5.17e-6] Per-agency routing — each agency has its own
            // facility tier set (spec §2 "Per-agency facility upgrade tiers,
            // shared physical KSC"). Projector overrides the matching facility
            // node's `lvl` value in the outgoing scenario per requesting agency.
            // Destructibles (building damage state) remains shared by design —
            // see Stage 5.17e-7 doc-only commit + AgencyScenarioProjector XML.
            if (AgencyProgressRouter.TryRouteFacilityUpgrade(client, data))
                return;

            //send the upgrade facility update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteFacilityLevelDataToFile(data.FacilityId, data.NormLevel);
        }
    }
}
