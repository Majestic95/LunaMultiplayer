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
            //[Stage 5.17c] Under PerAgencyCareer=true the projector overwrites the shared
            //ResearchAndDevelopment scenario blob with each agency's tracked value before
            //send. setscience would write to the shared blob, but every client sees their
            //own agency value via the projection — the admin's intended update is silently
            //invisible. Refuse with an explicit error. Stage 5.18d ships setagencyscience.
            if (GameplaySettings.SettingsStore.PerAgencyCareer)
            {
                LunaLog.Error("setscience is disabled under PerAgencyCareer=true. Use setagencyscience <agencyId> <amount> (Stage 5.18d) or edit Universe/Agencies/{guid}.txt directly while the server is stopped.");
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
