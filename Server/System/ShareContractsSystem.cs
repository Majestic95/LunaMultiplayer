using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Agency;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareContractsSystem
    {
        public static void ContractsReceived(ClientStructure client, ShareProgressContractsMsgData data)
        {
            LunaLog.Debug("Contract data received:");

            foreach (var item in data.Contracts)
            {
                LunaLog.Debug(item.ContractGuid.ToString());
            }

            // Stage 5.17d — when PerAgencyCareer=true, the AgencyContractRouter classifies
            // each contract: Offered/Generated stay in the shared scenario (CC's
            // ContractPreLoader still sees the world it expects); Active / Completed /
            // Failed / Cancelled / DeadlineExpired / Withdrawn route into the sender's
            // per-agency state and echo via owner-only AgencyContractMsgData. Under
            // gate=on we must NOT relay the inbound to peers — that would broadcast
            // another agency's private contract state (spec §10 Q1). TryRoute returns
            // false when gate=off so the existing shared-agency path runs unchanged.
            if (AgencyContractRouter.TryRoute(client, data))
                return;

            //send the contract update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteContractDataToFile(data);
        }
    }
}
