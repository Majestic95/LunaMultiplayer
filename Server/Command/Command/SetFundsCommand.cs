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
    public class SetFundsCommand : SimpleCommand
    {
        //Executes the SetFundsCommand
        public override bool Execute(string commandArgs)
        {
            //[Stage 5.17c, gate refined 5.17e-1 round-1 upgrade-lens review] Refuse the
            //command whenever the operator has set PerAgencyCareer=true — including the
            //misconfigured (PerAgencyCareer=true + non-Career mode) state. Under per-agency
            //active (mode==Career), the projector overwrites the shared Funding blob so
            //setfunds would silently fail. Under the misconfigured state, the per-agency
            //routers (5.17e-3 onwards) are disabled and setfunds WOULD work — but it would
            //leak to all peers via the shared Funds broadcast, surprising an operator who
            //thought they were in per-agency mode. Refuse loudly in both cases; the operator
            //fixes the misconfig deliberately before issuing the command. Stage 5.18d ships
            //the per-agency replacement (setagencyfunds <agencyId> <amount>).
            if (GameplaySettings.SettingsStore.PerAgencyCareer)
            {
                LunaLog.Error("setfunds is disabled while PerAgencyCareer=true. In Career mode use setagencyfunds <agencyId> <amount> (Stage 5.18d); in Science/Sandbox the per-agency setting is misconfigured (see boot warning). Operators can also edit Universe/Agencies/{guid}.txt directly while the server is stopped.");
                return false;
            }
            //Check parameter
            CommandSystemHelperMethods.SplitCommandParamArray(commandArgs, out var parameters);
            if (!CheckParameter(parameters)) return false;
            var funds = parameters[0];
            var isDouble = double.TryParse(funds, out var dFunds);
            if (isDouble)
            {
                //Set funds
                SetFunds(dFunds);
                return true;
            }
            else
            {
                LunaLog.Error($"Syntax error. Use valid number as parameter!");
                return false;
            }
        }

        //Sets given funds value
        private static void SetFunds(double funds)
        {
            //Fund update to server
            ScenarioDataUpdater.WriteFundsDataToFile(funds);
            //Fund update to all other clients
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressFundsMsgData>();
            data.Funds = funds;
            data.Reason = "Server Command";
            MessageQueuer.SendToAllClients<ShareProgressSrvMsg>(data);
            LunaLog.Debug($"Funds received: {data.Funds} Reason: {data.Reason}");
            ScenarioDataUpdater.WriteFundsDataToFile(data.Funds);
            //var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<ChatMsgData>();
            //msgData.From = GeneralSettings.SettingsStore.ConsoleIdentifier;
            //msgData.Text = "Funds were changed to " + funds.ToString();
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
