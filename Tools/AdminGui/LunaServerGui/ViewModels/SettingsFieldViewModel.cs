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

    /// <summary>Display-only string for locked / read-only contexts (same shape as 1D-1's SettingsField.DisplayValue).</summary>
    public string DisplayValue { get; }

    public SettingsFieldViewModel(PropertyInfo property, object instance, string comment, string? lockReason)
    {
        _property = property ?? throw new ArgumentNullException(nameof(property));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        Comment = comment ?? string.Empty;
        LockReason = lockReason;
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
        // Parse defensively so the dirty flag and parse error are always in
        // sync with the latest text. The actual commit happens on Save.
        if (TryParseTextToValue(value, out var parsed, out var error))
        {
            ParseError = null;
            IsDirty = !Equals(parsed, _originalValue);
        }
        else
        {
            ParseError = error;
            IsDirty = true; // Differs from baseline; just doesn't parse cleanly.
        }
    }

    partial void OnBoolValueChanged(bool value)
    {
        if (Editor != FieldEditorKind.Bool) return;
        IsDirty = !Equals(value, _originalValue);
    }

    partial void OnEnumValueChanged(string? value)
    {
        if (Editor != FieldEditorKind.Enum) return;
        IsDirty = !Equals(value, _originalValue?.ToString());
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
