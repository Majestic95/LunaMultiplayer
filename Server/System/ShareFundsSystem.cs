using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Agency;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareFundsSystem
    {
        public static void FundsReceived(ClientStructure client, ShareProgressFundsMsgData data)
        {
            LunaLog.Debug($"Funds received: {data.Funds} Reason: {data.Reason}");

            // [Stage 5.17e-3] When per-agency career is active, route the mutation to
            // the sender's AgencyState (Funds field) + owner-only state echo + skip the
            // shared-scenario broadcast/write entirely. The router returns false when
            // the gate is off OR the sender has no agency mapping, in which case we
            // fall through to the unchanged shared-agency path. Closes the leak where
            // a mid-session Funds change broadcast to every peer and clobbered their
            // local totals (audit: docs/research/05b-ksp-career-surface-audit.md).
            if (AgencyCurrencyRouter.TryRouteFunds(client, data))
                return;

            //send the funds update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteFundsDataToFile(data.Funds);
        }
    }
}
