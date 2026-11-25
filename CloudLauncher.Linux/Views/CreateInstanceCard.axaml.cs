using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// A new instance: name, summary, Minecraft version, loader and its version, and (signed in) who can
/// see it. The Windows launcher's CreatePackDialog as a card, with its calls: the instance is made
/// with <see cref="ApiClient.CreatePackAsync"/> (on this computer when signed out), its folder made,
/// and a Team or Public one published with server hosting on as a second step.
/// </summary>
/// <remarks>
/// Quilt is not offered: the shared services know only None, Fabric, Forge and NeoForge
/// (<see cref="LoaderKind"/>), and the launch can't install Quilt.
/// </remarks>
public partial class CreateInstanceCard : UserControl
{
    /// <summary>Where the Minecraft releases come from, newest first. The shots harness swaps in a fixed
    /// list so it never goes online.</summary>
    public static Func<CancellationToken, Task<List<string>>> MinecraftVersionSource { get; set; } =
        async ct => (await App.State.Versions.ListMinecraftVersionsAsync(ct: ct)).Select(v => v.Id).ToList();

    /// <summary>Where a loader's builds for a Minecraft version come from, newest first (empty when the
    /// loader publishes none for it).</summary>
    public static Func<LoaderKind, string, CancellationToken, Task<List<string>>> LoaderVersionSource { get; set; } =
        (loader, mc, ct) => App.State.Versions.ListLoaderVersionsAsync(loader, mc, ct);

    private readonly TaskCompletionSource<PackSummary?> _done = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _signedIn = App.State.Api.IsSignedIn;

    /// <summary>Set once the instance exists: a retry after a failed publish only publishes.</summary>
    private PackSummary? _created;
    private bool _creating;
    private bool _loaded;
    private int _loaderRequest;

    public CreateInstanceCard()
    {
        InitializeComponent();
        UpdateSummaryCounter();
        // Signed out, the instance is made on this computer, where nobody else can see it: there is
        // nothing to choose.
        VisibilityLabel.IsVisible = _signedIn;
        VisibilityBox.IsVisible = _signedIn;
        UpdateNote();

        SummaryBox.TextChanged += (_, _) => UpdateSummaryCounter();
        McVersionBox.SelectionChanged += async (_, _) => await RefreshLoaderVersionsAsync();
        LoaderBox.SelectionChanged += async (_, _) => await RefreshLoaderVersionsAsync();
        VisibilityBox.SelectionChanged += (_, _) => UpdateNote();
        EmptyBox.IsCheckedChanged += (_, _) => UpdateKind();
        KeyDown += OnKeyDown;
        AttachedToVisualTree += async (_, _) =>
        {
            Dispatcher.UIThread.Post(() => NameBox.Focus(), DispatcherPriority.Background);
            if (_loaded) return;
            _loaded = true;
            await LoadMinecraftVersionsAsync();
        };
    }

    /// <summary>Shows the card. The instance it made (also after Cancel, once it exists), or null.</summary>
    public static Task<PackSummary?> ShowAsync(MainWindow shell)
    {
        var card = new CreateInstanceCard();
        return shell.ShowCardAsync(card, card._done.Task, card.Close);
    }

    /// <summary>Cancel can't take back an instance already made, so it hands that one back and the
    /// list shows it.</summary>
    private void Close()
    {
        _cts.Cancel();
        _done.TrySetResult(_created);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Handled) return;
        if (e.Source is ComboBox) return;
        e.Handled = true;
        OnCreate(this, new RoutedEventArgs());
    }

    // ── versions ─────────────────────────────────────────────────────────────

    private async Task LoadMinecraftVersionsAsync()
    {
        Say("Loading the Minecraft versions...", error: false);
        try
        {
            var versions = await MinecraftVersionSource(_cts.Token);
            McVersionBox.ItemsSource = versions;
            McVersionBox.PlaceholderText = versions.Count == 0 ? "None found" : "";
            if (versions.Count > 0) McVersionBox.SelectedIndex = 0;
            Say("", error: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.LogError("instances.create", ex);
            McVersionBox.PlaceholderText = "Not loaded";
            Say("The Minecraft versions could not be loaded: " + InstancePlay.Sentence(ex.Message), error: true);
        }
    }

    private LoaderKind SelectedLoader() =>
        LoaderBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<LoaderKind>(tag, out var kind)
            ? kind
            : LoaderKind.None;

    private PackVisibility SelectedVisibility() =>
        VisibilityBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<PackVisibility>(tag, out var v)
            ? v
            : PackVisibility.Private;

    /// <summary>The chosen loader's builds for the chosen Minecraft version, newest selected.</summary>
    private async Task RefreshLoaderVersionsAsync()
    {
        var request = ++_loaderRequest;
        var loader = SelectedLoader();
        var mc = McVersionBox.SelectedItem as string;
        LoaderVersionBox.ItemsSource = null;
        LoaderVersionBox.IsEnabled = false;

        if (loader == LoaderKind.None || mc is null)
        {
            LoaderVersionBox.PlaceholderText = loader == LoaderKind.None ? "Not needed" : "";
            return;
        }
        LoaderVersionBox.PlaceholderText = "Loading...";
        Say($"Loading the {loader} versions...", error: false);
        try
        {
            var versions = await LoaderVersionSource(loader, mc, _cts.Token);
            if (request != _loaderRequest) return;   // a newer choice took over meanwhile
            LoaderVersionBox.ItemsSource = versions;
            LoaderVersionBox.IsEnabled = versions.Count > 0 && EmptyBox.IsChecked != true;
            LoaderVersionBox.PlaceholderText = versions.Count == 0 ? "None for this version" : "";
            if (versions.Count > 0) LoaderVersionBox.SelectedIndex = 0;
            Say(versions.Count == 0 ? $"{loader} doesn't publish builds for Minecraft {mc}." : "", error: versions.Count == 0);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (request != _loaderRequest) return;
            AppLog.LogError("instances.create", ex);
            LoaderVersionBox.PlaceholderText = "Not loaded";
            Say($"The {loader} versions could not be loaded: " + InstancePlay.Sentence(ex.Message), error: true);
        }
    }

    // ── the rest of the form ─────────────────────────────────────────────────

    /// <summary>The server cuts long summaries without saying so: count down near the limit.</summary>
    private void UpdateSummaryCounter()
    {
        var used = SummaryBox.Text?.Length ?? 0;
        var max = PackText.SummaryMaxLength;
        SummaryCounter.IsVisible = used > max * 3 / 4;
        SummaryCounter.Text = used > max ? $"{used - max} over the limit" : $"{used}/{max}";
        SummaryCounter.Classes.Set("danger", used > max);
    }

    /// <summary>An empty instance is a folder to share, with no Minecraft to pick.</summary>
    private void UpdateKind()
    {
        var playable = EmptyBox.IsChecked != true;
        McVersionBox.IsEnabled = playable;
        LoaderBox.IsEnabled = playable;
        LoaderVersionBox.IsEnabled = playable && LoaderVersionBox.ItemCount > 0;
        UpdateNote();
    }

    private void UpdateNote()
    {
        NoteText.Text = !_signedIn
            ? "It is kept on this computer. Sign in later to add it to an account and share it."
            : SelectedVisibility() != PackVisibility.Private
                ? "Publishing also turns on server hosting: the files you put in it are uploaded for the people who add it."
                : EmptyBox.IsChecked == true
                    ? "An empty instance is a shared folder only: it can't launch Minecraft."
                    : "";
        NoteText.IsVisible = NoteText.Text.Length > 0;
    }

    private void Say(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.IsVisible = text.Length > 0;
        StatusText.Classes.Set("danger", error);
        StatusText.Classes.Set("caption", !error);
    }

    // ── create ───────────────────────────────────────────────────────────────

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (_creating) return;   // Enter and the button can both arrive
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            Say("Enter a name for the instance.", error: true);
            NameBox.Focus();
            return;
        }

        var isEmpty = EmptyBox.IsChecked == true;
        string? mc = null;
        var loader = LoaderKind.None;
        string? loaderVersion = null;
        if (!isEmpty)
        {
            mc = McVersionBox.SelectedItem as string;
            if (string.IsNullOrEmpty(mc))
            {
                Say("Pick a Minecraft version.", error: true);
                return;
            }
            loader = SelectedLoader();
            if (loader != LoaderKind.None)
            {
                loaderVersion = LoaderVersionBox.SelectedItem as string;
                if (string.IsNullOrEmpty(loaderVersion))
                {
                    Say($"Pick a {loader} version.", error: true);
                    return;
                }
            }
        }

        var summary = SummaryBox.Text?.Trim() ?? "";
        if (summary.Length > PackText.SummaryMaxLength)
        {
            Say($"The summary is {summary.Length - PackText.SummaryMaxLength} characters too long.", error: true);
            SummaryBox.Focus();
            return;
        }
        var visibility = _signedIn ? SelectedVisibility() : PackVisibility.Private;

        _creating = true;
        CreateButton.IsEnabled = false;
        try
        {
            if (_created is null)
            {
                Say("Creating...", error: false);
                _created = await App.State.Api.CreatePackAsync(new CreatePackRequest(
                    name, null, isEmpty, mc, loader, loaderVersion, Summary: summary.Length == 0 ? null : summary));
                App.State.Packs.EnsurePackFolder(_created.Id, _created.Name, _created.IsShared);
                AppLog.Log("instances", $"Created instance '{_created.Name}' ({_created.Id}).");
            }

            if (visibility != PackVisibility.Private && !await TryPublishAsync(_created, visibility))
                return;   // the reason is on the card; Create again retries only the publish

            _done.TrySetResult(_created);
        }
        catch (ApiException ex)
        {
            AppLog.LogError("instances.create", ex);
            Say("The instance could not be created: " + InstancePlay.Sentence(ApiClient.ServerSentence(ex) ?? ex.Message), error: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances.create", ex);
            Say("The instance could not be created: " + InstancePlay.Sentence(ex.Message), error: true);
        }
        finally
        {
            _creating = false;
            CreateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Makes a just-created instance Team or Public, with server hosting on: a Public instance that
    /// isn't hosted would hand everyone who adds it an empty instance. On failure it stays Private.
    /// </summary>
    private async Task<bool> TryPublishAsync(PackSummary pack, PackVisibility visibility)
    {
        Say("Publishing...", error: false);
        try
        {
            var published = await App.State.Api.UpdatePackAsync(pack.Id,
                new UpdatePackRequest(null, null, visibility, IsShared: true, null, null, null, null));
            App.State.Packs.EnsurePackFolder(published.Id, published.Name, published.IsShared);
            _created = published;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.LogError("instances.create", ex);
            var why = ex is ApiException api ? ApiClient.ServerSentence(api) ?? api.Message : ex.Message;
            Say($"“{pack.Name}” was created but could not be published: {InstancePlay.Sentence(why)} "
                + "It is private for now. Press Create to try publishing again, or Cancel to keep it private.",
                error: true);
            return false;
        }
    }
}
