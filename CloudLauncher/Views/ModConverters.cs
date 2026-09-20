using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Priority (int) → the shared border brush from <see cref="PriorityPalette"/>.</summary>
public sealed class PriorityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => PriorityPalette.BorderFor(value is int p ? p : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Priority (int) → Visible when emphasized (non-zero), Collapsed otherwise.</summary>
public sealed class PriorityToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is int p && p != 0) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True → Visible, False → Collapsed. Pass parameter "invert" to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Non-null/non-empty string → Visible, else Collapsed.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>A <see cref="ModSide"/> → short label ("Both" / "Client" / "Server").</summary>
public sealed class ModSideLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ModSide s ? s switch { ModSide.Client => "Client", ModSide.Server => "Server", _ => "Both" } : "Both";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Paints a category group header in that category's own colour, so the headings in the List view
/// match the swatches on the Categories page and the clusters in the Graph.
/// </summary>
/// <remarks>
/// The colour is per pack and a value converter has no pack, so the view hands it a lookup when it
/// loads. Static because exactly one Modpack Management page is open at a time — and if none is, the
/// fallback is just the muted text colour, which is the right answer for a header anyway.
/// </remarks>
public sealed class CategoryToBrushConverter : IValueConverter
{
    /// <summary>Set by the view: category name → its brush, or null when it has no colour.</summary>
    public static Func<string, Brush?>? Lookup;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fallback = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        if (value is not string name || Lookup is null) return fallback;
        return Lookup(name) ?? fallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
