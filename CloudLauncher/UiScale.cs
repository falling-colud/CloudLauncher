using System.Windows;
using System.Windows.Media;

namespace CloudLauncher;

/// <summary>
/// The user's UI-scale preferences plus a live "re-apply" signal. The global launcher scale is a
/// layout zoom applied to the whole main window; the mod-list scale is an extra zoom applied to the
/// mod-entry containers in the list and browse views (so it stacks on top of the launcher scale).
/// </summary>
public static class UiScale
{
    public const double Min = 0.7, Max = 1.6;

    public static double Launcher => Clamp(App.State.Settings.LauncherScale);
    public static double ModList  => Clamp(App.State.Settings.ModListScale);

    public static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, Min, Max) : 1.0;

    /// <summary>Raised after either scale changes, so the open window and views can re-apply at once.</summary>
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
