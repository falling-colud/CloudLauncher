using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>The saved Minecraft accounts: add Microsoft or offline ones, and pick which one the
/// launcher plays as.</summary>
public partial class MinecraftAccountPanel : Page
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<AccountRow> _rows = new();

    public MinecraftAccountPanel(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        AccountsList.ItemsSource = _rows;
        App.State.MinecraftAccounts.AccountsChanged += Refresh;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => App.State.MinecraftAccounts.AccountsChanged -= Refresh;
    }

    private void Refresh()
    {
        var svc = App.State.MinecraftAccounts;
        var current = svc.Current;
        _rows.Clear();
        foreach (var a in svc.Accounts)
            _rows.Add(new AccountRow(a, isCurrent: current?.Id == a.Id));

        AccountsCount.Text = svc.Accounts.Count switch
        {
            0 => "no accounts",
            1 => "1 account",
            var n => $"{n} accounts"
        };
        EmptyLabel.Visibility = svc.Accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _shell.UpdateMcChip();
    }

    // ── status ───────────────────────────────────────────────────────────────

    private void Okay(string message)
    {
        StatusLabel.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        StatusLabel.Text = message;
    }

    private void Fail(string message)
    {
        StatusLabel.SetResourceReference(ForegroundProperty, "DangerBrush");
        StatusLabel.Text = message;
    }

    // ── adding ───────────────────────────────────────────────────────────────

    private async void OnAddMicrosoft(object sender, RoutedEventArgs e)
    {
        Okay("Opening Microsoft sign-in…");
        MicrosoftButton.IsEnabled = false;
        try
        {
            var acc = await App.State.MinecraftAccounts.AddMicrosoftAsync();
            Okay($"Signed in as {acc.Username}.");
        }
        catch (Exception ex)
        {
            Fail("Sign-in failed: " + ex.Message);
            AppLog.LogError("AddMicrosoft", ex);
        }
        finally { MicrosoftButton.IsEnabled = true; }
    }

    /// <summary>Enter in the offline name box adds the account — the panel has no default button.</summary>
    private void OnOfflineNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnAddOffline(sender, e);
    }

    private void OnAddOffline(object sender, RoutedEventArgs e)
    {
        try
        {
            var acc = App.State.MinecraftAccounts.AddOffline(OfflineNameBox.Text);
            OfflineNameBox.Text = "";
            Okay($"Added offline account: {acc.Username}.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    // ── row actions ──────────────────────────────────────────────────────────

    private void OnUseAccount(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id }) Use(id);
    }

    private void OnCtxUse(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is { } row) Use(row.Id);
    }

    private void Use(Guid id)
    {
        try
        {
            App.State.MinecraftAccounts.Switch(id);
            Okay($"Now playing as {App.State.MinecraftAccounts.DisplayName}.");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnCtxCopyUsername(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is not { } row) return;
        Okay(ClipboardHelper.TrySetText(row.Username)
            ? "Username copied."
            : "The clipboard is in use by another program.");
    }

    /// <summary>The UUID is stored for every signed-in Microsoft account and shown nowhere else —
    /// it is what a server operator asks for when whitelisting or banning someone.</summary>
    private void OnCtxCopyUuid(object sender, RoutedEventArgs e)
    {
        if (RowFromSender(sender) is not { } row) return;
        if (!row.HasUuid) { Fail("This account has no UUID — offline accounts do not get one."); return; }
        Okay(ClipboardHelper.TrySetText(row.Uuid)
            ? "UUID copied."
            : "The clipboard is in use by another program.");
    }

    private async void OnRemoveAccount(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: Guid id }) await RemoveAsync(id);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async void OnCtxRemove(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFromSender(sender) is { } row) await RemoveAsync(row.Id);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task RemoveAsync(Guid id)
    {
        var acc = App.State.MinecraftAccounts.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is null) return;

        var consequence = acc.Kind == MinecraftAccountKind.Microsoft
            ? "You will have to sign in to Microsoft again to use it."
            : "Offline accounts can be added back at any time by typing the name again.";

        if (!await AppDialog.ConfirmAsync(_shell, "Remove account",
                $"Remove {acc.Username} from this launcher?\n\n{consequence}",
                "Remove", "Cancel", danger: true))
            return;

        await App.State.MinecraftAccounts.RemoveAsync(id);
        Okay($"Removed {acc.Username}.");
    }

    /// <summary>Resolves the account row a row button or context-menu item belongs to.</summary>
    private static AccountRow? RowFromSender(object sender)
    {
        if (sender is FrameworkElement el && el.DataContext is AccountRow direct) return direct;
        if (sender is MenuItem mi)
        {
            var parent = mi.Parent;
            while (parent is MenuItem p) parent = p.Parent;
            if (parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target
                && target.DataContext is AccountRow row) return row;
        }
        return null;
    }
}

/// <summary>
/// One saved Minecraft account, as the list shows it.
/// </summary>
/// <remarks>
/// Only data here — no brushes. Snapshotting <c>(Brush)Application.Current.Resources[...]</c> in the
/// constructor left these rows painted in the old palette after a theme change, because
/// <see cref="ThemeService"/> replaces each resource with a new frozen brush on every apply. The
/// template now picks its colours with DynamicResource from <see cref="IsCurrent"/> and
/// <see cref="IsMicrosoft"/>.
/// </remarks>
public sealed class AccountRow
{
    public Guid Id { get; }
    public string Username { get; }
    public string KindLabel { get; }
    public bool IsCurrent { get; }
    public bool IsMicrosoft { get; }
    public string IconGlyph { get; }
    public string? Uuid { get; }
    public bool HasUuid => !string.IsNullOrEmpty(Uuid);

    public Visibility UseButtonVisibility => IsCurrent ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ActiveLabelVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    public AccountRow(StoredMinecraftAccount a, bool isCurrent)
    {
        Id = a.Id;
        Username = a.Username;
        Uuid = a.Uuid;
        IsCurrent = isCurrent;
        IsMicrosoft = a.Kind == MinecraftAccountKind.Microsoft;
        KindLabel = IsMicrosoft ? "Microsoft account" : "Offline";
        IconGlyph = IsMicrosoft ? "" : "";   // Microsoft mark / contact
    }

    public string RowTooltip =>
        $"{Username} · {KindLabel}"
        + (IsCurrent ? "\nThis is the account the launcher plays as." : "")
        + (HasUuid ? $"\nUUID {Uuid}" : "\nOffline accounts have no UUID.");
}
