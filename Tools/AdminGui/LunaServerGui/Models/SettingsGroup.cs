using System.Collections.Generic;

namespace LunaServerGui.Models;

/// <summary>
/// One loaded settings file (e.g. GeneralSettings.xml). FilePath is the
/// absolute path the loader expected; if Status indicates a problem the
/// Fields collection is still populated with defaults from the Definition
/// POCO so the operator sees what fields the file would contain.
/// </summary>
public sealed record SettingsGroup(
    string DisplayName,
    string FilePath,
    SettingsGroupStatus Status,
    string? Detail,
    IReadOnlyList<SettingsField> Fields)
{
    /// <summary>
    /// True when the field values come from a successfully parsed file.
    /// False when the values are POCO defaults shown because the file is
    /// missing or unparseable. Bound to a "defaults shown" header so the
    /// operator does not mistake placeholder defaults for the file's actual
    /// content (upgrade-lens concern from slice 1D-1 review).
    /// </summary>
    public bool ValuesAreFromFile => Status == SettingsGroupStatus.Loaded;

    /// <summary>
    /// 1.0 when values are from the file, 0.55 when they are placeholder
    /// defaults. Bound directly to a ScrollViewer.Opacity to avoid needing
    /// a bool-to-opacity IValueConverter (Avalonia ships no built-in one).
    /// </summary>
    public double FieldsOpacity => ValuesAreFromFile ? 1.0 : 0.55;
}

public enum SettingsGroupStatus
{
    /// <summary>File loaded and parsed.</summary>
    Loaded,

    /// <summary>File does not exist on disk. Fields show POCO defaults.</summary>
    MissingFile,

    /// <summary>File exists but failed to deserialize. Fields show POCO defaults; Detail has the exception summary.</summary>
    ParseError,
}
