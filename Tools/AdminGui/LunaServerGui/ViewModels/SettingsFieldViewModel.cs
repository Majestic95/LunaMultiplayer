using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using LunaServerGui.SettingsCatalog;

namespace LunaServerGui.ViewModels;

public enum FieldEditorKind
{
    /// <summary>String / int / float / double — operator types InvariantCulture text.</summary>
    Text,
    /// <summary>bool — checkbox.</summary>
    Bool,
    /// <summary>enum — ComboBox of member names.</summary>
    Enum,
    /// <summary>Locked: editor surfaces are disabled. <see cref="LockReason"/> explains why.</summary>
    Locked,
}

/// <summary>
/// One editable field on a settings form. Wraps a PropertyInfo on the
/// loaded Definition POCO instance. Editor surfaces (<see cref="TextValue"/>,
/// <see cref="BoolValue"/>, <see cref="EnumValue"/>) are populated from the
/// POCO's current value at construction. <see cref="IsDirty"/> flips when
/// the editor value differs from the captured original; the view binds to
/// it for dirty-highlight.
///
/// On Save, <see cref="TryCommitToInstance"/> parses the editor surface back
/// into the POCO's PropertyType using InvariantCulture (matching the BUG-013
/// locale-bleed defence convention) and writes via PropertyInfo.SetValue.
/// Parse failures set <see cref="ParseError"/> and return false; the
/// group-level save is gated on every field returning true.
/// </summary>
public sealed partial class SettingsFieldViewModel : ObservableObject
{
    private readonly PropertyInfo _property;
    private readonly object _instance;
    private readonly object? _originalValue;
    private readonly IReadOnlyList<FieldValidationRule> _rules;
    private readonly IReadOnlyList<FieldWarningRule> _warnings;

    public string Name => _property.Name;
    public string TypeName => _property.PropertyType.Name;
    public string Comment { get; }
    public FieldEditorKind Editor { get; }
    public bool IsLocked => Editor == FieldEditorKind.Locked;
    public string? LockReason { get; }
    public IReadOnlyList<string>? EnumChoices { get; }

    public bool IsTextEditor => Editor == FieldEditorKind.Text && !IsLocked;
    public bool IsBoolEditor => Editor == FieldEditorKind.Bool && !IsLocked;
    public bool IsEnumEditor => Editor == FieldEditorKind.Enum && !IsLocked;

    [ObservableProperty] private string _textValue = string.Empty;
    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private string? _enumValue;
    [ObservableProperty] private string? _parseError;
    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// First-failing validation rule's operator-facing message, or null on
    /// pass. Distinct from <see cref="ParseError"/> — a ParseError means
    /// "your text isn't a valid value of this type"; a ValidationError
    /// means "the parsed value violates a spec rule (out of range, too
    /// long, etc.)". Save is gated on BOTH being clear.
    /// </summary>
    [ObservableProperty] private string? _validationError;

    /// <summary>
    /// Cross-field validation message routed here by the parent
    /// <see cref="SettingsGroupViewModel"/> after its
    /// <see cref="SettingsCatalog.CrossFieldValidator"/> runs. Distinct
    /// from <see cref="ValidationError"/> so the rule-eval path (which
    /// runs on per-field change) doesn't clobber the cross-field message.
    /// View surfaces both stacked.
    /// </summary>
    [ObservableProperty] private string? _crossFieldError;

    /// <summary>
    /// First-firing warning rule's operator-facing message, or null on
    /// no-warning. Distinct from <see cref="ValidationError"/>: warnings
    /// render amber and do NOT block Save — they flag dangerous-but-legal
    /// edits (e.g. PerAgencyCareer flipping mid-save). Spec §Validation-
    /// And-Safety-Rules requires the warning surface for the two
    /// PerAgency fields; slice 1D-4 also routes them into the Save-
    /// confirm dialog body via <see cref="LaunchSettingsViewModel"/>.
    /// </summary>
    [ObservableProperty] private string? _warningMessage;

    /// <summary>Display-only string for locked / read-only contexts (same shape as 1D-1's SettingsField.DisplayValue).</summary>
    public string DisplayValue { get; }

    public SettingsFieldViewModel(
        PropertyInfo property,
        object instance,
        string comment,
        string? lockReason,
        IReadOnlyList<FieldValidationRule>? rules = null,
        IReadOnlyList<FieldWarningRule>? warnings = null)
    {
        _property = property ?? throw new ArgumentNullException(nameof(property));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        Comment = comment ?? string.Empty;
        LockReason = lockReason;
        _rules = rules ?? Array.Empty<FieldValidationRule>();
        _warnings = warnings ?? Array.Empty<FieldWarningRule>();
        _originalValue = property.GetValue(instance);
        DisplayValue = FormatForDisplay(_originalValue);

        var type = property.PropertyType;
        if (lockReason is not null)
        {
            Editor = FieldEditorKind.Locked;
        }
        else if (type == typeof(bool))
        {
            Editor = FieldEditorKind.Bool;
            _boolValue = (bool)(_originalValue ?? false);
        }
        else if (type.IsEnum)
        {
            Editor = FieldEditorKind.Enum;
            EnumChoices = Enum.GetNames(type);
            _enumValue = _originalValue?.ToString();
        }
        else if (IsSupportedTextType(type))
        {
            Editor = FieldEditorKind.Text;
            _textValue = FormatForDisplay(_originalValue);
        }
        else
        {
            // Unknown type: treat as locked so we don't silently corrupt the
            // file. Logged at construction time via the LockReason surface.
            Editor = FieldEditorKind.Locked;
            LockReason = $"Editor not implemented for type {type.Name}. Edit this field by hand in the XML.";
        }
    }

    partial void OnTextValueChanged(string value)
    {
        if (Editor != FieldEditorKind.Text) return;
        if (TryParseTextToValue(value, out var parsed, out var error))
        {
            ParseError = null;
            IsDirty = !Equals(parsed, _originalValue);
            ValidationError = RunRules(parsed);
            WarningMessage = RunWarnings(parsed);
        }
        else
        {
            ParseError = error;
            IsDirty = true;
            ValidationError = null;
            WarningMessage = null;
        }
    }

    partial void OnBoolValueChanged(bool value)
    {
        if (Editor != FieldEditorKind.Bool) return;
        IsDirty = !Equals(value, _originalValue);
        ValidationError = RunRules(value);
        WarningMessage = RunWarnings(value);
    }

    partial void OnEnumValueChanged(string? value)
    {
        if (Editor != FieldEditorKind.Enum) return;
        IsDirty = !Equals(value, _originalValue?.ToString());
        ValidationError = RunRules(value);
        WarningMessage = RunWarnings(value);
    }

    /// <summary>
    /// First-failing rule's message, or null if all rules pass / no rules
    /// configured for this field. Called whenever the editor surface
    /// changes (post-parse) AND on initial construction to surface
    /// validation failures the operator inherits from the loaded file
    /// before they edit anything.
    /// </summary>
    private string? RunRules(object? parsedValue)
    {
        foreach (var rule in _rules)
        {
            var msg = rule.Validate(parsedValue);
            if (msg is not null) return msg;
        }
        return null;
    }

    /// <summary>
    /// First-firing warning's message, or null if no warning applies.
    /// Warnings don't block Save; they flag dangerous-but-legal edits.
    /// </summary>
    private string? RunWarnings(object? parsedValue)
    {
        foreach (var w in _warnings)
        {
            var msg = w.Evaluate(parsedValue);
            if (msg is not null) return msg;
        }
        return null;
    }

    /// <summary>
    /// Return the value Save would commit for this field — the parsed
    /// editor surface for Text editors, the typed BoolValue/EnumValue for
    /// the others, the on-disk original for locked fields. Returns null
    /// for a Text editor whose surface fails to parse. Used by the
    /// LaunchSettingsViewModel difficulty-flip check to compare each
    /// Gameplay field's would-be-saved value against the preset.
    /// </summary>
    internal object? GetEffectiveValue()
    {
        if (Editor == FieldEditorKind.Locked) return _originalValue;
        return Editor switch
        {
            FieldEditorKind.Bool => BoolValue,
            FieldEditorKind.Enum when EnumValue is not null && Enum.TryParse(_property.PropertyType, EnumValue, out var e) => e,
            FieldEditorKind.Text when TryParseTextToValue(TextValue, out var parsed, out _) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Write the editor surface's parsed value to <paramref name="target"/>
    /// (which may be a shadow instance built for cross-field validation,
    /// or the real wrapped instance on Save). Returns false if the editor
    /// surface fails to parse — caller skips this field. Does NOT mutate
    /// the real wrapped instance unless the caller passes it explicitly.
    /// </summary>
    internal bool WriteToInstance(object target)
    {
        if (Editor == FieldEditorKind.Locked) return false;
        switch (Editor)
        {
            case FieldEditorKind.Bool:
                _property.SetValue(target, BoolValue);
                return true;
            case FieldEditorKind.Enum:
                if (EnumValue is null || !Enum.TryParse(_property.PropertyType, EnumValue, out var enumParsed))
                    return false;
                _property.SetValue(target, enumParsed);
                return true;
            case FieldEditorKind.Text:
                if (!TryParseTextToValue(TextValue, out var parsed, out _))
                    return false;
                _property.SetValue(target, parsed);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Write the captured original value from the wrapped instance to
    /// <paramref name="target"/>. Used when building a shadow instance for
    /// cross-field validation — fields that aren't dirty (or whose edits
    /// don't parse) need their on-disk value visible to the validator.
    /// </summary>
    internal void WriteOriginalToInstance(object target)
        => _property.SetValue(target, _originalValue);

    /// <summary>
    /// Restore the editor surface to the field's captured original. Used
    /// by <see cref="SettingsGroupViewModel.RevertAll"/>. Goes through the
    /// editor's bindable property setter so the partial OnXxxChanged
    /// handlers fire and re-evaluate IsDirty + ValidationError.
    /// </summary>
    public void RevertToOriginal()
    {
        switch (Editor)
        {
            case FieldEditorKind.Bool:
                BoolValue = (bool)(_originalValue ?? false);
                break;
            case FieldEditorKind.Enum:
                EnumValue = _originalValue?.ToString();
                break;
            case FieldEditorKind.Text:
                TextValue = FormatForDisplay(_originalValue);
                if (TextValue == "(unset)") TextValue = string.Empty;
                break;
            case FieldEditorKind.Locked:
                // Locked fields never had an editor surface that diverged
                // from the original; nothing to revert.
                break;
        }
    }

    /// <summary>
    /// Run rules + warnings against the current editor surface. Used by
    /// the parent group on construction to populate ValidationError +
    /// WarningMessage for fields that inherit a validation failure or
    /// warning from the loaded XML (e.g. operator edited the file by
    /// hand and put MaxPlayers=99999, or already had PerAgencyCareer=true).
    /// </summary>
    public void EvaluateRulesNow()
    {
        if (Editor == FieldEditorKind.Locked) return;
        object? currentValue = Editor switch
        {
            FieldEditorKind.Bool => BoolValue,
            FieldEditorKind.Enum => EnumValue,
            FieldEditorKind.Text when TryParseTextToValue(TextValue, out var parsed, out _) => parsed,
            _ => null,
        };
        ValidationError = RunRules(currentValue);
        WarningMessage = RunWarnings(currentValue);
    }

    /// <summary>
    /// Parse the editor surface back into the PropertyType and write it to
    /// the wrapped POCO instance. Returns true on success; false if parsing
    /// failed (in which case <see cref="ParseError"/> is set).
    /// </summary>
    public bool TryCommitToInstance()
    {
        if (Editor == FieldEditorKind.Locked) return true; // No-op: nothing to commit.
        if (!IsDirty) return true;                          // Unchanged: nothing to write.

        switch (Editor)
        {
            case FieldEditorKind.Bool:
                _property.SetValue(_instance, BoolValue);
                return true;

            case FieldEditorKind.Enum:
                if (EnumValue is null || !Enum.TryParse(_property.PropertyType, EnumValue, out var enumParsed))
                {
                    ParseError = $"'{EnumValue}' is not a valid {_property.PropertyType.Name} value.";
                    return false;
                }
                _property.SetValue(_instance, enumParsed);
                return true;

            case FieldEditorKind.Text:
                if (!TryParseTextToValue(TextValue, out var parsed, out var error))
                {
                    ParseError = error;
                    return false;
                }
                _property.SetValue(_instance, parsed);
                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// Returns a short, operator-readable description of the change for the
    /// Save-confirm dialog (e.g. "Cheats: true → false"). Empty string if
    /// the field is not dirty.
    /// </summary>
    public string ChangeSummary()
    {
        if (!IsDirty) return string.Empty;
        var newDisplay = Editor switch
        {
            FieldEditorKind.Bool => BoolValue ? "true" : "false",
            FieldEditorKind.Enum => EnumValue ?? "(unset)",
            FieldEditorKind.Text => TextValue,
            _ => "(unchangeable)",
        };
        return $"{Name}: {DisplayValue} → {newDisplay}";
    }

    private bool TryParseTextToValue(string text, out object? value, out string? error)
    {
        var t = _property.PropertyType;
        try
        {
            if (t == typeof(string))
            {
                value = text;
                error = null;
                return true;
            }
            if (t == typeof(int))
            {
                value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                error = null;
                return true;
            }
            if (t == typeof(float))
            {
                value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                error = null;
                return true;
            }
            if (t == typeof(double))
            {
                value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                error = null;
                return true;
            }
            value = null;
            error = $"Editor cannot parse type {t.Name}.";
            return false;
        }
        catch (FormatException)
        {
            value = null;
            error = $"'{text}' is not a valid {t.Name}.";
            return false;
        }
        catch (OverflowException)
        {
            value = null;
            error = $"'{text}' is out of range for {t.Name}.";
            return false;
        }
    }

    private static bool IsSupportedTextType(Type t)
        => t == typeof(string) || t == typeof(int) || t == typeof(float) || t == typeof(double);

    private static string FormatForDisplay(object? value) => value switch
    {
        null => "(unset)",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Compose the per-property comment + lock reason into one block of help
    /// text. Locked fields surface BOTH so the operator sees the original
    /// guidance plus the GUI-imposed restriction.
    /// </summary>
    public string HelpText => LockReason is null
        ? Comment
        : string.IsNullOrEmpty(Comment) ? LockReason : $"{Comment}\n\n[LOCKED] {LockReason}";
}
