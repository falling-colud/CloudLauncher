using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// A hub tab that can reload itself, so the page's Refresh button doesn't have to rebuild it.
/// </summary>
/// <remarks>
/// Optional; tabs without it are rebuilt. Implement it on tabs with state worth keeping, such as a
/// typed filter, a scroll position or a selection.
/// </remarks>
public interface ISharingHubTab
{
    Task RefreshAsync();

    /// <summary>The page was shown again. Tabs that support a quiet refresh (keeping their rows on
    /// screen) do one here; the default is a normal refresh.</summary>
    Task ReopenAsync() => RefreshAsync();
}

/// <summary>The tabs of the sharing hub, in the order they appear.</summary>
public enum SharingTab
{
    /// <summary>Everything shared, both ways and of every kind.</summary>
    Overview = 0,
    People = 1,
    Activity = 2,
    /// <summary>What your content costs on the server. Usage on this PC is the sidebar's Storage
    /// page.</summary>
    CloudStorage = 3
}

/// <summary>
/// Top-level hub for everything the launcher shares: which instances are hosted, who they are
/// shared with, what has been published to the library, and what changed lately.
/// </summary>
/// <remarks>
/// Each tab is an empty host with a placeholder. Its content is built the first time it is shown
/// (<see cref="TabFirstShown"/>, raised once per tab, including the one selected at load) and set
/// with <see cref="SetTabContent"/>, so opening the page doesn't make every tab's server calls.
/// </remarks>
public partial class SharingHubView : Page, IRefreshablePage, IReusablePage
{
    private readonly MainWindow _shell;

    // Sized from the enum so the per-tab arrays grow with it.
    private static readonly int TabCount = Enum.GetValues<SharingTab>().Length;

    // Tabs that have had TabFirstShown raised, whether or not they got any content.
    private readonly bool[] _announced = new bool[TabCount];

    // The panels built so far, so Refresh can ask a tab to reload itself instead of rebuilding it
    // and losing its filter.
    private readonly UIElement?[] _panels = new UIElement?[TabCount];

    // TabControl raises SelectionChanged while InitializeComponent is still running, before any of
    // the fields below exist. Nothing may act on a tab change until the constructor has finished.
    private bool _ready;

    /// <summary>
    /// Raised the first time each tab is shown; see the class remarks.
    /// </summary>
    public event Action<SharingTab>? TabFirstShown;

    public SharingHubView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;

        foreach (var tab in Enum.GetValues<SharingTab>())
            SeedPlaceholder(HostFor(tab));

        // Subscribed before anything can raise it: the Loaded handler announces the tab the page
        // opens on, and that has to build its panel like any other first show.
        TabFirstShown += OnTabFirstShown;

        _ready = true;

        Loaded += (_, _) =>
        {
            // The tab selected at load raises no change of its own, so announce it here. The page is reused
            // (IReusablePage), so on a reopen the tab is already announced and does a quiet refresh
            // instead.
            if (!_announced[CurrentTabIndex]) AnnounceCurrentTab();
            else if (_panels[CurrentTabIndex] is ISharingHubTab live) _ = live.ReopenAsync();
            else _ = RefreshAsync();

            App.State.ConnectivityChanged += OnConnectivityChanged;
            UpdateConnectionNote();
        };
        Unloaded += (_, _) => App.State.ConnectivityChanged -= OnConnectivityChanged;
    }

    // ── tab hosts ────────────────────────────────────────────────────────────

    /// <summary>The empty <see cref="Grid"/> that is a tab's whole body.</summary>
    public Grid HostFor(SharingTab tab) => tab switch
    {
        SharingTab.Overview  => OverviewHost,
        SharingTab.People    => PeopleHost,
        SharingTab.Activity  => ActivityHost,
        SharingTab.CloudStorage => CloudStorageHost,
        _ => OverviewHost
    };

    /// <summary>Replaces a tab's placeholder with the real thing.</summary>
    public void SetTabContent(SharingTab tab, UIElement content)
    {
        var host = HostFor(tab);
        host.Children.Clear();
        host.Children.Add(content);
        _panels[(int)tab] = content;
    }

    /// <summary>
    /// The aggregator all tabs share: one fan-out, one cache, one set of mutations.
    /// </summary>
    /// <remarks>
    /// Public so panels built elsewhere use it too instead of each making the same calls.
    /// </remarks>
    public SharingHubService Sharing { get; } = new();

    private void SeedPlaceholder(Grid host)
    {
        host.Children.Add(new TextBlock
        {
            Text = "Open this tab to load it.",
            Style = (Style)FindResource("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    // ── building the tabs ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a tab's panel the first time it is shown, once for the life of the page.
    /// </summary>
    /// <remarks>
    /// A panel that throws in its constructor only breaks its own tab, not the page.
    /// </remarks>
    private void OnTabFirstShown(SharingTab tab)
    {
        UIElement panel;
        try
        {
            panel = BuildPanelFor(tab);
        }
        catch (Exception ex)
        {
            AppLog.LogError($"sharing.{tab}", ex);
            panel = Note("That tab could not be opened. The error is in the launcher log.");
        }
        SetTabContent(tab, panel);
    }

    /// <summary>
    /// Maps each tab to its control.
    /// </summary>
    /// <remarks>
    /// The People and Activity panels are resolved by name at run time; until one exists, its tab
    /// shows a note instead of the placeholder.
    /// </remarks>
    private UIElement BuildPanelFor(SharingTab tab) => tab switch
    {
        SharingTab.Overview => new SharingOverviewPanel(_shell, Sharing),
        SharingTab.People => TryBuild("SharingPeoplePanel", "SharingTeamsPanel", "SharingPeopleTeamsPanel")
                             ?? Note("People and teams are not on this page yet."),
        SharingTab.Activity => TryBuild("SharingActivityPanel", "ActivityFeedPanel")
                             ?? Note("The activity feed is not on this page yet."),
        // Constructed directly, since a late-bound name would only hide a typo until run time. It takes
        // the shell because its rows open the instance, mod or resource pack they report on.
        SharingTab.CloudStorage => new SharingCloudStoragePanel(_shell),
        _ => Note("")
    };

    /// <summary>
    /// Constructs a panel by type name, trying the constructor signatures a sharing tab is likely to
    /// have. Returns null when no type exists or none of them match.
    /// </summary>
    /// <remarks>
    /// Late-bound so this file builds without panels that don't exist yet.
    /// </remarks>
    private UIElement? TryBuild(params string[] typeNames)
    {
        var assembly = typeof(SharingHubView).Assembly;
        foreach (var name in typeNames)
        {
            var type = assembly.GetType("CloudLauncher.Views." + name);
            if (type is null) continue;

            object?[][] shapes =
            [
                [_shell, Sharing],
                [_shell, this],
                [this],
                [_shell],
                []
            ];

            foreach (var args in shapes)
            {
                try
                {
                    if (Activator.CreateInstance(type, args) is UIElement built)
                    {
                        AppLog.Log("sharing", $"Tab panel {name} built.");
                        return built;
                    }
                }
                catch (MissingMethodException) { /* wrong constructor, try the next one */ }
                catch (Exception ex)
                {
                    // It matched and then threw: log it rather than trying other constructors.
                    AppLog.LogError("sharing.panel." + name, ex);
                    return null;
                }
            }
        }
        return null;
    }

    /// <summary>A centred, wrapped Muted line for a tab with nothing else to show.</summary>
    private TextBlock Note(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("Muted"),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        MaxWidth = 420,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ── tab lazy-load ────────────────────────────────────────────────────────

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // An inner ComboBox's selection change bubbles up as this same event; without the check it
        // would re-run the tab load every time someone picked a sort order.
        if (e.OriginalSource is not TabControl) return;
        if (!_ready) return;
        AnnounceCurrentTab();
    }

    private void AnnounceCurrentTab()
    {
        var tab = (SharingTab)CurrentTabIndex;
        StatusLabel.Text = TabName(tab);

        if (_announced[(int)tab]) return;
        _announced[(int)tab] = true;
        TabFirstShown?.Invoke(tab);
    }

    private static string TabName(SharingTab tab) => tab switch
    {
        SharingTab.Overview  => "Overview",
        SharingTab.People    => "People & teams",
        SharingTab.Activity  => "Activity",
        SharingTab.CloudStorage => "Cloud storage",
        _ => "Sharing"
    };

    // ── connectivity ─────────────────────────────────────────────────────────

    private void OnConnectivityChanged() => UpdateConnectionNote();

    private void UpdateConnectionNote() =>
        ConnectionNote.Visibility = App.State.IsOffline ? Visibility.Visible : Visibility.Collapsed;

    // ── header actions ───────────────────────────────────────────────────────

    /// <summary>
    /// The Share... menu: an instance, or something this account hosts.
    /// </summary>
    /// <remarks>
    /// Hosted items come from <see cref="SharingSnapshot.HostedMine"/>, which includes unshared ones
    /// the Overview doesn't list. Only the first page of each kind, like the Overview.
    /// </remarks>
    private void OnShare(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ShareButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        var instance = MenuRow("An instance...", "");
        instance.ToolTip = "Pick one of your instances and go straight to its Share & sync tab";
        instance.Click += (_, _) => _ = ShareInstanceAsync();
        menu.Items.Add(instance);

        var hosted = MenuRow("Something you host", "");
        var last = Sharing.Last;
        if (last is null)
            hosted.Items.Add(Disabled("Still reading what you host - try again in a moment"));
        else if (last.HostedMine.Count == 0)
            hosted.Items.Add(Disabled("Nothing hosted yet. Host a mod, world, resource pack or shader pack from its own page first."));
        else
            foreach (var row in last.HostedMine
                         .OrderBy(r => r.Kind, StringComparer.CurrentCulture)
                         .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                         .Take(60))
            {
                var item = MenuRow(row.Name, row.TypeGlyph);
                item.InputGestureText = row.Kind;
                var picked = row;
                item.Click += (_, _) => _ = ShareHostedAsync(picked);
                hosted.Items.Add(item);
            }
        menu.Items.Add(hosted);
        menu.IsOpen = true;
    }

    /// <summary>A menu row whose name is plain text: a Header string would turn the first
    /// underscore in a mod's name into an access key and swallow it.</summary>
    private MenuItem MenuRow(string text, string glyph) => new()
    {
        Header = new TextBlock { Text = text },
        Icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"),
            FontSize = 12
        }
    };

    private static MenuItem Disabled(string text) => new()
    {
        Header = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
        IsEnabled = false
    };

    /// <summary>
    /// Picks an instance and opens its Share &amp; sync tab (<see cref="PackSharePanel"/>).
    /// </summary>
    /// <remarks>
    /// Falls back to the instance's own page, which can fetch the details this hub lacks and has the
    /// same tab.
    /// </remarks>
    private async Task ShareInstanceAsync()
    {
        // Held down for the life of the dialog: the button is still under the pointer behind the
        // card, and a second click would stack a second picker on top of the first.
        ShareButton.IsEnabled = false;
        try
        {
            if (await ShareInstanceDialog.ShowAsync(_shell, Sharing) is not { } pick) return;

            if (pick.Detail is { } detail)
                FileManagementView.OpenOn(_shell, detail, FileManagementTab.Share);
            else
                _shell.OpenPackDetail(pick.Pack.Id, pick.Pack.Name);
        }
        catch (Exception ex)
        {
            // Nothing awaits this, so an exception would otherwise vanish.
            AppLog.LogError("sharing.share", ex);
            StatusLabel.Text = "That could not be opened. The error is in the launcher log.";
        }
        finally { ShareButton.IsEnabled = true; }
    }

    /// <summary>Opens the access dialog for something this account hosts, then refreshes the
    /// Overview if it may have changed.</summary>
    private async Task ShareHostedAsync(SharingRow row)
    {
        try
        {
            if (await SharingActions.ManageAccessAsync(_shell, row, Sharing.Last))
                await RefreshOverviewAsync();
        }
        catch (Exception ex)
        {
            AppLog.LogError("sharing.share", ex);
            StatusLabel.Text = $"Who can reach '{row.Name}' could not be opened: {SharingHubService.Explain(ex)}";
        }
    }

    // ── redeeming a link ─────────────────────────────────────────────────────

    /// <summary>
    /// The receiving end of a share link: paste it, see who is sharing what, accept. Team invite codes
    /// work here too.
    /// </summary>
    /// <remarks>
    /// Instance and bundle tokens are previewed before accepting. A 404 means the token is neither, so
    /// it is offered as a team code, which has no preview.
    /// </remarks>
    private async void OnRedeem(object sender, RoutedEventArgs e)
    {
        RedeemButton.IsEnabled = false;
        try
        {
            var pasted = await _shell.PromptAsync("Redeem a link", "Share link or invite code");
            if (string.IsNullOrWhiteSpace(pasted)) return;
            if (SharingHubService.ParseInviteToken(pasted) is not { } token)
            {
                StatusLabel.Text = "That doesn't look like a CloudLauncher share link or invite code.";
                return;
            }

            InvitationPreview? preview = null;
            try { preview = await Sharing.PreviewInvitationAsync(token, CancellationToken.None); }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
            {
                StatusLabel.Text = "That invitation was sent to somebody else by name, so it only works for them.";
                return;
            }

            if (preview is null)
            {
                var join = await AppDialog.ConfirmAsync(_shell, "Join a team",
                    "That isn't a link to an instance or to anything hosted. If it was sent to you as a "
                    + "team invite code, you can join the team with it.",
                    "Join the team", "Cancel");
                if (!join) return;
                var team = await Sharing.RedeemTeamCodeAsync(token, CancellationToken.None);
                StatusLabel.Text = $"You joined {team.Name}. What it shares shows up on the Overview.";
                await _shell.RefreshPackTeamFoldersAsync();
                await RefreshOverviewAsync();
                return;
            }

            if (preview.Revoked)
            {
                StatusLabel.Text = $"{preview.InvitedByUsername} took that link to '{preview.SubjectName}' back. Ask them for a new one.";
                return;
            }
            if (preview.Expired)
            {
                StatusLabel.Text = $"That link to '{preview.SubjectName}' has expired. Ask {preview.InvitedByUsername} for a new one.";
                return;
            }

            var message = preview.Message is { Length: > 0 } m ? $"\n\n'{m}'" : "";
            var again = preview.AlreadyAccepted
                ? "\n\nYou have used this invitation before; accepting again changes nothing."
                : "";
            var ok = await AppDialog.ConfirmAsync(_shell, "Redeem this link",
                $"{preview.InvitedByUsername} is sharing '{preview.SubjectName}' with you "
                + $"({SharingHubService.DescribePermissions(preview.Permissions)}).{message}{again}",
                "Accept", "Cancel");
            if (!ok) return;

            var redeemed = await Sharing.RedeemAsync(token, CancellationToken.None);
            if (redeemed.Family == SharedFamily.Instance) _shell.RefreshPacks();
            StatusLabel.Text = redeemed.Family == SharedFamily.Instance
                ? $"'{redeemed.Name}' is in your instances now. Download it from its page to play it."
                : $"You can reach '{redeemed.Name}' now.";
            await RefreshOverviewAsync();
            SharingActions.Open(_shell, redeemed.Family, redeemed.Id, redeemed.Name);
        }
        catch (Exception ex)
        {
            AppLog.LogError("sharing.redeem", ex);
            StatusLabel.Text = "That link could not be redeemed: " + SharingHubService.Explain(ex) + ".";
        }
        finally { RedeemButton.IsEnabled = true; }
    }

    /// <summary>Shows the Overview and reloads it after something it lists has changed.</summary>
    private Task RefreshOverviewAsync()
    {
        if (Tabs.SelectedIndex != (int)SharingTab.Overview)
            Tabs.SelectedIndex = (int)SharingTab.Overview; // announces it, which builds and loads it
        return _panels[(int)SharingTab.Overview] is ISharingHubTab overview
            ? overview.RefreshAsync()
            : Task.CompletedTask;
    }

    /// <summary>
    /// Reloads the tab on screen, like the offline banner's Retry: the panel's own refresh when it has
    /// one, otherwise a rebuild, which loses its filter and scroll position.
    /// </summary>
    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    /// <summary>The selected tab index, clamped; the index into the per-tab arrays.</summary>
    private int CurrentTabIndex => Math.Clamp(Tabs.SelectedIndex, 0, _announced.Length - 1);

    public Task RefreshAsync()
    {
        var tab = (SharingTab)CurrentTabIndex;
        AppLog.Log("sharing", $"Refresh requested for the {TabName(tab)} tab.");
        UpdateConnectionNote();

        if (_panels[(int)tab] is ISharingHubTab live) return live.RefreshAsync();

        _announced[(int)tab] = false;
        AnnounceCurrentTab();
        return Task.CompletedTask;
    }
}
