using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Command.Command.Base;
using Server.Command.Common;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using Server.System.Scenario;

namespace Server.Command.Command
{
    public class SetScienceCommand : SimpleCommand
    {
        //Executes the SetScienceCommand
        public override bool Execute(string commandArgs)
        {
            //[Stage 5.17c, gate refined 5.17e-1 round-1 upgrade-lens review] Refuse the
            //command whenever the operator has set PerAgencyCareer=true — including the
            //misconfigured (PerAgencyCareer=true + non-Career mode) state. Under per-agency
            //active (mode==Career), the projector overwrites the shared ResearchAndDevelopment
            //blob so setscience would silently fail. Under the misconfigured state, the
            //per-agency routers (5.17e-3 onwards) are disabled and setscience WOULD work —
            //but it would leak to all peers via the shared Science broadcast, surprising an
            //operator who thought they were in per-agency mode. Refuse loudly in both cases;
            //the operator fixes the misconfig deliberately. Stage 5.18d ships /setagency science.
            if (GameplaySettings.SettingsStore.PerAgencyCareer)
            {
                LunaLog.Error("setscience is disabled while PerAgencyCareer=true. In Career mode use /setagency science <agency-id-or-owner> <amount> (Stage 5.18d slice f — run /listagencies to see ids/owners); in Science/Sandbox the per-agency setting is misconfigured (see boot warning). Operators can also edit Universe/Agencies/{guid}.txt directly while the server is stopped.");
                return false;
            }
            //Check parameter
            CommandSystemHelperMethods.SplitCommandParamArray(commandArgs, out var parameters);
            if (!CheckParameter(parameters)) return false;
            var science = parameters[0];
            var isFloat = float.TryParse(science, out var fScience);
            if (isFloat)
            {
                //Set science
                SetScience(fScience);
                return true;
            }
            else
            {
                LunaLog.Error($"Syntax error. Use valid number as parameter!");
                return false;
            }
        }

        //Sets given science value
        private static void SetScience(float science)
        {
            //Science update to server
            ScenarioDataUpdater.WriteScienceDataToFile(science);
            //Science update to all other clients
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressScienceMsgData>();
            data.Science = science;
            data.Reason = "Server Command";
            MessageQueuer.SendToAllClients<ShareProgressSrvMsg>(data);
            LunaLog.Debug($"Science received: {data.Science} Reason: {data.Reason}");
            ScenarioDataUpdater.WriteScienceDataToFile(data.Science);
            //var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<ChatMsgData>();
            //msgData.From = GeneralSettings.SettingsStore.ConsoleIdentifier;
            //msgData.Text = "Science was changed to " + science.ToString();
            //MessageQueuer.SendToAllClients<ChatSrvMsg>(msgData);
        }

        //Checks the given parameter
        private static bool CheckParameter(string[] parameters)
        {
            if (parameters == null || parameters.Length != 1)
            {
                LunaLog.Error($"Syntax error. Use valid number as parameter!");
                return false;
            }
            return true;
        }
    }
}
