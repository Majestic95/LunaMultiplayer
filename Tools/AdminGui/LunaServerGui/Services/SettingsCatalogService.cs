using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LunaServerGui.Models;
using LunaServerGui.SettingsCatalog;
using LunaServerGui.SettingsCatalog.Definitions;
using LunaServerGui.ViewModels;

namespace LunaServerGui.Services;

/// <summary>
/// Loads the six priority settings files from a server's Config/ folder and
/// builds editable group view-models. Reflects over the duplicated POCOs to
/// produce per-field VMs in declaration order. Centralises the lock-list
/// for fields that 1D-2 deliberately refuses to edit (PerAgencyCareer +
/// AllowEnablePerAgencyOnExistingUniverse are deferred to slice 1D-4's
/// confirm-dialog gate per spec §Validation-And-Safety-Rules).
///
/// The remaining six settings groups (CraftSettings, DebugSettings,
/// DedicatedServerSettings, IntervalSettings, ScreenshotSettings,
/// WarpSettings) are deliberately deferred to a follow-up slice if the
/// reflection-driven approach pays off in production use.
/// </summary>
public sealed class SettingsCatalogService
{
    private readonly SettingsXmlService _xml;

    private static readonly (string DisplayName, string FileName, Type DefinitionType)[] PriorityGroups =
    {
        ("General",       "GeneralSettings.xml",      typeof(GeneralSettingsDefinition)),
        ("Connection",    "ConnectionSettings.xml",   typeof(ConnectionSettingsDefinition)),
        ("Gameplay",      "GameplaySettings.xml",     typeof(GameplaySettingsDefinition)),
        ("Website",       "WebsiteSettings.xml",      typeof(WebsiteSettingsDefinition)),
        ("Log",           "LogSettings.xml",          typeof(LogSettingsDefinition)),
        ("Master Server", "MasterServerSettings.xml", typeof(MasterServerSettingsDefinition)),
    };

    /// <summary>
    /// Fields the GUI deliberately refuses to edit in slice 1D-2. Keyed by
    /// (DeclaringType, PropertyName). The value is the operator-readable
    /// reason shown in the tooltip + the field's help text. Slice 1D-4 will
    /// remove these entries and replace them with a confirm-dialog gate.
    /// </summary>
    private static readonly Dictionary<(Type Type, string Property), string> LockedFields = new()
    {
        [(typeof(GameplaySettingsDefinition), nameof(GameplaySettingsDefinition.PerAgencyCareer))] =
            "PerAgencyCareer cannot be changed mid-save without losing accumulated career state. " +
            "A dedicated confirm dialog (slice 1D-4) will gate this edit; for now, edit the XML file " +
            "directly before the universe is first populated.",
        [(typeof(GameplaySettingsDefinition), nameof(GameplaySettingsDefinition.AllowEnablePerAgencyOnExistingUniverse))] =
            "Enabling per-agency on an existing universe is an irreversible operation that will hide " +
            "accumulated shared-agency progress from per-agency clients. A dedicated advanced-confirm " +
            "dialog (slice 1D-4) will gate this edit; do not toggle it casually.",
    };

    public SettingsCatalogService(SettingsXmlService xml)
    {
        _xml = xml ?? throw new ArgumentNullException(nameof(xml));
    }

    /// <summary>
    /// Load all priority settings files from <paramref name="configDirectory"/>
    /// and produce one editable group view-model per file. Synchronous I/O;
    /// callers (LaunchSettingsViewModel.ReloadAsync) wrap with Task.Run to
    /// keep the UI thread free.
    /// </summary>
    public IReadOnlyList<SettingsGroupViewModel> LoadGroupViewModels(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        var result = new List<SettingsGroupViewModel>(PriorityGroups.Length);
        foreach (var (displayName, fileName, definitionType) in PriorityGroups)
        {
            var path = Path.Combine(configDirectory, fileName);
            result.Add(LoadOne(displayName, path, definitionType));
        }
        return result;
    }

    private SettingsGroupViewModel LoadOne(string displayName, string path, Type definitionType)
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
            // Strip newlines so the single-line Detail surface stays readable
            // (XmlSerializer's InvalidOperationException messages can be
            // multi-line). Append a recovery hint — the GUI deliberately
            // refuses to save over a parse-broken file, so the operator
            // needs a path forward.
            var msg = ex.GetBaseException().Message.Replace("\r", " ").Replace("\n", " ");
            detail = $"{msg} — fix the file in a text editor (e.g. Notepad) and click Reload. The GUI refuses to overwrite a parse-broken file to protect from accidental data loss.";
            instance = Activator.CreateInstance(definitionType);
        }

        var fieldVms = BuildFieldVms(definitionType, instance!);
        return new SettingsGroupViewModel(_xml, displayName, path, status, detail, instance!, fieldVms);
    }

    private static IReadOnlyList<SettingsFieldViewModel> BuildFieldVms(Type definitionType, object instance)
    {
        // MetadataToken sort preserves declaration order — important so the
        // form mirrors the XML file layout. Holds for the 6 priority POCOs
        // (none use partial classes; if a future Definition does, the
        // ordering is compiler-implementation-defined and needs revisiting).
        return definitionType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken)
            .Select(p =>
            {
                var comment = p.GetCustomAttribute<XmlCommentAttribute>()?.Value ?? string.Empty;
                LockedFields.TryGetValue((definitionType, p.Name), out var lockReason);
                return new SettingsFieldViewModel(p, instance, comment, lockReason);
            })
            .ToList();
    }
}
