using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LunaServerGui.ViewModels;

/// <summary>
/// Bool → IBrush converter for the per-field dirty highlight. True maps to
/// a translucent accent fill; false maps to <see cref="Brushes.Transparent"/>.
/// Lives in the ViewModels namespace so the view's XAML can reference it
/// alongside the VMs without an extra namespace import.
/// </summary>
public sealed class DirtyHighlightConverter : IValueConverter
{
    public static readonly DirtyHighlightConverter Instance = new();

    private static readonly IBrush DirtyBrush = new SolidColorBrush(Color.FromArgb(48, 0, 120, 215));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? DirtyBrush : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
