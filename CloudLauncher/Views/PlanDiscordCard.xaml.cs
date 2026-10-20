using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The planning page's "Show in Discord": the channels a pack's boards are posted to, and linking a
/// new one, through a pasted channel webhook or (when the server has the app) a <c>/plan link</c>
/// code.
/// </summary>
/// <remarks>The server does the posting and keeps the messages current (<c>DiscordPlanPublisher</c>);
/// this card only manages the links. Shown with <c>MainWindow.ShowCardAsync</c>.</remarks>
public partial class PlanDiscordCard : UserControl
{
    private readonly TaskCompletionSource _done = new();
    private readonly Guid _packId;
    private readonly string _boardId;
    private PackDiscordInfo? _info;
    private bool _busy;

    public PlanDiscordCard(Guid packId, string boardId, string boardName)
    {
        InitializeComponent();
        _packId = packId;
        _boardId = boardId;
        OnlyBoardBox.Content = $"Only this board ({boardName})";
        Loaded += async (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            await LoadAsync();
        };
    }

    public Task Completion => _done.Task;

    public void Cancel() => _done.TrySetResult();

    private static ApiClient Api => App.State.Api;

    private async Task LoadAsync()
    {
        try
        {
            _info = await Api.GetPackDiscordAsync(_packId);
        }
        catch (Exception ex) when (ex is ApiException or OfflineException or HttpRequestException)
        {
            EmptyLabel.Text = "";
            Status(Describe(ex));
            return;
        }
        if (_info is null)
        {
            EmptyLabel.Text = "";
            Status("This server can't post boards to Discord yet.");
            WebhookBox.IsEnabled = LinkButton.IsEnabled = OnlyBoardBox.IsEnabled = false;
            return;
        }
        BotPanel.Visibility = _info.BotAvailable ? Visibility.Visible : Visibility.Collapsed;
        RenderLinks();
    }

    private void RenderLinks()
    {
        var links = _info?.Links ?? [];
        LinkList.Children.Clear();
        foreach (var link in links) LinkList.Children.Add(Row(link));
        LinkBox.Visibility = links.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabel.Text = links.Count > 0 ? "" : "Not shown in any channel yet.";
        EmptyLabel.Visibility = links.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement Row(PackDiscordLinkDto link)
    {
        var row = new DockPanel { Margin = new Thickness(8, 6, 6, 6) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (link.ViewUrl is { } view)
            buttons.Children.Add(LinkButtonFor("Open", "Open the live boards in your browser", () => SafeLaunch.OpenUrl(view)));
        if (link.State != "pending")
            buttons.Children.Add(LinkButtonFor("Redraw", "Post every board again now", () => _ = RefreshAsync(link)));
        buttons.Children.Add(LinkButtonFor("Remove", "Stop showing the boards there and take their messages down",
            () => _ = RemoveAsync(link)));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = link.Label is { Length: > 0 } label ? label
            : link.Mode == "bot" ? "Discord channel" : "Discord webhook";
        if (link.BoardName is { } board) title += "  ·  " + board;
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = StateLine(link),
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource(link.State == "broken" ? "WarningBrush" : "TextSecondaryBrush")
        });
        row.Children.Add(text);
        return row;
    }

    private static string StateLine(PackDiscordLinkDto link) => link.State switch
    {
        "pending" => $"Waiting for /plan link code:{link.Code} in a channel"
                     + (link.CodeExpiresAt is { } at ? $" (until {at.ToLocalTime():t})" : ""),
        "broken" => "Stopped: " + (link.LastError ?? "the channel can't be posted to any more."),
        _ when link.PublishedAt is null => "Posting the boards...",
        _ when link.LastError is { } error => error,
        _ => link.BoardId is null ? "Every board, kept up to date" : "Kept up to date"
    };

    private Button LinkButtonFor(string text, string tip, Action click)
    {
        var button = new Button
        {
            Content = text, Style = (Style)FindResource("LinkButton"), Margin = new Thickness(10, 0, 0, 0),
            ToolTip = tip, VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => { if (!_busy) click(); };
        return button;
    }

    private async void OnLinkWebhook(object sender, RoutedEventArgs e)
    {
        var url = WebhookBox.Text.Trim();
        if (url.Length == 0)
        {
            Status("Paste the channel's webhook URL first.");
            WebhookBox.Focus();
            return;
        }
        await RunAsync(async () =>
        {
            await Api.CreatePackDiscordLinkAsync(_packId, url, OnlyBoardBox.IsChecked == true ? _boardId : null);
            WebhookBox.Text = "";
            Status("Linked. The boards show up in the channel in a few seconds.");
        });
    }

    private async void OnGetCode(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var link = await Api.CreatePackDiscordLinkAsync(_packId, null, OnlyBoardBox.IsChecked == true ? _boardId : null);
            CommandText.Text = $"/plan link code:{link.Code}";
            CodePanel.Visibility = Visibility.Visible;
            Status("Type the command in the channel within 30 minutes. It needs the Manage Channels permission.");
        });
    }

    private void OnInviteBot(object sender, RoutedEventArgs e)
    {
        if (_info?.BotInviteUrl is { } url && !SafeLaunch.OpenUrl(url)) Status("That link can't be opened.");
    }

    private void OnCopyCommand(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CommandText.Text);
            Status("Copied.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status("The clipboard is busy; try again.");
        }
    }

    private Task RefreshAsync(PackDiscordLinkDto link) => RunAsync(async () =>
    {
        await Api.RefreshPackDiscordLinkAsync(_packId, link.Id);
        Status("Redrawing the boards there now.");
    });

    private async Task RemoveAsync(PackDiscordLinkDto link)
    {
        if (Window.GetWindow(this) is MainWindow main
            && !await main.ShowConfirmAsync("Stop showing the boards there?",
                "Their messages are taken down from the channel. You can link it again any time.",
                "Remove", danger: true))
            return;
        await RunAsync(async () =>
        {
            await Api.DeletePackDiscordLinkAsync(_packId, link.Id);
            Status("Removed.");
        });
    }

    /// <summary>One change at a time: runs it, then shows the list as the server has it now.</summary>
    private async Task RunAsync(Func<Task> change)
    {
        if (_busy) return;
        _busy = true;
        LinkButton.IsEnabled = CodeButton.IsEnabled = false;
        try
        {
            await change();
            _info = await Api.GetPackDiscordAsync(_packId) ?? _info;
            RenderLinks();
        }
        catch (Exception ex) when (ex is ApiException or OfflineException or HttpRequestException)
        {
            Status(Describe(ex));
        }
        finally
        {
            _busy = false;
            LinkButton.IsEnabled = CodeButton.IsEnabled = true;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        ApiException api => ApiClient.ServerSentence(api) ?? api.Message,
        OfflineException off => off.Message,
        _ => "Couldn't reach the server."
    };

    private void Status(string text) => StatusLabel.Text = text;

    private void OnClose(object sender, RoutedEventArgs e) => Cancel();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter && WebhookBox.IsKeyboardFocused) { OnLinkWebhook(this, new RoutedEventArgs()); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
