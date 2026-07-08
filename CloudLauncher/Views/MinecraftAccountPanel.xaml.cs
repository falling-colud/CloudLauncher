using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

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

    private async void OnAddMicrosoft(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Opening Microsoft sign-in…";
        MicrosoftButton.IsEnabled = false;
        try
        {
            var acc = await App.State.MinecraftAccounts.AddMicrosoftAsync();
            StatusLabel.Text = $"Signed in as {acc.Username}.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Sign-in failed: " + ex.Message;
            AppLog.LogError("AddMicrosoft", ex);
        }
        finally { MicrosoftButton.IsEnabled = true; }
    }

    private void OnAddOffline(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "";
        try
        {
            var acc = App.State.MinecraftAccounts.AddOffline(OfflineNameBox.Text);
            OfflineNameBox.Text = "";
            StatusLabel.Text = $"Added offline account: {acc.Username}.";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnUseAccount(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is Guid id)
            App.State.MinecraftAccounts.Switch(id);
    }

    private async void OnRemoveAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not Guid id) return;
        var acc = App.State.MinecraftAccounts.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is null) return;
        if (MessageBox.Show(_shell, $"Remove account {acc.Username}?",
                "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await App.State.MinecraftAccounts.RemoveAsync(id);
    }
}

public sealed class AccountRow
{
    public Guid Id { get; }
    public string Username { get; }
    public string KindLabel { get; }
    public bool IsCurrent { get; }
    public string IconGlyph { get; }
    public Brush  IconBg    { get; }
    public Brush  IconFg    { get; }
    public Brush  RowBackground { get; }
    public Brush  BorderColor   { get; }
    public Visibility UseButtonVisibility   => IsCurrent ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ActiveLabelVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    public AccountRow(StoredMinecraftAccount a, bool isCurrent)
    {
        var rs = Application.Current.Resources;
        Id = a.Id;
        Username = a.Username;
        IsCurrent = isCurrent;
        if (a.Kind == MinecraftAccountKind.Microsoft)
        {
            KindLabel = "Microsoft account";
            IconGlyph = ""; // Xbox/Microsoft mark
            IconBg = (Brush)rs["AccentSoftBrush"];
            IconFg = (Brush)rs["AccentBrush"];
        }
        else
        {
            KindLabel = "Offline";
            IconGlyph = ""; // Contact glyph
            IconBg = (Brush)rs["Surface4Brush"];
            IconFg = (Brush)rs["TextSecondaryBrush"];
        }
        RowBackground = isCurrent ? (Brush)rs["AccentSoftBrush"] : (Brush)rs["Surface2Brush"];
        BorderColor   = isCurrent ? (Brush)rs["AccentBrush"]     : (Brush)rs["BorderBrush"];
    }
}
