using LmpCommon.Enums;
using LmpCommon.Message.Data.Scenario;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Properties;
using Server.Server;
using Server.Settings.Structures;
using Server.System.Agency;
using Server.System.Scenario;
using System.IO;
using System.Linq;
using System.Text;

namespace Server.System
{
    public class ScenarioSystem
    {
        public const string ScenarioFileFormat = ".txt";
        public static string ScenariosPath = Path.Combine(ServerContext.UniverseDirectory, "Scenarios");

        public static bool GenerateDefaultScenarios()
        {
            var scenarioFilesCreated =
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ROCScenario.txt"), Resources.ROCScenario) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "DeployedScience.txt"), Resources.DeployedScience) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "CommNetScenario.txt"), Resources.CommNetScenario) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "PartUpgradeManager.txt"), Resources.PartUpgradeManager) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ProgressTracking.txt"), Resources.ProgressTracking) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ResourceScenario.txt"), Resources.ResourceScenario) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioAchievements.txt"), Resources.ScenarioAchievements) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioDestructibles.txt"), Resources.ScenarioDestructibles) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "SentinelScenario.txt"), Resources.SentinelScenario) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "VesselRecovery.txt"), Resources.VesselRecovery) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioNewGameIntro.txt"), Resources.ScenarioNewGameIntro);

            if (GeneralSettings.SettingsStore.GameMode != GameMode.Sandbox)
            {
                scenarioFilesCreated &= FileHandler.CreateFile(Path.Combine(ScenariosPath, "ResearchAndDevelopment.txt"), Resources.ResearchAndDevelopment);
            }
            else
            {
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "ResearchAndDevelopment.txt"));
            }

            if (GeneralSettings.SettingsStore.GameMode == GameMode.Career)
            {
                scenarioFilesCreated &=
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "ContractSystem.txt"), Resources.ContractSystem) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "Funding.txt"), Resources.Funding) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "Reputation.txt"), Resources.Reputation) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioContractEvents.txt"), Resources.ScenarioContractEvents) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioUpgradeableFacilities.txt"), Resources.ScenarioUpgradeableFacilities) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "StrategySystem.txt"), Resources.StrategySystem);
            }
            else
            {
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "ContractSystem.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "Funding.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "Reputation.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "ScenarioContractEvents.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "ScenarioUpgradeableFacilities.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "StrategySystem.txt"));
            }

            return scenarioFilesCreated;
        }

        public static void SendScenarioModules(ClientStructure client)
        {
            //[Stage 5.17c] Per-player scenario projection: when PerAgencyCareer=true, the
            //requesting client receives ScenarioModule blobs where Funding/Sci/Rep have
            //been overwritten with THEIR agency's values (spec §10 Q1 privacy +
            //§5 read-path projection). Under gate=off (or Sandbox) AgencyScenarioProjector
            //returns the serialized text unchanged — dual-mode silence preserved.
            //Projection is read-only — the canonical CurrentScenarios store is never
            //mutated by this path, so concurrent Share* writers and other clients'
            //SendScenarioModules calls cannot race a projected snapshot.
            //
            //ConcurrentDictionary.Keys is a snapshot view; GetScenarioInConfigNodeFormat
            //returns null if a concurrent remove slipped between the Keys snapshot and
            //the lookup. No production code path removes from CurrentScenarios today
            //(round-1 server-systems review verified), but the .Where filter below is
            //defensive — without it, a future remover would NRE the receive thread on
            //Encoding.UTF8.GetBytes(null).
            var scenarioDataArray = ScenarioStoreSystem.CurrentScenarios.Keys
                .Select(s => new
                {
                    Module = Path.GetFileNameWithoutExtension(s),
                    Text = ScenarioStoreSystem.GetScenarioInConfigNodeFormat(s),
                })
                .Where(x => x.Text != null)
                .Select(x =>
                {
                    var projected = AgencyScenarioProjector.ProjectForClient(x.Module, x.Text, client);
                    var serializedData = Encoding.UTF8.GetBytes(projected);
                    return new ScenarioInfo
                    {
                        Data = serializedData,
                        NumBytes = serializedData.Length,
                        Module = x.Module,
                    };
                }).ToArray();

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<ScenarioDataMsgData>();
            msgData.ScenariosData = scenarioDataArray;
            msgData.ScenarioCount = scenarioDataArray.Length;

            MessageQueuer.SendToClient<ScenarioSrvMsg>(client, msgData);
        }


        public static void ParseReceivedScenarioData(ClientStructure client, ScenarioBaseMsgData messageData)
        {
            var data = (ScenarioDataMsgData)messageData;
            LunaLog.Debug($"Saving {data.ScenarioCount} scenario modules from {client.PlayerName}");
            for (var i = 0; i < data.ScenarioCount; i++)
            {
                var scenarioAsConfigNode = Encoding.UTF8.GetString(data.ScenariosData[i].Data, 0, data.ScenariosData[i].NumBytes);
                ScenarioDataUpdater.RawConfigNodeInsertOrUpdate(data.ScenariosData[i].Module, scenarioAsConfigNode);
            }
        }
    }
}
