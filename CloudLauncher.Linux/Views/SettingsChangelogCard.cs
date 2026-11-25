using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>What's New: every release the server has published, newest first, with the one you run
/// marked. The Windows launcher's ChangelogDialog.</summary>
public static class SettingsChangelogCard
{
    public static async Task ShowAsync(MainWindow shell)
    {
        var done = new TaskCompletionSource<bool>();
        using var cancel = new CancellationTokenSource();

        var title = new TextBlock { Text = "What's New" };
        title.Classes.Add("h2");
        var sub = new TextBlock { Text = $"You are running {AppVersion.CurrentString}.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 14) };
        sub.Classes.Add("muted");

        var list = new StackPanel { Spacing = 8, Margin = new(10) };
        var message = new TextBlock
        {
            Text = "Loading...",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 380,
            Margin = new(16, 40)
        };
        message.Classes.Add("muted");
        var well = new Border
        {
            Height = 420,
            Child = new Panel
            {
                Children =
                {
                    new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                    message
                }
            }
        };
        well.Classes.Add("well");

        var footer = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        footer.Classes.Add("caption");
        var close = new Button { Content = "Close", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Classes.Add("primary");
        close.Click += (_, _) => done.TrySetResult(true);
        var bottom = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 16, 0, 0) };
        bottom.Children.Add(footer);
        Grid.SetColumn(close, 1);
        bottom.Children.Add(close);

        var card = new Border
        {
            Width = 640,
            Child = new StackPanel { Children = { title, sub, well, bottom } }
        };
        card.Classes.Add("dialog");

        _ = LoadAsync();
        await shell.ShowCardAsync(card, done.Task, () => done.TrySetResult(true));
        cancel.Cancel();

        async Task LoadAsync()
        {
            try
            {
                var (releases, linux) = await LinuxUpdater.GetReleasesAsync(cancel.Token);
                var installed = AppVersion.Rank(AppVersion.Current);
                var rows = releases
                    .Select(r => (Info: r, Rank: AppVersion.Rank(Version.TryParse(r.Version, out var v) ? v : new Version(0, 0))))
                    .OrderByDescending(r => r.Rank)
                    .ThenByDescending(r => r.Info.ReleasedAt)
                    .ToList();
                if (rows.Count == 0)
                {
                    message.Text = "The server has not published any release notes yet.";
                    return;
                }
                message.IsVisible = false;
                foreach (var (info, rank) in rows)
                    list.Children.Add(Row(info, isInstalled: rank == installed, isNewer: linux && rank > installed));

                sub.Text = $"{rows.Count} release{(rows.Count == 1 ? "" : "s")}. You are running {AppVersion.CurrentString}.";
                var newer = linux ? rows.Count(r => r.Rank > installed) : 0;
                footer.Text = !linux
                    ? "These are the launcher's release notes; the Linux builds follow the same numbers."
                    : newer == 0
                        ? "You are on the latest version."
                        : $"{newer} newer release{(newer == 1 ? "" : "s")}: Check for Updates installs the newest.";
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex)
            {
                message.IsVisible = true;
                message.Text = "Could not load the release notes: " + LinuxUpdater.Describe(ex);
                AppLog.Log("update", "What's New: " + ex.Message);
            }
        }
    }

    private static Control Row(LauncherReleaseInfo info, bool isInstalled, bool isNewer)
    {
        var version = new TextBlock { Text = $"Version {info.Version}", VerticalAlignment = VerticalAlignment.Center };
        version.Classes.Add("h3");
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { version } };
        if (isInstalled) head.Children.Add(Pill("Installed", "accent"));
        else if (isNewer) head.Children.Add(Pill("Available", "success"));

        var date = new TextBlock
        {
            Text = info.ReleasedAt > DateTimeOffset.UnixEpoch ? TimeFormat.Date(info.ReleasedAt) : "",
            VerticalAlignment = VerticalAlignment.Center
        };
        date.Classes.Add("caption");
        var top = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 0, 0, 8) };
        top.Children.Add(head);
        Grid.SetColumn(date, 1);
        top.Children.Add(date);

        var notes = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(info.Notes) ? "No notes for this release." : info.Notes.Trim(),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19
        };
        notes.Classes.Add("muted");

        var row = new Border
        {
            Padding = new(14, 12),
            CornerRadius = new(4),
            BorderThickness = new(1),
            Child = new StackPanel { Children = { top, notes } }
        };
        row.Bind(Border.BackgroundProperty, row.GetResourceObservable("Surface2Brush"));
        row.Bind(Border.BorderBrushProperty, row.GetResourceObservable(isInstalled ? "AccentBrush" : "BorderSubtleBrush"));
        return row;
    }

    private static Border Pill(string text, string kind)
    {
        var pill = new Border { Child = new TextBlock { Text = text } };
        pill.Classes.Add("pill");
        pill.Classes.Add(kind);
        return pill;
    }
}
