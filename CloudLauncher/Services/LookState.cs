using System.ComponentModel;
using System.Windows.Media;

namespace CloudLauncher.Services;

/// <summary>The look currently on screen: the applied half of <see cref="LookSettings"/>, read by
/// self-drawn UI (<see cref="Controls.SlateBorder"/>, animations, the click sound) and bindable from
/// XAML through <see cref="Current"/>.</summary>
/// <remarks>
/// <para>Kept apart from the settings so drawing code never reads a half-edited settings object
/// (during a slider drag, say). <see cref="ThemeService.ApplyLook"/> is the only writer.</para>
/// <para><see cref="Version"/> bumps on every apply; self-drawn elements redraw when they come back
/// on screen with an older one, since a page kept alive off the visual tree misses the change.</para>
/// </remarks>
public sealed class LookState : INotifyPropertyChanged
{
    public static LookState Current { get; } = new();

    /// <summary>The Slate style is on (false = Classic).</summary>
    public bool IsSlate { get; private set; }

    /// <summary>Slate's Vanilla skin: stone-grey, bevelled buttons, square corners.</summary>
    public bool IsVanilla { get; private set; }

    /// <summary>Corner radius in pixel steps (0-4). Always 0 on the Vanilla skin.</summary>
    public int Radius { get; private set; } = 3;

    /// <summary>Hard two-pixel drop shadows under buttons, menus and raised cards.</summary>
    public bool Shadows { get; private set; }

    /// <summary>Animation speed multiplier: 0 = no motion, 1 = normal, 2 = half speed.</summary>
    public double Motion { get; private set; } = 1.0;

    /// <summary>Pages and tabs slide in.</summary>
    public bool Transitions { get; private set; } = true;

    /// <summary>Buttons click.</summary>
    public bool UiSounds { get; private set; }

    /// <summary>What the hard shadows are painted with; darker on the Dark skin than on Vanilla's
    /// lighter stone.</summary>
    public Brush ShadowBrush { get; private set; } = Brushes.Transparent;

    public int Version { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(LookSettings settings, Brush shadowBrush)
    {
        ShadowBrush = shadowBrush;
        IsSlate = settings.IsSlate;
        IsVanilla = IsSlate && settings.IsVanilla;
        Radius = IsVanilla ? 0 : Math.Clamp(settings.Radius, 0, LookSettings.MaxRadius);
        Shadows = IsSlate && settings.Shadows;
        // Motion is a Slate option; Classic always runs at normal speed.
        Motion = IsSlate ? Math.Clamp(settings.Motion, 0, 2) : 1.0;
        Transitions = !IsSlate || settings.Transitions;
        UiSounds = IsSlate && settings.UiSounds;
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    /// <summary>A duration in milliseconds scaled by the motion setting (0 when motion is off).</summary>
    public double Ms(double baseMs) => baseMs * Motion;
}
