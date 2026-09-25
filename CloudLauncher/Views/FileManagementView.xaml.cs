using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>The tabs of the file-management page, in the order they appear.</summary>
public enum FileManagementTab
{
    Files = 0,
    Editor = 1,
    Share = 2,
    Hosting = 3,
    Cleanup = 4,
    Compare = 5
}

/// <summary>Everything you can do to one instance's files: browse, edit, share and sync, host,
/// clean up, and compare with another instance.</summary>
/// <remarks>Opened via <see cref="MainWindow.OpenFileManagementForPack"/> and listed in the shell's
/// <c>forceCover</c> set, because a tree beside a file pane doesn't fit in the split column. Each
/// tab's panel is built the first time the tab is shown (<see cref="TabFirstShown"/>,
/// <see cref="BuildPanelFor"/>).</remarks>
public partial class FileManagementView : Page, ISidePanelBackHandler
{
    private readonly MainWindow _shell;

    // Which tabs have had TabFirstShown raised for them.
    private readonly bool[] _announced = new bool[6];

    // The panels this page built, so Back can be offered to the one on screen.
    private readonly UIElement?[] _panels = new UIElement?[6];

    // TabControl raises SelectionChanged during InitializeComponent, before these fields exist.
    private bool _ready;

    /// <summary>The tab <see cref="OpenOn"/> wants the next page to start on.</summary>
    /// <remarks>The shell's OpenFileManagementForPack builds the page itself and returns nothing,
    /// so the wanted tab is parked here for that one constructor call and cleared in
    /// <see cref="OpenOn"/>'s finally. Both run synchronously on the UI thread.</remarks>
    private static FileManagementTab? _pendingTab;

    /// <summary>The instance whose files this page manages.</summary>
    public PackDetail Pack { get; }

    /// <summary>Raised the first time each tab is shown, including the tab selected at load.</summary>
    public event Action<FileManagementTab>? TabFirstShown;

    public FileManagementView(MainWindow shell, PackDetail pack)
    {
        InitializeComponent();
        _shell = shell;
        Pack = pack;
        PackNameLabel.Text = pack.Name;

        foreach (var tab in Enum.GetValues<FileManagementTab>())
            SeedPlaceholder(HostFor(tab));

        // Subscribed before anything can raise it: the Loaded handler below announces the starting
        // tab, which needs its panel built like any other.
        TabFirstShown += OnTabFirstShown;

        if (_pendingTab is { } wanted)
            Tabs.SelectedIndex = (int)wanted;

        _ready = true;
        Loaded += (_, _) => AnnounceCurrentTab();
    }

    /// <summary>The shell that opened this page, for tabs that need to navigate out of it.</summary>
    public MainWindow Shell => _shell;

    /// <summary>Opens the file-management page for an instance on a particular tab, e.g. from a
    /// "server is running" chip or a "clean this up" prompt.</summary>
    public static void OpenOn(MainWindow shell, PackDetail pack, FileManagementTab tab)
    {
        _pendingTab = tab;
        try { shell.OpenFileManagementForPack(pack); }
        finally { _pendingTab = null; }
    }

    /// <summary>Switches to a tab, building it if this is its first show.</summary>
    public void ShowTab(FileManagementTab tab)
    {
        Tabs.SelectedIndex = (int)tab;
        AnnounceCurrentTab();
    }

    // ── tab hosts ────────────────────────────────────────────────────────────

    /// <summary>The empty <see cref="Grid"/> that is a tab's whole body.</summary>
    public Grid HostFor(FileManagementTab tab) => tab switch
    {
        FileManagementTab.Files   => FilesHost,
        FileManagementTab.Editor  => EditorHost,
        FileManagementTab.Share   => ShareHost,
        FileManagementTab.Hosting => HostingHost,
        FileManagementTab.Cleanup => CleanupHost,
        FileManagementTab.Compare => CompareHost,
        _ => FilesHost
    };

    /// <summary>Replaces a tab's placeholder with the real thing.</summary>
    public void SetTabContent(FileManagementTab tab, UIElement content)
    {
        var host = HostFor(tab);
        host.Children.Clear();
        host.Children.Add(content);
        _panels[(int)tab] = content;
    }

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
        var tab = (FileManagementTab)Math.Clamp(Tabs.SelectedIndex, 0, _announced.Length - 1);
        if (_announced[(int)tab]) return;
        _announced[(int)tab] = true;
        TabFirstShown?.Invoke(tab);
    }

    /// <summary>Builds the tab's panel the first time it is shown. Panels are built once per page
    /// and handle their own refresh; rebuilding one would lose the folder the user had navigated
    /// into.</summary>
    private void OnTabFirstShown(FileManagementTab tab)
    {
        UIElement panel;
        try
        {
            panel = BuildPanelFor(tab);
        }
        catch (Exception ex)
        {
            // A panel that throws in its constructor shouldn't take the page down; the other tabs
            // still work.
            Services.AppLog.LogError($"file-management.{tab}", ex);
            panel = Note("That tab could not be opened. The error is in the launcher log.");
        }
        SetTabContent(tab, panel);
    }

    /// <summary>Maps each tab to its panel.</summary>
    private UIElement BuildPanelFor(FileManagementTab tab)
    {
        switch (tab)
        {
            case FileManagementTab.Files:
                // Two steps: the panel's constructor is parameterless so it can be used in XAML,
                // and Load points it at an instance.
                var browser = new FileBrowserPanel();
                browser.Load(_shell, Pack);
                return browser;

            case FileManagementTab.Editor:
                return new FileEditorPanel(Pack.Id, Pack.Name);

            case FileManagementTab.Share:
                var share = new PackSharePanel();
                share.Load(_shell, Pack);
                return share;

            case FileManagementTab.Hosting:
                return new ServerHostingPanel(this);

            case FileManagementTab.Cleanup:
                return new FileCleanupPanel(_shell, Pack);

            case FileManagementTab.Compare:
                return new FileComparePanel(_shell, Pack);

            default:
                return Note("");
        }
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

    // ── side-panel Back ──────────────────────────────────────────────────────

    /// <summary>Offers Back to the panel on screen first, so a tab with its own drill-down (a
    /// folder, a file open in the editor) can unwind that instead of closing the page. If it
    /// doesn't handle it, the side panel pops.</summary>
    public bool TryHandleBack() =>
        _panels[Math.Clamp(Tabs.SelectedIndex, 0, _panels.Length - 1)]
            is ISidePanelBackHandler handler && handler.TryHandleBack();
}
