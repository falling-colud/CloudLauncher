using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>What the picker returns: the chosen instance, and its detail when it could be
/// fetched.</summary>
/// <remarks><see cref="Detail"/> is null only when the server could not be asked and nothing was
/// cached. The caller then opens the instance's own page instead.</remarks>
public sealed record ShareInstanceChoice(PackSummary Pack, PackDetail? Detail);

/// <summary>One instance as a row of the picker.</summary>
/// <remarks>Computed once when built, so a row never disagrees with the note under the list.</remarks>
public sealed class ShareInstanceRow
{
    public required PackSummary Pack { get; init; }

    /// <summary>The access list, when the hub could fetch it. Null means unknown, which is not the
    /// same as "nobody", and the row says so.</summary>
    public PackDetail? Detail { get; init; }

    public Guid Id => Pack.Id;
    public string Name => Pack.Name;

    /// <summary>Minecraft version, loader and who can already reach it: what tells two instances
    /// called "Test" apart.</summary>
    public required string SubLine { get; init; }

    /// <summary>Scanning aid: "shared", "public", "team", or empty when nobody else has it.</summary>
    public required string StateTag { get; init; }

    /// <summary>True when this instance already has an audience, so the dialog offers to manage it
    /// rather than to start it.</summary>
    public required bool AlreadyShared { get; init; }

    /// <summary>How many people and teams can reach it, in words, or null when that is not known.</summary>
    public string? AudienceLine { get; init; }

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    /// <summary>What a typed query is matched against: name, version and loader, since "1.20.1" and
    /// "fabric" are common ways to narrow a long list.</summary>
    public required string Haystack { get; init; }
}

/// <summary>The picker behind the Sharing page's main action: choose an instance, then go and share
/// it.</summary>
/// <remarks>It only picks; sharing itself happens on the Share &amp; sync tab
/// (<see cref="PackSharePanel"/>). Instances that are already shared stay in the list, but the row
/// shows who has them and the button reads "Manage sharing". Instances the user can't share
/// (someone else's, without manage rights) are left out, with a line under the list saying how many
/// and why. Shown as an in-window card through <see cref="MainWindow.ShowCardAsync"/>: Escape and
/// the backdrop cancel, Enter takes the selection.</remarks>
public partial class ShareInstanceDialog : UserControl
{
    /// <summary>Row count from which the search box is shown. Below this the whole list fits on
    /// screen.</summary>
    private const int SearchFrom = 8;

    /// <summary>How many owner names the "not listed" line spells out before switching to a
    /// count.</summary>
    private const int OwnerNameCap = 3;

    private readonly TaskCompletionSource<ShareInstanceChoice?> _tcs = new();
    private readonly CancellationTokenSource _work = new();

    private readonly List<ShareInstanceRow> _all = [];

    /// <summary>True while the chosen instance's detail is being fetched, so a second Enter cannot
    /// start a second fetch.</summary>
    private bool _resolving;

    public ShareInstanceDialog()
    {
        InitializeComponent();

        // The card itself must be focusable: while loading, the search box is hidden and the list
        // is empty, so neither can take focus, and with nothing focused Escape would reach the page
        // behind.
        Focusable = true;
        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            FocusEntry();
        };
    }

    /// <summary>Puts the caret where typing should go: the search box, else the list, else the card
    /// (only while loading).</summary>
    private void FocusEntry()
    {
        if (SearchBar.Visibility == Visibility.Visible) Search.Focus();
        else if (List.Visibility == Visibility.Visible && List.Items.Count > 0) List.Focus();
        else Focus();
    }

    /// <summary>The chosen instance, or null if the dialog was cancelled.</summary>
    public Task<ShareInstanceChoice?> Result => _tcs.Task;

    /// <summary>The backdrop's cancel, handed to <see cref="MainWindow.ShowCardAsync"/>.</summary>
    public void Cancel()
    {
        _work.Cancel();
        _tcs.TrySetResult(null);
    }

    /// <summary>Shows the picker over the window and returns what was chosen, or null.</summary>
    /// <remarks>Filling runs alongside the card: a recent snapshot lands in the first frame, but a
    /// cold one takes a dozen server calls and the dialog should be visible meanwhile.</remarks>
    public static async Task<ShareInstanceChoice?> ShowAsync(MainWindow host, SharingHubService sharing)
    {
        var card = new ShareInstanceDialog();
        var shown = host.ShowCardAsync(card, card.Result, card.Cancel);
        _ = card.FillAsync(sharing);
        await shown;
        return card.Result.Result;
    }

    // ── filling ──────────────────────────────────────────────────────────────

    private async Task FillAsync(SharingHubService sharing)
    {
        if (!App.State.Settings.IsLoggedIn)
        {
            ShowNote("Sign in to share an instance",
                     "Sharing lives on the server, so it has to know who you are before it can give "
                     + "anybody else access to something of yours.");
            return;
        }

        ShowNote("Looking for your instances...", "");
        try
        {
            var snap = await sharing.LoadAsync(force: false, _work.Token);
            Apply(snap, sharing.MyUserId);
        }
        catch (OperationCanceledException)
        {
            // The dialog was dismissed while the snapshot was still coming. Nothing to paint onto.
        }
        catch (Exception ex)
        {
            AppLog.LogError("sharing.picker", ex);
            ShowNote("Your instances could not be read", SharingHubService.Explain(ex));
        }
    }

    /// <summary>Turns one snapshot into rows.</summary>
    /// <remarks>Public for the offscreen layout harness, which passes in a snapshot it built
    /// itself.</remarks>
    public void Apply(SharingSnapshot snap, Guid me)
    {
        _all.Clear();

        // Instances someone else owns without making me a manager. Every sharing call on them would
        // be refused.
        var blocked = 0;
        var owners = new List<string>();

        foreach (var pack in snap.Packs)
        {
            var mine = me != Guid.Empty && pack.OwnerId == me;
            if (!mine && !pack.EffectivePermissions.HasFlag(PackPermissions.ManageCollaborators))
            {
                blocked++;
                if (pack.OwnerUsername is { Length: > 0 } owner && !owners.Contains(owner))
                    owners.Add(owner);
                continue;
            }

            snap.Details.TryGetValue(pack.Id, out var detail);
            _all.Add(BuildRow(pack, detail));
        }

        // Not shared first, then newest: the button says "Share an instance", so unshared ones are
        // the likely target. Shared ones are easy to find with the search box.
        _all.Sort((a, b) =>
        {
            if (a.AlreadyShared != b.AlreadyShared) return a.AlreadyShared ? 1 : -1;
            return b.Pack.UpdatedAt.CompareTo(a.Pack.UpdatedAt);
        });

        SearchBar.Visibility = _all.Count >= SearchFrom ? Visibility.Visible : Visibility.Collapsed;
        SetExcluded(blocked, owners);
        ApplyFilter();

        // The rows only exist now, so focus the search box or list now. Only while the card still
        // holds focus (Loaded put it there), so focus is never pulled away from someone who has
        // started typing.
        if (IsKeyboardFocusWithin && !Search.IsKeyboardFocusWithin) FocusEntry();
    }

    private static ShareInstanceRow BuildRow(PackSummary pack, PackDetail? detail)
    {
        var people = detail?.Collaborators.Count ?? 0;
        var teams = detail?.Teams.Count ?? 0;
        var pending = detail?.PendingInvitations?.Count(i => i.AcceptedAt is null && i.RevokedAt is null) ?? 0;
        var shared = pack.IsShared || pack.Visibility != PackVisibility.Private
                     || people > 0 || teams > 0 || pending > 0;

        var bits = new List<string>
        {
            pack.MinecraftVersion is { Length: > 0 } mc ? mc : "no Minecraft version"
        };
        if (pack.Loader != LoaderKind.None) bits.Add(pack.Loader.ToString());

        var audience = Audience(detail, people, teams, pending, pack.Visibility);
        bits.Add(shared ? audience ?? "shared - who has access is not known" : "not shared yet");

        return new ShareInstanceRow
        {
            Pack = pack,
            Detail = detail,
            SubLine = string.Join(" · ", bits),
            StateTag = !shared ? "" : pack.Visibility switch
            {
                PackVisibility.Public => "public",
                PackVisibility.Team => "team",
                _ => "shared"
            },
            AlreadyShared = shared,
            AudienceLine = audience,
            Haystack = $"{pack.Name}\n{pack.MinecraftVersion}\n{pack.Loader}"
        };
    }

    /// <summary>Who can already reach it, in words. Null when the hub could not read the access
    /// list, which the row must say instead of "nobody else".</summary>
    private static string? Audience(PackDetail? detail, int people, int teams, int pending,
                                    PackVisibility visibility)
    {
        if (visibility == PackVisibility.Public) return "public on this server";
        if (detail is null) return null;

        var bits = new List<string>();
        if (people > 0) bits.Add(SharingHubService.Plural(people, "person", "people"));
        if (teams > 0) bits.Add(SharingHubService.Plural(teams, "team"));

        var who = bits.Count > 0 ? "shared with " + string.Join(" and ", bits) : "nobody else yet";
        if (pending > 0) who += " · " + SharingHubService.Plural(pending, "invitation") + " pending";
        return who;
    }

    private void SetExcluded(int blocked, IReadOnlyList<string> owners)
    {
        if (blocked == 0)
        {
            ExcludedNote.Text = "";
            ExcludedNote.Visibility = Visibility.Collapsed;
            return;
        }

        ExcludedNote.Text = $"{SharingHubService.Plural(blocked, "instance")} in your library "
                          + $"{(blocked == 1 ? "is" : "are")} not listed: "
                          + $"{(blocked == 1 ? "it belongs" : "they belong")} to {Whose(owners)}, "
                          + "and only an owner can change who reaches an instance.";
        ExcludedNote.Visibility = Visibility.Visible;
    }

    /// <summary>The owners as a phrase for a sentence: names when there are few, a count
    /// otherwise.</summary>
    private static string Whose(IReadOnlyList<string> owners)
    {
        if (owners.Count == 0) return "somebody else";
        if (owners.Count == 1) return owners[0];

        var named = owners.Take(OwnerNameCap).ToList();
        var rest = owners.Count - named.Count;
        if (rest > 0) named.Add(SharingHubService.Plural(rest, "other"));
        return string.Join(", ", named.Take(named.Count - 1)) + " and " + named[^1];
    }

    // ── filtering ────────────────────────────────────────────────────────────

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility =
            Search.Text.Length == 0 && !Search.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void OnSearchFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        SearchPlaceholder.Visibility =
            Search.Text.Length == 0 && !Search.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyFilter()
    {
        var query = Search.Text.Trim();
        var rows = query.Length == 0
            ? _all
            : _all.Where(r => r.Haystack.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        List.ItemsSource = rows;
        if (rows.Count > 0) List.SelectedIndex = 0;

        if (_all.Count == 0)
            ShowNote("Nothing here is yours to share",
                     "Sharing is always a property of an instance you own. Create one, or download "
                     + "one somebody shared with you and make a copy of it, and it will be here.");
        else if (rows.Count == 0)
            ShowNote("Nothing matched",
                     "No instance's name, Minecraft version or loader contains that.");
        else
            ShowList();

        UpdateSelection();
    }

    private void ShowNote(string title, string body)
    {
        NoteTitle.Text = title;
        NoteBody.Text = body;
        NoteBody.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotePanel.Visibility = Visibility.Visible;
        List.Visibility = Visibility.Collapsed;
        UpdateSelection();
    }

    private void ShowList()
    {
        NotePanel.Visibility = Visibility.Collapsed;
        List.Visibility = Visibility.Visible;
    }

    // ── the selection, and what the button will do about it ──────────────────

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (_resolving) return;

        if (List.Visibility != Visibility.Visible || List.SelectedItem is not ShareInstanceRow row)
        {
            AcceptButton.Content = "Share it";
            AcceptButton.IsEnabled = false;
            SelectionNote.Text = _all.Count == 0 ? "" : "Pick an instance to see what sharing it does.";
            return;
        }

        AcceptButton.Content = row.AlreadyShared ? "Manage sharing" : "Share it";
        AcceptButton.IsEnabled = true;

        // What the accent button is about to do, worded differently for an instance that already
        // has an audience. It has to fit in the note's three lines, hence the shortened name.
        var name = ShortName(row.Name);
        SelectionNote.Text = (row.AlreadyShared
            ? $"'{name}' is already shared - {row.AudienceLine ?? "who has it is not known"}. "
              + "Opening it goes to its Share & sync tab: invite somebody else, or stop sharing it."
            : $"'{name}' is not shared yet. Opening it goes to its Share & sync tab: publish it, "
              + "then invite people or copy a link.")
            + (App.State.IsOffline ? " Offline: that tab opens read-only." : "");
    }

    /// <summary>The longest an instance's name may be inside a sentence that has to fit on three
    /// lines.</summary>
    /// <remarks>Cut back to a word boundary when there is one, since a name cut mid-word looks like
    /// a rendering bug; the row above shows the full name anyway. (Not called <c>Clip</c>: that is
    /// a UIElement property.)</remarks>
    private static string ShortName(string name)
    {
        const int max = 44;
        if (name.Length <= max) return name;

        var cut = name[..(max - 1)].TrimEnd();
        var space = cut.LastIndexOf(' ');
        if (space >= max / 2) cut = cut[..space].TrimEnd(' ', ',', '—', '-', '·');
        return cut + "...";
    }

    // ── taking it ────────────────────────────────────────────────────────────

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => _ = AcceptAsync();

    private async void OnAccept(object sender, RoutedEventArgs e) => await AcceptAsync();

    /// <summary>Resolves the chosen instance's detail and completes the dialog.</summary>
    /// <remarks>The hub already has details for every instance you can manage, so this usually
    /// returns immediately. Otherwise it fetches the detail so the user lands on the tab they asked
    /// for; if that fails, the caller falls back to the instance page.</remarks>
    private async Task AcceptAsync()
    {
        if (_resolving) return;
        if (List.Visibility != Visibility.Visible || List.SelectedItem is not ShareInstanceRow row) return;

        var detail = row.Detail ?? PackDetailCache.Load(row.Id);
        if (detail is null)
        {
            _resolving = true;
            var label = AcceptButton.Content;
            AcceptButton.Content = "Opening...";
            AcceptButton.IsEnabled = false;
            try
            {
                detail = await App.State.Api.GetPackAsync(row.Id, _work.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLog.LogError("sharing.picker.detail", ex);
            }
            finally
            {
                _resolving = false;
                AcceptButton.Content = label;
                AcceptButton.IsEnabled = true;
            }
        }

        _work.Cancel();
        _tcs.TrySetResult(new ShareInstanceChoice(row.Pack, detail));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;

            case Key.Enter:
                _ = AcceptAsync();
                e.Handled = true;
                break;

            // Arrow keys move from the search box into the list, so you can filter and then pick
            // from the keyboard.
            case Key.Down when Search.IsKeyboardFocusWithin:
            case Key.Up when Search.IsKeyboardFocusWithin:
                Step(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
        }
        base.OnPreviewKeyDown(e);
    }

    private void Step(int by)
    {
        if (List.Items.Count == 0) return;
        List.SelectedIndex = Math.Clamp(List.SelectedIndex + by, 0, List.Items.Count - 1);
        if (List.SelectedItem is { } item) List.ScrollIntoView(item);
    }
}
