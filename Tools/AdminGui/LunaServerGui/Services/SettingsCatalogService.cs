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

    /// <summary>
    /// Per-field validation rules sourced from spec §Validation-And-Safety-
    /// Rules + the duplicated POCOs' XmlComment hints. Rule evaluation runs
    /// on the post-parse typed value, never raw editor text. Multiple rules
    /// per field stack — first failing rule wins on the operator-facing
    /// message. Keyed by (Type, PropertyName) for the same shape as
    /// <see cref="LockedFields"/>.
    /// </summary>
    private static readonly Dictionary<(Type Type, string Property), FieldValidationRule[]> FieldRules = new()
    {
        // GeneralSettings — string max-lengths + non-empty server identity
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.ServerName))]
            = new FieldValidationRule[] { new RequiredRule(), new MaxLengthRule(30) },
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.Description))]
            = new FieldValidationRule[] { new MaxLengthRule(200) },
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.CountryCode))]
            = new FieldValidationRule[] { new MaxLengthRule(2) },
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.WebsiteText))]
            = new FieldValidationRule[] { new MaxLengthRule(15) },
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.Website))]
            = new FieldValidationRule[] { new MaxLengthRule(60) },
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.Password))]
            = new FieldValidationRule[] { new MaxLengthRule(30) },
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.AdminPassword))]
            = new FieldValidationRule[] { new MaxLengthRule(30) },
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.ServerMotd))]
            = new FieldValidationRule[] { new MaxLengthRule(255) },
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.MaxPlayers))]
            = new FieldValidationRule[] { new NumericRangeRule(1, 10000) },
        [(typeof(GeneralSettingsDefinition), nameof(GeneralSettingsDefinition.MaxUsernameLength))]
            = new FieldValidationRule[] { new NumericRangeRule(1, 100) },

        // ConnectionSettings — ports + MTU + interval sanity
        [(typeof(ConnectionSettingsDefinition), nameof(ConnectionSettingsDefinition.Port))]
            = new FieldValidationRule[] { new NumericRangeRule(1, 65535) },
        [(typeof(ConnectionSettingsDefinition), nameof(ConnectionSettingsDefinition.HearbeatMsInterval))]
            = new FieldValidationRule[] { new MinValueRule(1) },
        [(typeof(ConnectionSettingsDefinition), nameof(ConnectionSettingsDefinition.ConnectionMsTimeout))]
            = new FieldValidationRule[] { new MinValueRule(1) },
        [(typeof(ConnectionSettingsDefinition), nameof(ConnectionSettingsDefinition.UpnpMsTimeout))]
            = new FieldValidationRule[] { new MinValueRule(1) },
        [(typeof(ConnectionSettingsDefinition), nameof(ConnectionSettingsDefinition.MaximumTransmissionUnit))]
            = new FieldValidationRule[] { new NumericRangeRule(1, 8192) },

        // WebsiteSettings — port + refresh interval
        [(typeof(WebsiteSettingsDefinition), nameof(WebsiteSettingsDefinition.Port))]
            = new FieldValidationRule[] { new NumericRangeRule(1, 65535) },
        [(typeof(WebsiteSettingsDefinition), nameof(WebsiteSettingsDefinition.RefreshIntervalMs))]
            = new FieldValidationRule[] { new MinValueRule(100) },

        // MasterServerSettings — server's explicit "Min value = 5000" floor
        [(typeof(MasterServerSettingsDefinition), nameof(MasterServerSettingsDefinition.MasterServerRegistrationMsInterval))]
            = new FieldValidationRule[] { new MinValueRule(5000) },

        // LogSettings — non-negative expire
        [(typeof(LogSettingsDefinition), nameof(LogSettingsDefinition.ExpireLogs))]
            = new FieldValidationRule[] { new NumericRangeRule(0, 36500) },
    };

    private static readonly Dictionary<string, string> EmptyErrors = new();

    /// <summary>
    /// Cross-field rules: evaluated against the loaded POCO instance.
    /// Currently only one rule (HearbeatMsInterval &lt; ConnectionMsTimeout
    /// in ConnectionSettings, per spec §Validation-And-Safety-Rules);
    /// cross-group rules like Connection.Port vs Website.Port collision are
    /// deferred — they require coordination across SettingsGroupViewModels
    /// and the spec marks port-conflicts as "where possible".
    /// EmptyErrors is declared above this so the lambda capture sees a
    /// non-null reference at static-init time.
    /// </summary>
    private static readonly Dictionary<Type, CrossFieldValidator> CrossFieldValidators = new()
    {
        [typeof(ConnectionSettingsDefinition)] = static instance =>
        {
            var conn = (ConnectionSettingsDefinition)instance;
            if (conn.HearbeatMsInterval >= conn.ConnectionMsTimeout)
            {
                // Flag the heartbeat field — it's the one that's "too high"
                // semantically. Operator typically tunes heartbeat-down to
                // fix this rather than timeout-up.
                return new Dictionary<string, string>
                {
                    [nameof(ConnectionSettingsDefinition.HearbeatMsInterval)] =
                        $"Must be less than ConnectionMsTimeout ({conn.ConnectionMsTimeout}ms). " +
                        $"A heartbeat ≥ timeout means clients drop before sending one.",
                };
            }
            return EmptyErrors;
        },
    };

    internal static CrossFieldValidator? GetCrossFieldValidator(Type definitionType)
        => CrossFieldValidators.TryGetValue(definitionType, out var v) ? v : null;

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
        var crossFieldValidator = GetCrossFieldValidator(definitionType);
        return new SettingsGroupViewModel(_xml, displayName, path, status, detail, instance!, fieldVms, crossFieldValidator);
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
                FieldRules.TryGetValue((definitionType, p.Name), out var rules);
                return new SettingsFieldViewModel(p, instance, comment, lockReason, rules);
            })
            .ToList();
    }
}
