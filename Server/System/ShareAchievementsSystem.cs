using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Agency;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareAchievementsSystem
    {
        public static void AchievementsReceived(ClientStructure client, ShareProgressAchievementsMsgData data)
        {
            LunaLog.Debug($"Achievements data received: {data.Id}");

            // [Stage 5.17e-6] Per-agency routing — world firsts belong to the
            // agency that achieved them. Spec §2 acknowledges optional global
            // "<agency> achieved X" chat announcements are a v2+ extension,
            // not in scope for v1.
            if (AgencyProgressRouter.TryRouteAchievement(client, data))
                return;

            //send the achievements update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteAchievementDataToFile(data);
        }
    }
}
