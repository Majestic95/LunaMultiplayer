using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Agency;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareScienceSubjectSystem
    {
        public static void ScienceSubjectReceived(ClientStructure client, ShareProgressScienceSubjectMsgData data)
        {
            LunaLog.Debug($"Science experiment received: {data.ScienceSubject.Id}");

            // [Stage 5.17e-5] Per-agency routing — persist to AgencyState.ScienceSubjects
            // and skip the shared scenario / peer relay. Projector splices the
            // per-agency subjects into outgoing R&D scenarios on next scene-load.
            if (AgencyResearchRouter.TryRouteScienceSubject(client, data))
                return;

            //send the science subject update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteScienceSubjectDataToFile(data.ScienceSubject);
        }
    }
}
