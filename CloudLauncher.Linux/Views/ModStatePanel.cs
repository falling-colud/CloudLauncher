using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CloudLauncher.Views;

/// <summary>
/// What a list area of the mods pages says when it has no rows to show: loading, empty, nothing
/// matched, an error or offline (the last two with Retry). Laid over the list; hidden while rows show.
/// The Windows launcher's PageStateView, in short.
/// </summary>
public sealed class ModStatePanel : UserControl
{
    private readonly TextBlock _glyph = new() { FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _title = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly TextBlock _body = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Width = 160, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _retry = new() { Content = "Retry", HorizontalAlignment = HorizontalAlignment.Center };

    /// <summary>Retry was clicked on an error or offline state.</summary>
    public event Action? RetryRequested;

    public ModStatePanel()
    {
        _glyph.Classes.Add("icon");
        _glyph.Bind(TextBlock.ForegroundProperty, _glyph.GetResourceObservable("TextTertiaryBrush"));
        _title.Classes.Add("h2");
        _body.Classes.Add("muted");
        _retry.Click += (_, _) => RetryRequested?.Invoke();
        Content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 380,
            Margin = new(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _glyph, _bar, _title, _body, _retry }
        };
        IsVisible = false;
    }

    /// <summary>A search or scan is under way and there is nothing to show yet.</summary>
    public void Loading(string line)
    {
        Set(null, null, line, bar: true, retry: false);
    }

    /// <summary>Nothing there (or nothing matched).</summary>
    public void Empty(string title, string body, string glyph = "") => Set(glyph, title, body, bar: false, retry: false);

    /// <summary>Something failed; Retry tries again.</summary>
    public void Error(string title, string body) => Set("", title, body, bar: false, retry: true);

    /// <summary>The network is out; Retry tries again.</summary>
    public void Offline(string title, string body) => Set("", title, body, bar: false, retry: true);

    /// <summary>Rows are showing.</summary>
    public void Hide() => IsVisible = false;

    private void Set(string? glyph, string? title, string? body, bool bar, bool retry)
    {
        _glyph.Text = glyph ?? "";
        _glyph.IsVisible = glyph is not null;
        _title.Text = title ?? "";
        _title.IsVisible = !string.IsNullOrEmpty(title);
        _body.Text = body ?? "";
        _body.IsVisible = !string.IsNullOrEmpty(body);
        _bar.IsVisible = bar;
        _retry.IsVisible = retry;
        IsVisible = true;
    }
}
