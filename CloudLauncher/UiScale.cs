using System.Windows;
using System.Windows.Media;

namespace CloudLauncher;

/// <summary>
/// UI scale settings. The launcher scale zooms the whole main window; the mod-list scale is an
/// extra zoom on the mod entries in the list and browse views, on top of the launcher scale.
/// </summary>
public static class UiScale
{
    public const double Min = 0.7, Max = 1.6;

    public static double Launcher => Clamp(App.State.Settings.LauncherScale);
    public static double ModList  => Clamp(App.State.Settings.ModListScale);

    public static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, Min, Max) : 1.0;

    /// <summary>Raised after either scale changes so open views can re-apply it.</summary>
    public static event Action? Changed;
    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>Applies (or clears, at 100%) the mod-list scale as a uniform LayoutTransform.</summary>
    public static void ApplyModListScale(FrameworkElement? container)
    {
        if (container is null) return;
        var s = ModList;
        container.LayoutTransform = Math.Abs(s - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);
    }
}
