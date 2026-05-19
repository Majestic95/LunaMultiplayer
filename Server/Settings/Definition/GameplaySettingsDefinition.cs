using LmpCommon.Xml;
using System;
using System.Xml;
using System.Xml.Serialization;

namespace Server.Settings.Definition
{
    [Serializable]
    public class GameplaySettingsDefinition
    {
        // [fix:BUG-039] XmlSerializer drops unknown child elements by default; the server's
        // Load()-then-Save() cycle (SettingsBase.Load at SettingsBase.cs:30-31, "We call the
        // save to add the new settings into the file") then rewrote the file without them,
        // so operator-added custom keys vanished on first server start. The [XmlAnyElement]
        // collection captures any unrecognised child elements at load time and XmlSerializer
        // emits them back out on save. Operators can now add custom <SomeKey>value</SomeKey>
        // elements to gameplaysettings.xml and have them round-trip cleanly (preserved across
        // restarts; useful for operator metadata, mod-tool coordination, and forward-compat
        // when LMP adds a setting before the operator updates their files).
        //
        // PUBLIC FIELD (not a property) is deliberate: System.Reflection.GetProperties() does
        // not return public fields, so SettingsHandler.HasDifferencesAgainstGivenSetting's
        // preset-comparison reflection (lines 65-71) does not see this member — no
        // null-deref hazard, no false flip-to-Custom on operators with custom elements.
        //
        // Custom elements move to the END of the parent element on round-trip (standard
        // XmlSerializer ordering for [XmlAnyElement]). Original sibling order with known
        // settings is not preserved. Acceptable trade-off vs. dropping the data entirely.
        //
        // Scoped to GameplaySettingsDefinition only — the bug is specifically filed against
        // gameplaysettings.xml (#587). If operators ask for the same on the other 11 settings
        // files, replicate the field shape there.
        [XmlAnyElement]
        public XmlElement[] CustomElements;


        //General options

        [XmlComment(Value = "Allow Reverting")]
        public bool CanRevert { get; set; }

        [XmlComment(Value = "Missing Crews Respawn")]
        public bool MissingCrewsRespawn { get; set; }

        [XmlComment(Value = "Crew Respawn Time")]
        public float RespawnTime { get; set; }

        [XmlComment(Value = "Auto-Hire Crewmembers before Flight")]
        public bool AutoHireCrews { get; set; }

        [XmlComment(Value = "No Entry Purchase Required on Research")]
        public bool BypassEntryPurchaseAfterResearch { get; set; }

        [XmlComment(Value = "Indestructible Facilities")]
        public bool IndestructibleFacilities { get; set; }

        [XmlComment(Value = "Allow Stock Vessels")]
        public bool AllowStockVessels { get; set; }

        [XmlComment(Value = "Allow Other launch sites (for users that have the Making History DLC)")]
        public bool AllowOtherLaunchSites { get; set; }

        [XmlComment(Value = "Enable per-agency career mode (each player has independent funds/science/reputation/tech/contracts). Default false preserves shared-agency behaviour. Cannot be changed mid-save: pick a value before the universe is first populated.")]
        public bool PerAgencyCareer { get; set; }

        [XmlComment(Value = "[Stage 5.17e-9] Allow PerAgencyCareer=true on a universe that already has accumulated shared-agency state (vessels + career scalars/contracts/tech/etc.). Default false: server refuses to start with a loud message pointing operator to the fresh-start workflow (spec §10). Set true ONLY if you accept that the accumulated shared progress will be invisible to per-agency clients (projector strips it on send).")]
        public bool AllowEnablePerAgencyOnExistingUniverse { get; set; }

        //Game systems

        [XmlComment(Value = "Re-Entry Heating")]
        public float ReentryHeatScale { get; set; }

        [XmlComment(Value = "Resource Abundance")]
        public float ResourceAbundance { get; set; }

        [XmlComment(Value = "Enable Comm Network")]
        public bool CommNetwork { get; set; }

        // Career Settings

        [XmlComment(Value = "Starting Funds")]
        public float StartingFunds { get; set; }

        [XmlComment(Value = "Starting Science")]
        public float StartingScience { get; set; }

        [XmlComment(Value = "Starting Reputation")]
        public float StartingReputation { get; set; }

        [XmlComment(Value = "Science Rewards")]
        public float ScienceGainMultiplier { get; set; }

        [XmlComment(Value = "Funds Rewards")]
        public float FundsGainMultiplier { get; set; }

        [XmlComment(Value = "Reputation Rewards")]
        public float RepGainMultiplier { get; set; }

        [XmlComment(Value = "Funds Penalties")]
        public float FundsLossMultiplier { get; set; }

        [XmlComment(Value = "Reputation Penalties")]
        public float RepLossMultiplier { get; set; }

        [XmlComment(Value = "Decline Penalty")]
        public float RepLossDeclined { get; set; }

        // Advanced Options

        [XmlComment(Value = "Enable Kerbal Experience")]
        public bool KerbalExp { get; set; }

        [XmlComment(Value = "Kerbals Level Up Immediately")]
        public bool ImmediateLevelUp { get; set; }

        [XmlComment(Value = "Allow Negative Funds/Science")]
        public bool AllowNegativeCurrency { get; set; }

        [XmlComment(Value = "Enable parts pressure limits")]
        public bool PressurePartLimits { get; set; }

        [XmlComment(Value = "Enable parts G limits")]
        public bool GPartLimits { get; set; }

        [XmlComment(Value = "Enable Kerbal G limits")]
        public bool GKerbalLimits { get; set; }

        [XmlComment(Value = "Kerbal G tolerance multiplier")]
        public float KerbalGToleranceMult { get; set; }

        [XmlComment(Value = "Resource Transfer Obeys Crossfeed Rules")]
        public bool ObeyCrossfeedRules { get; set; }

        [XmlComment(Value = "Always allow action groups")]
        public bool ActionGroupsAlways { get; set; }

        [XmlComment(Value = "Building Impact Damage Multiplier")]
        public float BuildingDamageMultiplier { get; set; }

        [XmlComment(Value = "Part Upgrades")]
        public bool PartUpgrades { get; set; }

        // CommNet Options

        [XmlComment(Value = "Require Signal for Control")]
        public bool RequireSignalForControl { get; set; }

        [XmlComment(Value = "Plasma blackout")]
        public bool ReentryBlackout { get; set; }

        [XmlComment(Value = "Range Modifier")]
        public float RangeModifier { get; set; }

        [XmlComment(Value = "DSN Modifier")]
        public float DsnModifier { get; set; }

        [XmlComment(Value = "Occlusion Modifier, Vac")]
        public float OcclusionModifierVac { get; set; }

        [XmlComment(Value = "Occlusion Modifier, Atm")]
        public float OcclusionModifierAtm { get; set; }

        [XmlComment(Value = "Enable Extra Groundstations")]
        public bool ExtraGroundstations { get; set; }

        [XmlComment(Value = "Enable full SAS in Sandbox")]
        public bool EnableFullSASInSandbox { get; set; }

        public void SetEasy()
        {
            //General options
            CanRevert = true;
            MissingCrewsRespawn = true;
            RespawnTime = 2f;
            AutoHireCrews = false;
            BypassEntryPurchaseAfterResearch = true;
            IndestructibleFacilities = true;
            AllowStockVessels = true;
            AllowOtherLaunchSites = true;
            PerAgencyCareer = false;
            AllowEnablePerAgencyOnExistingUniverse = false;

            //Game systems
            ReentryHeatScale = 0.5f;
            ResourceAbundance = 1.2f;
            CommNetwork = false;

            // Career Settings
            StartingFunds = 250000.0f;
            StartingScience = 0.0f;
            StartingReputation = 0.0f;
            ScienceGainMultiplier = 2.0f;
            FundsGainMultiplier = 2.0f;
            RepGainMultiplier = 2.0f;
            FundsLossMultiplier = 0.5f;
            RepLossMultiplier = 0.5f;
            RepLossDeclined = 0.0f;

            // Advanced Options
            KerbalExp = true;
            ImmediateLevelUp = true;
            AllowNegativeCurrency = false;
            PressurePartLimits = false;
            GPartLimits = false;
            GKerbalLimits = false;
            KerbalGToleranceMult = 1.0f;
            ObeyCrossfeedRules = false;
            ActionGroupsAlways = false;
            BuildingDamageMultiplier = 0.03f;
            PartUpgrades = true;
            EnableFullSASInSandbox = false;

            // CommNet Options
            RequireSignalForControl = false;
            ReentryBlackout = false;
            RangeModifier = 1.5f;
            DsnModifier = 1.0f;
            OcclusionModifierVac = 0.0f;
            OcclusionModifierAtm = 0.0f;
            ExtraGroundstations = true;
        }

        public void SetNormal()
        {
            CanRevert = true;
            MissingCrewsRespawn = true;
            RespawnTime = 2f;
            AutoHireCrews = false;
            BypassEntryPurchaseAfterResearch = true;
            IndestructibleFacilities = false;
            AllowStockVessels = false;
            AllowOtherLaunchSites = true;
            PerAgencyCareer = false;
            AllowEnablePerAgencyOnExistingUniverse = false;

            //Game systems
            ReentryHeatScale = 1.0f;
            ResourceAbundance = 1.0f;
            CommNetwork = true;

            // Career Settings
            StartingFunds = 25000.0f;
            StartingScience = 0.0f;
            StartingReputation = 0.0f;
            ScienceGainMultiplier = 1.0f;
            FundsGainMultiplier = 1.0f;
            RepGainMultiplier = 1.0f;
            FundsLossMultiplier = 1.0f;
            RepLossMultiplier = 1.0f;
            RepLossDeclined = 1.0f;

            // Advanced Options
            KerbalExp = true;
            ImmediateLevelUp = false;
            AllowNegativeCurrency = false;
            PressurePartLimits = false;
            GPartLimits = false;
            GKerbalLimits = false;
            KerbalGToleranceMult = 1.0f;
            ObeyCrossfeedRules = false;
            ActionGroupsAlways = false;
            BuildingDamageMultiplier = 0.05f;
            PartUpgrades = true;
            EnableFullSASInSandbox = false;

            // CommNet Options
            RequireSignalForControl = false;
            ReentryBlackout = false;
            RangeModifier = 1.0f;
            DsnModifier = 1.0f;
            OcclusionModifierVac = 0.9f;
            OcclusionModifierAtm = 0.75f;
            ExtraGroundstations = true;
        }

        public void SetModerate()
        {
            CanRevert = true;
            MissingCrewsRespawn = false;
            RespawnTime = 2f;
            AutoHireCrews = false;
            BypassEntryPurchaseAfterResearch = false;
            IndestructibleFacilities = false;
            AllowStockVessels = false;
            AllowOtherLaunchSites = true;
            PerAgencyCareer = false;
            AllowEnablePerAgencyOnExistingUniverse = false;

            //Game systems
            ReentryHeatScale = 1.0f;
            ResourceAbundance = 0.8f;
            CommNetwork = true;

            // Career Settings
            StartingFunds = 15000.0f;
            StartingScience = 0.0f;
            StartingReputation = 0.0f;
            ScienceGainMultiplier = 0.9f;
            FundsGainMultiplier = 0.9f;
            RepGainMultiplier = 0.9f;
            FundsLossMultiplier = 1.5f;
            RepLossMultiplier = 1.5f;
            RepLossDeclined = 2.0f;

            // Advanced Options
            KerbalExp = true;
            ImmediateLevelUp = false;
            AllowNegativeCurrency = true;
            PressurePartLimits = false;
            GPartLimits = false;
            GKerbalLimits = false;
            KerbalGToleranceMult = 1.0f;
            ObeyCrossfeedRules = true;
            ActionGroupsAlways = false;
            BuildingDamageMultiplier = 0.1f;
            PartUpgrades = true;
            EnableFullSASInSandbox = false;

            // CommNet Options
            RequireSignalForControl = false;
            ReentryBlackout = false;
            RangeModifier = 0.8f;
            DsnModifier = 1.0f;
            OcclusionModifierVac = 1.0f;
            OcclusionModifierAtm = 0.85f;
            ExtraGroundstations = true;
        }

        public void SetHard()
        {
            CanRevert = false;
            MissingCrewsRespawn = false;
            RespawnTime = 2f;
            AutoHireCrews = false;
            BypassEntryPurchaseAfterResearch = false;
            IndestructibleFacilities = false;
            AllowStockVessels = false;
            AllowOtherLaunchSites = true;
            PerAgencyCareer = false;
            AllowEnablePerAgencyOnExistingUniverse = false;

            //Game systems
            ReentryHeatScale = 1.0f;
            ResourceAbundance = 0.5f;
            CommNetwork = true;

            // Career Settings
            StartingFunds = 10000.0f;
            StartingScience = 0.0f;
            StartingReputation = 0.0f;
            ScienceGainMultiplier = 0.6f;
            FundsGainMultiplier = 0.6f;
            RepGainMultiplier = 0.6f;
            FundsLossMultiplier = 2.0f;
            RepLossMultiplier = 2.0f;
            RepLossDeclined = 3.0f;

            // Advanced Options
            KerbalExp = true;
            ImmediateLevelUp = false;
            AllowNegativeCurrency = true;
            PressurePartLimits = false;
            GPartLimits = false;
            GKerbalLimits = false;
            KerbalGToleranceMult = 1.0f;
            ObeyCrossfeedRules = true;
            ActionGroupsAlways = false;
            BuildingDamageMultiplier = 0.2f;
            PartUpgrades = true;
            EnableFullSASInSandbox = false;

            // CommNet Options

            RequireSignalForControl = false;
            ReentryBlackout = false;
            RangeModifier = 0.65f;
            DsnModifier = 1.0f;
            OcclusionModifierVac = 1.0f;
            OcclusionModifierAtm = 1.0f;
            ExtraGroundstations = true;
        }
    }
}
