using LmpCommon.Enums;
using LmpCommon.Message.Data.Settings;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Message.Base;
using Server.Server;
using Server.Settings.Structures;
using Server.System.Agency;

namespace Server.Message
{
    public class SettingsMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            //We don't use this message anymore so we can recycle it
            message.Recycle();

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<SettingsReplyMsgData>();
            msgData.WarpMode = WarpSettings.SettingsStore.WarpMode;
            msgData.GameMode = GeneralSettings.SettingsStore.GameMode;
            msgData.TerrainQuality = GeneralSettings.SettingsStore.TerrainQuality;
            msgData.AllowCheats = GeneralSettings.SettingsStore.Cheats;
            msgData.AllowAdmin = !string.IsNullOrEmpty(GeneralSettings.SettingsStore.AdminPassword);
            msgData.AllowSackKerbals = GeneralSettings.SettingsStore.AllowSackKerbals;
            msgData.MaxNumberOfAsteroids = GeneralSettings.SettingsStore.NumberOfAsteroids;
            msgData.MaxNumberOfComets = GeneralSettings.SettingsStore.NumberOfComets;
            msgData.ConsoleIdentifier = GeneralSettings.SettingsStore.ConsoleIdentifier;
            msgData.GameDifficulty = GeneralSettings.SettingsStore.GameDifficulty;
            msgData.SafetyBubbleDistance = GeneralSettings.SettingsStore.SafetyBubbleDistance;
            msgData.MaxVesselParts = GeneralSettings.SettingsStore.MaxVesselParts;
            msgData.VesselUpdatesMsInterval = IntervalSettings.SettingsStore.VesselUpdatesMsInterval;
            msgData.SecondaryVesselUpdatesMsInterval = IntervalSettings.SettingsStore.SecondaryVesselUpdatesMsInterval;
            msgData.MinScreenshotIntervalMs = ScreenshotSettings.SettingsStore.MinScreenshotIntervalMs;
            msgData.MaxScreenshotWidth = ScreenshotSettings.SettingsStore.MaxScreenshotWidth;
            msgData.MaxScreenshotHeight = ScreenshotSettings.SettingsStore.MaxScreenshotHeight;
            msgData.MinCraftLibraryRequestIntervalMs = CraftSettings.SettingsStore.MinCraftLibraryRequestIntervalMs;
            msgData.PrintMotdInChat = GeneralSettings.SettingsStore.PrintMotdInChat;

            // [Stage 5.17e-2] Per-agency career active state. Use the combined check
            // (PerAgencyCareer=true AND GameMode=Career) so the client sees the
            // functionally-active state, not the raw operator setting. A 0.31.x client
            // mirror (Stage 5.18a) gates its CreateRequest send + agency UI on this
            // field. Set unconditionally outside the GameDifficulty=Custom block
            // because the gate is independent of difficulty preset.
            msgData.PerAgencyCareerEnabled = AgencySystem.PerAgencyEnabled;

            // [Stage 6 Phase 6.6] Per-agency kerbal-roster active state. Uses the
            // combined Phase 6.5 predicate (PerAgencyEnabled AND PerAgencyKerbalRoster)
            // so the client distinguishes "shared-roster transient scrub" from
            // "per-agency partition scrub" — Phase 6.6's tracking-station "Crew: N
            // (agency)" label gates on this field to avoid seeding transient
            // mislabels under PerAgencyCareer=on / PerAgencyKerbalRoster=off
            // configurations (typical Stage 5 → Stage 6 ramp window).
            msgData.PerAgencyKerbalRosterEnabled = AgencySystem.PerAgencyKerbalRosterEnabled;

            if (GeneralSettings.SettingsStore.GameDifficulty == GameDifficulty.Custom && GameplaySettings.SettingsStore != null)
            {
                msgData.EnableCommNet = GameplaySettings.SettingsStore.CommNetwork;
                msgData.RespawnTimer = GameplaySettings.SettingsStore.RespawnTime;
                msgData.AllowOtherLaunchSites = GameplaySettings.SettingsStore.AllowOtherLaunchSites;
                msgData.AllowStockVessels = GameplaySettings.SettingsStore.AllowStockVessels;
                msgData.AutoHireCrews = GameplaySettings.SettingsStore.AutoHireCrews;
                msgData.BypassEntryPurchaseAfterResearch = GameplaySettings.SettingsStore.BypassEntryPurchaseAfterResearch;
                msgData.IndestructibleFacilities = GameplaySettings.SettingsStore.IndestructibleFacilities;
                msgData.MissingCrewsRespawn = GameplaySettings.SettingsStore.MissingCrewsRespawn;
                msgData.ReentryHeatScale = GameplaySettings.SettingsStore.ReentryHeatScale;
                msgData.ResourceAbundance = GameplaySettings.SettingsStore.ResourceAbundance;
                msgData.FundsGainMultiplier = GameplaySettings.SettingsStore.FundsGainMultiplier;
                msgData.CanRevert = GameplaySettings.SettingsStore.CanRevert;
                msgData.RepLossDeclined = GameplaySettings.SettingsStore.RepLossDeclined;
                msgData.FundsLossMultiplier = GameplaySettings.SettingsStore.FundsLossMultiplier;
                msgData.RepGainMultiplier = GameplaySettings.SettingsStore.RepGainMultiplier;
                msgData.RepLossMultiplier = GameplaySettings.SettingsStore.RepLossMultiplier;
                msgData.ScienceGainMultiplier = GameplaySettings.SettingsStore.ScienceGainMultiplier;
                msgData.StartingFunds = GameplaySettings.SettingsStore.StartingFunds;
                msgData.StartingReputation = GameplaySettings.SettingsStore.StartingReputation;
                msgData.StartingScience = GameplaySettings.SettingsStore.StartingScience;
                //Advanced
                msgData.ActionGroupsAlways = GameplaySettings.SettingsStore.ActionGroupsAlways;
                msgData.GKerbalLimits = GameplaySettings.SettingsStore.GKerbalLimits;
                msgData.GPartLimits = GameplaySettings.SettingsStore.GPartLimits;
                msgData.KerbalGToleranceMult = GameplaySettings.SettingsStore.KerbalGToleranceMult;
                msgData.PressurePartLimits = GameplaySettings.SettingsStore.PressurePartLimits;
                msgData.EnableKerbalExperience = GameplaySettings.SettingsStore.KerbalExp;
                msgData.ImmediateLevelUp = GameplaySettings.SettingsStore.ImmediateLevelUp;
                msgData.AllowNegativeCurrency = GameplaySettings.SettingsStore.AllowNegativeCurrency;
                msgData.ResourceTransferObeyCrossfeed = GameplaySettings.SettingsStore.ObeyCrossfeedRules;
                msgData.BuildingImpactDamageMult = GameplaySettings.SettingsStore.BuildingDamageMultiplier;
                msgData.PartUpgradesInCareerAndSandbox = GameplaySettings.SettingsStore.PartUpgrades;
                msgData.EnableFullSASInSandbox = GameplaySettings.SettingsStore.EnableFullSASInSandbox;
                //Commnet
                msgData.RequireSignalForControl = GameplaySettings.SettingsStore.RequireSignalForControl;
                msgData.DsnModifier = GameplaySettings.SettingsStore.DsnModifier;
                msgData.RangeModifier = GameplaySettings.SettingsStore.RangeModifier;
                msgData.OcclusionMultiplierVac = GameplaySettings.SettingsStore.OcclusionModifierVac;
                msgData.OcclusionMultiplierAtm = GameplaySettings.SettingsStore.OcclusionModifierAtm;
                msgData.EnableGroundStations = GameplaySettings.SettingsStore.ExtraGroundstations;
                msgData.PlasmaBlackout = GameplaySettings.SettingsStore.ReentryBlackout;
            }

            MessageQueuer.SendToClient<SetingsSrvMsg>(client, msgData);
        }
    }
}
