using System;

namespace LunaServerGui.Models;

/// <summary>
/// One row in a settings form: the property's name, declared type, current
/// value, and the inline help text from the [XmlComment] attribute (empty
/// string if absent). Read-only descriptor — slice 1D-2 will add a setter
/// path; slice 1D-3 will add validation metadata (max-length, ranges, etc.).
/// </summary>
public sealed record SettingsField(
    string Name,
    Type DeclaredType,
    object? Value,
    string Comment)
{
    /// <summary>
    /// Culture-invariant string rendering of <see cref="Value"/> for read-only
    /// display. Avoids the LMP precedent of locale-bleed bugs (BUG-013) by
    /// pinning InvariantCulture for numeric ToString. Null renders as
    /// "(unset)" so the form distinguishes empty-string from missing.
    /// </summary>
    public string DisplayValue => Value switch
    {
        null => "(unset)",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => Value.ToString() ?? string.Empty,
    };
}
