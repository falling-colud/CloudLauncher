using System.Windows;
using System.Windows.Media.Animation;

namespace CloudLauncher.Animations;

/// <summary>
/// Animates a <see cref="GridLength"/> (e.g. a <c>ColumnDefinition.Width</c> or
/// <c>RowDefinition.Height</c>) between two values. WPF has no built-in animation for
/// <see cref="GridLength"/>, so the sidebar expand/collapse needs this. Only absolute
/// (pixel) lengths are interpolated; Star/Auto are snapped to <see cref="To"/> at the end.
/// </summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation));

    public static readonly DependencyProperty EasingFunctionProperty =
        DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation));

    public GridLength? From
    {
        get => (GridLength?)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
    {
        var fromVal = (From ?? (GridLength)defaultOriginValue);
        var toVal = To;

        // Only pixel lengths interpolate meaningfully; anything else snaps to the target.
        if (!fromVal.IsAbsolute || !toVal.IsAbsolute)
            return toVal;

        double progress = clock.CurrentProgress ?? 0d;
        if (EasingFunction is { } ease)
            progress = ease.Ease(progress);

        double value = fromVal.Value + (toVal.Value - fromVal.Value) * progress;
        return new GridLength(value, GridUnitType.Pixel);
    }
}
