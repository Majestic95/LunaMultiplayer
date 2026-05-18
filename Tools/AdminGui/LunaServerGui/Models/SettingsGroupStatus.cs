namespace LunaServerGui.Models;

/// <summary>
/// Outcome of trying to load one settings file. The two non-Loaded states
/// drive the "POCO defaults shown" UI in the form so the operator does
/// not mistake placeholder defaults for real file content.
/// </summary>
public enum SettingsGroupStatus
{
    /// <summary>File loaded and parsed.</summary>
    Loaded,

    /// <summary>File does not exist on disk. The form shows POCO defaults.</summary>
    MissingFile,

    /// <summary>File exists but failed to deserialize. The form shows POCO defaults; Detail has the exception summary.</summary>
    ParseError,
}
