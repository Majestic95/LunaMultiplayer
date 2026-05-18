using CommNet;
using LmpCommon.Enums;

namespace LmpClient.Systems.SettingsSys
{
    public class SettingsServerStructure
    {
        public WarpMode WarpMode { get; set; } = WarpMode.Subspace;
        public GameParameters ServerParameters { get; set; }
        public GameParameters.AdvancedParams ServerAdvancedParameters { get; set; } = new GameParameters.AdvancedParams();
        public CommNetParams ServerCommNetParameters { get; set; } = new CommNetParams();
        public GameMode GameMode { get; set; }
        public TerrainQuality TerrainQuality { get; set; }
        public bool AllowCheats { get; set; }
        public bool AllowAdmin { get; set; }
        public bool AllowSackKerbals { get; set; }
        public int MaxNumberOfAsteroids { get; set; }
        public int MaxNumberOfComets { get; set; }
        public string ConsoleIdentifier { get; set; } = "";
        public GameDifficulty GameDifficulty { get; set; }
        public float SafetyBubbleDistance { get; set; } = 100f;
        public int MaxVesselParts { get; set; }
        public int VesselUpdatesMsInterval { get; set; }
        public int SecondaryVesselUpdatesMsInterval { get; set; }
        public int MinScreenshotIntervalMs { get; set; }
        public int MaxScreenshotWidth { get; set; }
        public int MaxScreenshotHeight { get; set; }
        public int MinCraftLibraryRequestIntervalMs { get; set; }
        public bool PrintMotdInChat { get; set; }

        /// <summary>
        /// Mirrors <c>SettingsReplyMsgData.PerAgencyCareerEnabled</c> — the server's
        /// combined gate (<c>PerAgencyCareer=true AND GameMode=Career</c>). Stage 5.18a
        /// surfaces this so the future 5.18c UI / 5.18b write-path patches can early-
        /// out without sniffing protocol versions. Defaults to false so a server that
        /// ships pre-0.31 protocol (no tail byte for this field on the wire) reads as
        /// per-agency-off.
        /// </summary>
        public bool PerAgencyCareerEnabled { get; set; }
    }
}
