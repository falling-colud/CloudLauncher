using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CloudLauncher.Animations;

namespace CloudLauncher.Views;

/// <summary>A chosen colour, where a null <see cref="Hex"/> means "clear it". Cancel returns no
/// choice at all.</summary>
public readonly record struct ColorChoice(string? Hex);

/// <summary>
/// HSV colour picker (saturation/value square, hue strip, hex entry, preset swatches) shown as an
/// in-window card. Used for category colours, where a fixed eight-colour palette runs out fast.
/// </summary>
public partial class ColorPickerDialog : UserControl
{
    private readonly TaskCompletionSource<ColorChoice?> _tcs = new();

    private double _hue;          // 0-360
    private double _sat = 1;      // 0-1
    private double _val = 1;      // 0-1
    private bool _syncing;        // guards the hex box <-> sliders round trip
    private bool _draggingShade, _draggingHue;

    public ColorPickerDialog(string title, string? initial, IEnumerable<string>? presets = null)
    {
        InitializeComponent();
        TitleLabel.Text = title;

        var start = ParseHex(initial) ?? Color.FromRgb(0x5B, 0x9D, 0xF9);
        (_hue, _sat, _val) = ToHsv(start);

        BuildSwatches(presets);
        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            SyncAll();
        };
    }

    public Task<ColorChoice?> Result => _tcs.Task;
    public void Cancel() => _tcs.TrySetResult(null);

    /// <summary>Shows the picker over the main window. Null means cancelled; a result with a null
    /// hex means the user chose "No colour".</summary>
    public static async Task<ColorChoice?> ShowAsync(MainWindow host, string title, string? initial,
        IEnumerable<string>? presets = null)
    {
        var card = new ColorPickerDialog(title, initial, presets);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    // ── swatches ────────────────────────────────────────────────────────────────

    private void BuildSwatches(IEnumerable<string>? extra)
    {
        // Shared accents, then a broader grid so there's always an unused colour, then the colours
        // already used in this pack.
        var hexes = AccentPalette.Colors.Select(c => c.Hex)
            .Concat(ExtendedPalette())
            .Concat(extra ?? Array.Empty<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var hex in hexes)
        {
            var color = ParseHex(hex);
            if (color is null) continue;

            var swatch = new CloudLauncher.Controls.SlateBorder
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(color.Value),
                BorderBrush = (Brush)FindResource("BorderStrongBrush"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex
            };
            var captured = color.Value;
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                (_hue, _sat, _val) = ToHsv(captured);
                SyncAll();
            };
            Swatches.Items.Add(swatch);
        }
    }

    /// <summary>Six hues x four shades, generated instead of listing 24 constants.</summary>
    private static IEnumerable<string> ExtendedPalette()
    {
        double[] hues = [0, 25, 45, 95, 160, 195, 220, 265, 300, 330];
        (double s, double v)[] shades = [(0.72, 0.98), (0.85, 0.78), (0.45, 0.95), (0.60, 0.55)];

        foreach (var h in hues)
            foreach (var (s, v) in shades)
                yield return ToHex(FromHsv(h, s, v));
    }

    // ── interaction ─────────────────────────────────────────────────────────────

    private void OnShadeDown(object sender, MouseButtonEventArgs e)
    {
        _draggingShade = true;
        ShadeCanvas.CaptureMouse();
        UpdateShade(e.GetPosition(ShadeCanvas));
    }

    private void OnShadeMove(object sender, MouseEventArgs e)
    {
        if (_draggingShade) UpdateShade(e.GetPosition(ShadeCanvas));
    }

    private void OnShadeUp(object sender, MouseButtonEventArgs e)
    {
        _draggingShade = false;
        ShadeCanvas.ReleaseMouseCapture();
    }

    private void UpdateShade(Point p)
    {
        var w = Math.Max(1, ShadeCanvas.ActualWidth);
        var h = Math.Max(1, ShadeCanvas.ActualHeight);
        _sat = Math.Clamp(p.X / w, 0, 1);
        _val = Math.Clamp(1 - p.Y / h, 0, 1);
        SyncAll();
    }

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        HueCanvas.CaptureMouse();
        UpdateHue(e.GetPosition(HueCanvas));
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (_draggingHue) UpdateHue(e.GetPosition(HueCanvas));
    }

    private void OnHueUp(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = false;
        HueCanvas.ReleaseMouseCapture();
    }

    private void UpdateHue(Point p)
    {
        var h = Math.Max(1, HueCanvas.ActualHeight);
        _hue = Math.Clamp(p.Y / h, 0, 1) * 360;
        SyncAll();
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (ParseHex(HexBox.Text) is not { } color) return;   // ignore partial input until it parses
        (_hue, _sat, _val) = ToHsv(color);
        SyncAll(skipHexBox: true);
    }

    /// <summary>Pushes the current HSV to every control: the square's hue backdrop, both thumbs,
    /// the preview and the hex box.</summary>
    private void SyncAll(bool skipHexBox = false)
    {
        _syncing = true;
        try
        {
            var color = FromHsv(_hue, _sat, _val);

            HueFill.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));
            Preview.Background = new SolidColorBrush(color);
            if (!skipHexBox) HexBox.Text = ToHex(color);

            // Thumbs sit on top of the value they point at, so both are offset by half their size.
            var w = ShadeCanvas.ActualWidth;
            var h = ShadeCanvas.ActualHeight;
            if (w > 0 && h > 0)
            {
                Canvas.SetLeft(ShadeThumb, _sat * w - ShadeThumb.Width / 2);
                Canvas.SetTop(ShadeThumb, (1 - _val) * h - ShadeThumb.Height / 2);
            }
            if (HueCanvas.ActualHeight > 0)
                Canvas.SetTop(HueThumb, _hue / 360 * HueCanvas.ActualHeight - HueThumb.Height / 2);
        }
        finally { _syncing = false; }
    }

    private void OnAccept(object sender, RoutedEventArgs e) =>
        _tcs.TrySetResult(new ColorChoice(ToHex(FromHsv(_hue, _sat, _val))));

    private void OnClear(object sender, RoutedEventArgs e) => _tcs.TrySetResult(new ColorChoice(null));

    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { OnAccept(this, e); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    // ── colour maths ────────────────────────────────────────────────────────────

    public static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        if (s.Length != 7) return null;
        try
        {
            return Color.FromRgb(
                byte.Parse(s.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;

        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;

        return (h, max <= 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
