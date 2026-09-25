using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>Priority (int) -> the shared border brush from <see cref="PriorityPalette"/>.</summary>
public sealed class PriorityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => PriorityPalette.BorderFor(value is int p ? p : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Priority (int) -> Visible when non-zero, Collapsed otherwise.</summary>
public sealed class PriorityToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is int p && p != 0) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True -> Visible, false -> Collapsed. Pass "invert" as the parameter to flip.</summary>
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

/// <summary>Non-empty string -> Visible, else Collapsed.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>A <see cref="ModSide"/> -> short label ("Both" / "Client" / "Server").</summary>
public sealed class ModSideLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ModSide s ? s switch { ModSide.Client => "Client", ModSide.Server => "Server", _ => "Both" } : "Both";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Paints a category group header in that category's colour, matching the Categories page and
/// the Graph.
/// </summary>
/// <remarks>
/// Colours are per pack and a converter has no pack, so the view sets Lookup when it loads.
/// Static because only one Modpack Management page is open at a time; with none, headers fall
/// back to the muted text colour.
/// </remarks>
public sealed class CategoryToBrushConverter : IValueConverter
{
    /// <summary>Set by the view: category name -> its brush, or null when it has no colour.</summary>
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

/// <summary>
/// Icon URL -> a cached, downscaled <see cref="ImageSource"/> from <see cref="ModIconCache"/>.
/// The <c>ConverterParameter</c> is the on-screen icon width in DIPs; the cache decodes at 2x that.
/// </summary>
/// <remarks>
/// Binding a URL straight to <c>Image.Source</c> downloads with no concurrency cap, decodes at full
/// size and throws on every empty URL. This returns the cached, frozen bitmap, or null.
/// A converter only runs once, so a cache miss stays empty until the view is rebuilt. That suits
/// the graph and planning board; virtualized lists should use <see cref="IconLoader"/> instead.
/// </remarks>
public sealed class ModIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string url && !string.IsNullOrWhiteSpace(url)
            ? ModIconCache.Get(url, WidthFrom(parameter))
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    /// <summary>XAML hands the parameter through as a string; code-built bindings pass a number.</summary>
    private static int WidthFrom(object? parameter) => parameter switch
    {
        int i => i,
        double dbl => (int)dbl,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 32,
    };
}
