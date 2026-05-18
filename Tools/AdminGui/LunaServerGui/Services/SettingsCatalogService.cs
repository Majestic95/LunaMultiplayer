using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LunaServerGui.Models;
using LunaServerGui.SettingsCatalog;
using LunaServerGui.SettingsCatalog.Definitions;

namespace LunaServerGui.Services;

/// <summary>
/// Reflects over the duplicated Definition POCOs to produce SettingsField
/// descriptors, and loads all priority settings files for a given config
/// directory. Six groups in slice 1D-1 (General, Connection, Gameplay,
/// Website, Log, MasterServer); the remaining 6 (CraftSettings,
/// DebugSettings, DedicatedServerSettings, IntervalSettings,
/// ScreenshotSettings, WarpSettings) land in a follow-up slice if the
/// reflection-driven approach pays off.
/// </summary>
public sealed class SettingsCatalogService
{
    private readonly SettingsXmlService _xml;

    /// <summary>
    /// Display order matches the spec §2 priority list. Stable across
    /// reloads so the operator's selected tab survives a Refresh.
    /// </summary>
    private static readonly (string DisplayName, string FileName, Type DefinitionType)[] PriorityGroups =
    {
        ("General",      "GeneralSettings.xml",       typeof(GeneralSettingsDefinition)),
        ("Connection",   "ConnectionSettings.xml",    typeof(ConnectionSettingsDefinition)),
        ("Gameplay",     "GameplaySettings.xml",      typeof(GameplaySettingsDefinition)),
        ("Website",      "WebsiteSettings.xml",       typeof(WebsiteSettingsDefinition)),
        ("Log",          "LogSettings.xml",           typeof(LogSettingsDefinition)),
        ("Master Server","MasterServerSettings.xml",  typeof(MasterServerSettingsDefinition)),
    };

    public SettingsCatalogService(SettingsXmlService xml)
    {
        _xml = xml ?? throw new ArgumentNullException(nameof(xml));
    }

    /// <summary>
    /// Load all priority settings files from <paramref name="configDirectory"/>.
    /// Always returns one SettingsGroup per priority entry — missing files
    /// produce a MissingFile status with POCO defaults so the operator still
    /// sees what fields would be present.
    /// </summary>
    public IReadOnlyList<SettingsGroup> LoadAll(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        var result = new List<SettingsGroup>(PriorityGroups.Length);
        foreach (var (displayName, fileName, definitionType) in PriorityGroups)
        {
            var path = Path.Combine(configDirectory, fileName);
            result.Add(LoadOne(displayName, path, definitionType));
        }
        return result;
    }

    private SettingsGroup LoadOne(string displayName, string path, Type definitionType)
    {
        object? instance;
        SettingsGroupStatus status;
        string? detail = null;

        try
        {
            instance = _xml.Read(definitionType, path);
            if (instance is null)
            {
                status = SettingsGroupStatus.MissingFile;
                detail = "File not yet created. The server creates it with defaults on first start.";
                instance = Activator.CreateInstance(definitionType);
            }
            else
            {
                status = SettingsGroupStatus.Loaded;
            }
        }
        catch (Exception ex)
        {
            status = SettingsGroupStatus.ParseError;
            // Strip newlines so the single-line Detail field stays readable —
            // XmlSerializer's InvalidOperationException messages can be
            // multi-line.
            detail = ex.GetBaseException().Message.Replace("\r", " ").Replace("\n", " ");
            instance = Activator.CreateInstance(definitionType);
        }

        var fields = DescribeFields(definitionType, instance!);
        return new SettingsGroup(displayName, path, status, detail, fields);
    }

    /// <summary>
    /// Reflect over <paramref name="definitionType"/>'s public instance
    /// properties (ones the XmlSerializer would round-trip) and project
    /// them into SettingsField descriptors. Preserves declaration order
    /// via MetadataToken sort — important so the form mirrors the layout
    /// of the source POCO (and hence the XML file).
    /// </summary>
    public IReadOnlyList<SettingsField> DescribeFields(Type definitionType, object instance)
    {
        ArgumentNullException.ThrowIfNull(definitionType);
        ArgumentNullException.ThrowIfNull(instance);

        return definitionType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken)
            .Select(p => new SettingsField(
                Name: p.Name,
                DeclaredType: p.PropertyType,
                Value: p.GetValue(instance),
                Comment: p.GetCustomAttribute<XmlCommentAttribute>()?.Value ?? string.Empty))
            .ToList();
    }
}
