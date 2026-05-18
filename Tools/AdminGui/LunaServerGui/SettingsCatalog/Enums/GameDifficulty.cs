namespace LunaServerGui.SettingsCatalog.Enums;

// Duplicated from LmpCommon/Enums/GameDificulty.cs (sic, server typo).
// Underlying values match for XmlSerializer round-trip compatibility.
public enum GameDifficulty
{
    Easy = 0,
    Normal = 1,
    Moderate = 2,
    Hard = 3,
    Custom = 4
}
