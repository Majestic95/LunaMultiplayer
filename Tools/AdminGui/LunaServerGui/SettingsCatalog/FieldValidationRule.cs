using System;
using System.Globalization;

namespace LunaServerGui.SettingsCatalog;

/// <summary>
/// Per-field validation rule applied to the parsed value of a settings
/// field before the operator can Save. Returns null on pass; an operator-
/// readable message on fail. Rules live in
/// <see cref="LunaServerGui.Services.SettingsCatalogService"/>'s rules
/// map keyed by (Type, PropertyName) — the POCOs themselves stay
/// byte-identical to the server's.
///
/// Rules ONLY run against post-parse typed values, never raw editor text
/// — that means a ParseError (e.g. "not a valid Int32") suppresses the
/// rule evaluation; the operator must fix the parse first.
/// </summary>
public abstract record FieldValidationRule
{
    /// <summary>Returns null on pass, a one-line operator-facing message on fail.</summary>
    public abstract string? Validate(object? parsedValue);
}

/// <summary>
/// Caps a string's length. Sourced from XmlComment hints like "Max 30 char"
/// in the duplicated POCO files — captured here as a declarative rule so
/// the comment text stays human-only.
/// </summary>
public sealed record MaxLengthRule(int MaxLength) : FieldValidationRule
{
    public override string? Validate(object? parsedValue)
    {
        if (parsedValue is not string s) return null;
        return s.Length > MaxLength
            ? $"Too long ({s.Length} chars). Max is {MaxLength}."
            : null;
    }
}

/// <summary>
/// Closed-interval inclusive range on a numeric value. Operator messages
/// use InvariantCulture so the displayed bounds match what the operator
/// typed in the editor.
/// </summary>
public sealed record NumericRangeRule(double Min, double Max) : FieldValidationRule
{
    public override string? Validate(object? parsedValue)
    {
        if (parsedValue is null) return null;
        if (!TryConvert(parsedValue, out var d)) return null;
        if (d < Min || d > Max)
        {
            return $"Out of range. Allowed: {Min.ToString("G", CultureInfo.InvariantCulture)}–" +
                   $"{Max.ToString("G", CultureInfo.InvariantCulture)} (you entered " +
                   $"{d.ToString("G", CultureInfo.InvariantCulture)}).";
        }
        return null;
    }

    private static bool TryConvert(object value, out double d)
    {
        try
        {
            d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            d = 0;
            return false;
        }
    }
}

/// <summary>
/// One-sided lower bound. Used for fields like
/// MasterServerRegistrationMsInterval where the server enforces a floor
/// (min 5000ms per its XmlComment).
/// </summary>
public sealed record MinValueRule(double Min) : FieldValidationRule
{
    public override string? Validate(object? parsedValue)
    {
        if (parsedValue is null) return null;
        try
        {
            var d = Convert.ToDouble(parsedValue, CultureInfo.InvariantCulture);
            return d < Min
                ? $"Too low. Minimum is {Min.ToString("G", CultureInfo.InvariantCulture)} (you entered {d.ToString("G", CultureInfo.InvariantCulture)})."
                : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// String must be non-empty after trim. Used for fields where the server
/// rejects an empty value at startup.
/// </summary>
public sealed record RequiredRule() : FieldValidationRule
{
    public override string? Validate(object? parsedValue)
    {
        if (parsedValue is string s && string.IsNullOrWhiteSpace(s))
            return "Required. Cannot be empty.";
        return null;
    }
}

/// <summary>
/// Cross-field rule: evaluates the whole instance of a Definition POCO
/// and returns zero-or-more per-field error messages keyed by property
/// name. Lets us encode "HearbeatMsInterval &lt; ConnectionMsTimeout"
/// without coupling either field to the other.
/// </summary>
public delegate System.Collections.Generic.IReadOnlyDictionary<string, string>
    CrossFieldValidator(object instance);

/// <summary>
/// Per-field WARNING rule. Distinct from <see cref="FieldValidationRule"/>:
/// warnings do NOT block Save (operator can proceed with caveat), they
/// render in amber instead of red, and they fire on dangerous-but-legal
/// edits where the operator should think twice. Used in slice 1D-4 for
/// PerAgencyCareer + AllowEnablePerAgencyOnExistingUniverse — both
/// fields are legal to change but the consequences are irreversible
/// (spec §Validation-And-Safety-Rules).
/// </summary>
public abstract record FieldWarningRule
{
    /// <summary>Returns null on no-warning; an operator-facing message when a warning applies.</summary>
    public abstract string? Evaluate(object? parsedValue);
}

/// <summary>
/// Warning fires when a bool field is set to <paramref name="WhenValueIs"/>.
/// Lets us flag "PerAgencyCareer=true" without flagging "PerAgencyCareer=false".
/// </summary>
public sealed record BoolValueWarning(bool WhenValueIs, string Message) : FieldWarningRule
{
    public override string? Evaluate(object? parsedValue)
        => parsedValue is bool b && b == WhenValueIs ? Message : null;
}
