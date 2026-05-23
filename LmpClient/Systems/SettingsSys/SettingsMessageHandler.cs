using CommNet;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Settings;
using LmpCommon.Message.Interface;
using System.Collections.Concurrent;

namespace LmpClient.Systems.SettingsSys
{
    public class SettingsMessageHandler : SubSystem<SettingsSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is SettingsReplyMsgData msgData)) return;

            // [diag:settings-sync-race] Entry log paired with the SettingsSynced-state-
            // transition log at the bottom of this method. v15 cohort hit second-connect
            // NRE in StartGameNow because ServerParameters was null when game-start ran;
            // this log lets the next repro confirm whether HandleMessage actually ran for
            // this connection and (if so) at what NetworkState. Compare entry timestamp
            // against the StartGameNow log to spot a state-machine race that lets the
            // outer chain advance past SyncingSettings without HandleMessage completing.
            // Trimmed to race-relevant fields only (consumer-lens review SHOULD-FIX) —
            // the PerAgency* tail-fields don't help diagnose this race.
            LunaLog.Log(
                "[LMP]: [diag:settings-sync-race] SettingsMessageHandler.HandleMessage ENTRY. " +
                $"NetworkState={MainSystem.NetworkState}, " +
                $"GameMode={msgData.GameMode}, GameDifficulty={msgData.GameDifficulty}");

            SettingsSystem.ServerSettings.WarpMode = msgData.WarpMode;
            SettingsSystem.ServerSettings.GameMode = msgData.GameMode;
            SettingsSystem.ServerSettings.TerrainQuality = msgData.TerrainQuality;
            SettingsSystem.ServerSettings.AllowCheats = msgData.AllowCheats;
            SettingsSystem.ServerSettings.AllowAdmin = msgData.AllowAdmin;
            SettingsSystem.ServerSettings.AllowSackKerbals = msgData.AllowSackKerbals;
            SettingsSystem.ServerSettings.MaxNumberOfAsteroids = msgData.MaxNumberOfAsteroids;
            SettingsSystem.ServerSettings.MaxNumberOfComets = msgData.MaxNumberOfComets;
            SettingsSystem.ServerSettings.ConsoleIdentifier = msgData.ConsoleIdentifier;
            SettingsSystem.ServerSettings.SafetyBubbleDistance = msgData.SafetyBubbleDistance;
            SettingsSystem.ServerSettings.MaxVesselParts = msgData.MaxVesselParts;
            SettingsSystem.ServerSettings.VesselUpdatesMsInterval = msgData.VesselUpdatesMsInterval;
            SettingsSystem.ServerSettings.SecondaryVesselUpdatesMsInterval = msgData.SecondaryVesselUpdatesMsInterval;
            SettingsSystem.ServerSettings.GameDifficulty = msgData.GameDifficulty;
            SettingsSystem.ServerSettings.MinScreenshotIntervalMs = msgData.MinScreenshotIntervalMs;
            SettingsSystem.ServerSettings.MaxScreenshotWidth = msgData.MaxScreenshotWidth;
            SettingsSystem.ServerSettings.MaxScreenshotHeight = msgData.MaxScreenshotHeight;
            SettingsSystem.ServerSettings.MinCraftLibraryRequestIntervalMs = msgData.MinScreenshotIntervalMs;
            SettingsSystem.ServerSettings.PrintMotdInChat = msgData.PrintMotdInChat;
            SettingsSystem.ServerSettings.PerAgencyCareerEnabled = msgData.PerAgencyCareerEnabled;
            SettingsSystem.ServerSettings.PerAgencyKerbalRosterEnabled = msgData.PerAgencyKerbalRosterEnabled;

            SettingsSystem.ServerSettings.ServerParameters =
                GameParameters.GetDefaultParameters(
                    MainSystem.Singleton.ConvertGameMode(SettingsSystem.ServerSettings.GameMode),
                    (GameParameters.Preset)SettingsSystem.ServerSettings.GameDifficulty);

            if (SettingsSystem.ServerSettings.GameDifficulty == GameDifficulty.Custom)
            {
                SettingsSystem.ServerSettings.ServerParameters = new GameParameters
                {
                    preset = GameParameters.Preset.Custom,
                    Difficulty =
                    {
                        AllowOtherLaunchSites = msgData.AllowOtherLaunchSites,
                        AllowStockVessels = msgData.AllowStockVessels,
                        AutoHireCrews = msgData.AutoHireCrews,
                        BypassEntryPurchaseAfterResearch = msgData.BypassEntryPurchaseAfterResearch,
                        IndestructibleFacilities = msgData.IndestructibleFacilities,
                        MissingCrewsRespawn = msgData.MissingCrewsRespawn,
                        ReentryHeatScale = msgData.ReentryHeatScale,
                        ResourceAbundance = msgData.ResourceAbundance,
                        RespawnTimer = msgData.RespawnTimer,
                        EnableCommNet = msgData.EnableCommNet
                    },
                    Career =
                    {
                        FundsGainMultiplier = msgData.FundsGainMultiplier,
                        FundsLossMultiplier = msgData.FundsLossMultiplier,
                        RepGainMultiplier = msgData.RepGainMultiplier,
                        RepLossMultiplier = msgData.RepLossMultiplier,
                        RepLossDeclined = msgData.RepLossDeclined,
                        ScienceGainMultiplier = msgData.ScienceGainMultiplier,
                        StartingFunds = msgData.StartingFunds,
                        StartingReputation = msgData.StartingReputation,
                        StartingScience = msgData.StartingScience
                    },
                    Flight =
                    {
                        CanRestart = msgData.CanRevert,
                        CanLeaveToEditor = msgData.CanRevert
                    }
                };

                SettingsSystem.ServerSettings.ServerAdvancedParameters = new GameParameters.AdvancedParams
                {
                    ActionGroupsAlways = msgData.ActionGroupsAlways,
                    GKerbalLimits = msgData.GKerbalLimits,
                    GPartLimits = msgData.GPartLimits,
                    KerbalGToleranceMult = msgData.KerbalGToleranceMult,
                    PressurePartLimits = msgData.PressurePartLimits,
                    AllowNegativeCurrency = msgData.AllowNegativeCurrency,
                    EnableKerbalExperience = msgData.EnableKerbalExperience,
                    ImmediateLevelUp = msgData.ImmediateLevelUp,
                    ResourceTransferObeyCrossfeed = msgData.ResourceTransferObeyCrossfeed,
                    BuildingImpactDamageMult = msgData.BuildingImpactDamageMult,
                    PartUpgradesInCareer = msgData.PartUpgradesInCareerAndSandbox,
                    PartUpgradesInSandbox = msgData.PartUpgradesInCareerAndSandbox,
                    EnableFullSASInSandbox = msgData.EnableFullSASInSandbox,
                };

                SettingsSystem.ServerSettings.ServerCommNetParameters = new CommNetParams
                {
                    requireSignalForControl = msgData.RequireSignalForControl,
                    DSNModifier = msgData.DsnModifier,
                    rangeModifier = msgData.RangeModifier,
                    occlusionMultiplierVac = msgData.OcclusionMultiplierVac,
                    occlusionMultiplierAtm = msgData.OcclusionMultiplierAtm,
                    enableGroundStations = msgData.EnableGroundStations,
                    plasmaBlackout = msgData.PlasmaBlackout
                };
            }

            //Never allow quickload, it's useless in a multiplayer game
            SettingsSystem.ServerSettings.ServerParameters.Flight.CanQuickLoad = false;

            // [diag:settings-sync-race] Exit log — confirms ServerParameters was populated
            // AND we successfully transitioned NetworkState to SettingsSynced. If the next
            // repro's KSP.log shows an ENTRY without an EXIT before the StartGameNow abort,
            // an exception swallowed mid-handler is the upstream race; if it shows neither,
            // the message was never delivered (NetworkReceiver / Lidgren / queue lifecycle
            // issue); if it shows both but StartGameNow still aborts, ServerParameters is
            // being nulled BETWEEN exit and StartGameNow (another OnDisabled fired mid-
            // connect — investigate NetworkEvent.onNetworkStatusChanged firing).
            LunaLog.Log(
                "[LMP]: [diag:settings-sync-race] SettingsMessageHandler.HandleMessage EXIT — " +
                $"ServerParameters populated. Transitioning to SettingsSynced.");

            MainSystem.NetworkState = ClientState.SettingsSynced;
        }
    }
}
