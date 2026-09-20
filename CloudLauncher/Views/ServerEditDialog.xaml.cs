using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The in-window card for adding a server to an instance's multiplayer list, or editing one that is
/// already in it.
/// </summary>
/// <remarks>
/// One card for both jobs, because they ask the same three questions and only the instance differs:
/// adding needs to know which list to write to, editing already knows and must not silently move the
/// entry somewhere else. The address is validated as it is typed rather than on save, so the hint
/// line can show the host and port that will actually be used — the commonest mistake here is a
/// pasted address with a stray <c>https://</c> or a trailing slash on it.
/// </remarks>
public partial class ServerEditDialog : UserControl
{
    /// <summary>What the card came back with.</summary>
    public sealed record EditResult(Guid PackId, string Name, string Address);

    private readonly TaskCompletionSource<EditResult?> _tcs = new();
    private readonly bool _editing;

    private ServerEditDialog(string title, string subtitle, string actionText, bool editing,
                             IReadOnlyList<PackSummary> instances, Guid? fixedPackId,
                             string name, string address)
    {
        InitializeComponent();
        _editing = editing;
        TitleLabel.Text = title;
        SubLabel.Text = subtitle;
        SaveButton.Content = actionText;
        NameBox.Text = name;
        AddressBox.Text = address;

        var items = instances
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new InstanceItem(p.Id, p.Name))
            .ToList();
        InstanceBox.ItemsSource = items;
        InstanceBox.SelectedItem = items.FirstOrDefault(i => i.Id == fixedPackId) ?? items.FirstOrDefault();
        // Editing writes back to the file the entry came from. Moving an entry between instances is
        // "copy to instance" plus a delete, which the page offers as two separate, reversible actions.
        InstanceBox.IsEnabled = !editing;
        InstanceBox.ToolTip = editing
            ? "The instance this entry lives in. Use “Copy to instance…” to put it in another one."
            : "Which instance's server list this entry goes into";

        Focusable = true;
        Loaded += (_, _) =>
        {
            Animate.SlideFadeIn(this, 0, 14, 200);
            UpdateAddressHint();
            (name.Length == 0 ? NameBox : AddressBox).Focus();
        };
    }

    public Task<EditResult?> Result => _tcs.Task;
    public void Cancel() => _tcs.TrySetResult(null);

    /// <summary>Asks for a new server. <paramref name="preselect"/> is the instance the page is
    /// filtered to, so the obvious destination is already chosen.</summary>
    public static async Task<EditResult?> AddAsync(MainWindow host, IReadOnlyList<PackSummary> instances,
                                                   Guid? preselect)
    {
        var card = new ServerEditDialog(
            "Add server",
            "Adds the server to one instance's multiplayer list, exactly as the in-game “Add Server” button would.",
            "Add", editing: false, instances, preselect, "", "");
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    /// <summary>Edits an existing entry in place, keeping it where it is in the instance's list.</summary>
    public static async Task<EditResult?> EditAsync(MainWindow host, IReadOnlyList<PackSummary> instances,
                                                    ServerEntry entry)
    {
        var card = new ServerEditDialog(
            "Edit server",
            $"Changes this entry in {entry.SourcePackName}'s multiplayer list. Its place in the list is kept.",
            "Save", editing: true, instances, entry.SourcePackId, entry.Name, entry.Address);
        await host.ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs e) => UpdateAddressHint();

    /// <summary>Shows what the typed address resolves to, which is the quickest way to notice that a
    /// pasted URL or an IPv6 literal is not being read the way it looks.</summary>
    private void UpdateAddressHint()
    {
        var raw = AddressBox.Text?.Trim() ?? "";
        if (raw.Length == 0)
        {
            AddressHint.Text = "Leave the port off for 25565.";
            return;
        }
        var (host, port) = MinecraftServerPing.ParseAddress(raw);
        AddressHint.Text = host.Length == 0
            ? "That does not look like an address."
            : $"Connects to {host} on port {port}.";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _tcs.TrySetResult(null);

    private void OnSave(object sender, RoutedEventArgs e) => Accept();

    /// <summary>
    /// Validates and answers, or explains what is wrong and stays open.
    /// </summary>
    /// <remarks>The name is allowed to be empty and falls back to the address, which is what the game
    /// shows for a direct-connect entry; the address is not, because an entry without one is a row
    /// nobody can join and Minecraft itself would skip it.</remarks>
    private void Accept()
    {
        var address = AddressBox.Text?.Trim() ?? "";
        if (address.Length == 0) { Fail("Enter the server's address."); return; }

        var (host, _) = MinecraftServerPing.ParseAddress(address);
        if (host.Length == 0 || host.Contains(' ') || host.Contains('/'))
        {
            Fail("That is not a server address — it should be a host name or IP, optionally with :port.");
            return;
        }

        if (InstanceBox.SelectedItem is not InstanceItem instance)
        {
            Fail(_editing ? "This entry's instance is missing." : "Pick an instance to add it to.");
            return;
        }

        var name = NameBox.Text?.Trim() ?? "";
        _tcs.TrySetResult(new EditResult(instance.Id, name.Length > 0 ? name : address, address));
    }

    private void Fail(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _tcs.TrySetResult(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>One row of the instance combo.</summary>
    private sealed record InstanceItem(Guid Id, string Label);
}
