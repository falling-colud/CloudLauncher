using System.Windows;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The content-size indicator: three ascending bars, filled up to the mod's
/// <see cref="ModMeta.ContentSize"/>. A different visual cue from priority, which uses the coloured
/// stripe and border. Collapses at size 0, since an empty meter on every untagged mod is noise.
/// </summary>
public sealed class ContentSizeMeter : FrameworkElement
{
    private const double BarW = 3, Gap = 2, MinH = 4, StepH = 3;

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(int), typeof(ContentSizeMeter),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnSizeChanged));

    public int Size
    {
        get => (int)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public ContentSizeMeter()
    {
        Width = ModContentSize.Max * BarW + (ModContentSize.Max - 1) * Gap;   // 13
        Height = MinH + (ModContentSize.Max - 1) * StepH;                     // 10
        VerticalAlignment = VerticalAlignment.Center;
        Apply();
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ContentSizeMeter)d).Apply();

    /// <summary>Sets visibility and tooltip in one place, so the meter behaves the same on list rows,
    /// board cards and graph nodes.</summary>
    private void Apply()
    {
        var size = ModContentSize.Clamp(Size);
        Visibility = size == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTip = size == 0 ? null : "Content size · " + ModContentSize.Describe(size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = ModContentSize.Clamp(Size);
        if (size == 0) return;

        var on = Brush("AccentBrush", Brushes.SteelBlue);
        var off = Brush("BorderStrongBrush", Brushes.Gray);

        for (var i = 0; i < ModContentSize.Max; i++)
        {
            var h = MinH + i * StepH;
            var rect = new Rect(i * (BarW + Gap), Height - h, BarW, h);
            dc.DrawRoundedRectangle(i < size ? on : off, null, rect, 1, 1);
        }
    }

    /// <summary>Theme brush by key, with a fallback so the control still renders if it's built before
    /// the resource dictionaries are reachable (e.g. in a designer surface).</summary>
    private Brush Brush(string key, Brush fallback)
    {
        try { return (Brush)FindResource(key); }
        catch { return fallback; }
    }
}
