using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class DevWindow : Window
{
    public static readonly RuleAction[] ActionValues =
        [RuleAction.Local, RuleAction.Shared, RuleAction.Ignored];

    private readonly ObservableCollection<RuleRow> _rows = new();
    private readonly ObservableCollection<QuotaRow> _quotaRows = new();

    public DevWindow()
    {
        InitializeComponent();
        DefaultRulesGrid.ItemsSource = _rows;
        QuotaGrid.ItemsSource = _quotaRows;
        NewActionBox2.ItemsSource = ActionValues;
        NewActionBox2.SelectedIndex = 0;
        // Seed from the local cache while the server load runs.
        LoadFromCache();
        _ = LoadFromServerAsync();
        _ = LoadQuotasAsync();
    }

    private async Task LoadFromServerAsync()
    {
        try
        {
            var settings = await App.State.Api.GetGlobalSettingsAsync();
            ApplyServerSettings(settings);
            ApiKeyStatus.Text = "Loaded from server.";
            ApiKeyStatus.Foreground = Brushes.SeaGreen;
        }
        catch (Exception ex)
        {
            ApiKeyStatus.Text = "Couldn't load server settings: " + ex.Message;
            ApiKeyStatus.Foreground = Brushes.OrangeRed;
        }
    }

    private void ApplyServerSettings(CloudLauncher.Shared.GlobalSettingsView s)
    {
        CfKeyBox.Text  = "";
        MrTokenBox.Text = "";
        CfKeyBox.Tag  = s.HasCurseForgeApiKey ? "set" : "unset";
        MrTokenBox.Tag = s.HasModrinthToken    ? "set" : "unset";
        CfStatusLabel.Text  = s.HasCurseForgeApiKey ? "✓ Key is set on the server." : "⚠ No key set on the server.";
        CfStatusLabel.Foreground  = s.HasCurseForgeApiKey ? Brushes.SeaGreen : Brushes.OrangeRed;
        MrStatusLabel.Text = s.HasModrinthToken    ? "✓ Token is set on the server." : "No token set (optional).";
        MrStatusLabel.Foreground = s.HasModrinthToken    ? Brushes.SeaGreen : Brushes.Gray;

        if (s.DefaultRules.Count > 0)
        {
            _rows.Clear();
            foreach (var r in s.DefaultRules)
                _rows.Add(new RuleRow(new PackRule { Pattern = r.Pattern, Action = (RuleAction)(int)r.Action }));
            // Mirror to local cache so other code paths (offline pack creation) see the latest.
            App.State.Rules.SaveGlobalDefaults(
                _rows.Select(r => new PackRule { Pattern = r.Pattern, Action = r.Action }).ToList());
        }
    }

    private async void OnSaveApiKeys(object sender, RoutedEventArgs e)
    {
        var cfRaw = CfKeyBox.Text.Trim();
        var mrRaw = MrTokenBox.Text.Trim();
        var req = new CloudLauncher.Shared.UpdateGlobalSettingsRequest(
            CurseForgeApiKey: cfRaw.Length > 0 ? cfRaw : null,
            ModrinthToken:    mrRaw.Length > 0 ? mrRaw : null);
        try
        {
            var updated = await App.State.Api.UpdateGlobalSettingsAsync(req);
            ApplyServerSettings(updated);
            ApiKeyStatus.Text = "Saved to server.";
            ApiKeyStatus.Foreground = Brushes.SeaGreen;
        }
        catch (Exception ex)
        {
            ApiKeyStatus.Text = "Save failed: " + ex.Message;
            ApiKeyStatus.Foreground = Brushes.OrangeRed;
        }
    }

    private async void OnClearCfKey(object sender, RoutedEventArgs e)
    {
        try
        {
            var updated = await App.State.Api.UpdateGlobalSettingsAsync(
                new CloudLauncher.Shared.UpdateGlobalSettingsRequest(ClearCurseForgeApiKey: true));
            ApplyServerSettings(updated);
            ApiKeyStatus.Text = "CurseForge key cleared.";
        }
        catch (Exception ex) { ApiKeyStatus.Text = "Clear failed: " + ex.Message; }
    }

    private async void OnClearMrToken(object sender, RoutedEventArgs e)
    {
        try
        {
            var updated = await App.State.Api.UpdateGlobalSettingsAsync(
                new CloudLauncher.Shared.UpdateGlobalSettingsRequest(ClearModrinthToken: true));
            ApplyServerSettings(updated);
            ApiKeyStatus.Text = "Modrinth token cleared.";
        }
        catch (Exception ex) { ApiKeyStatus.Text = "Clear failed: " + ex.Message; }
    }

    private void LoadFromCache()
    {
        _rows.Clear();
        var rules = App.State.Rules.LoadGlobalDefaults() ?? PackRuleService.DefaultRules();
        foreach (var r in rules)
            _rows.Add(new RuleRow(r));
    }

    private async Task SaveRulesAsync()
    {
        DefaultRulesGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        var rules = _rows.Select(r => new PackRule { Pattern = r.Pattern, Action = r.Action }).ToList();
        try
        {
            await App.State.Rules.PushGlobalDefaultsToServerAsync(rules);
            DevStatusLabel.Text = "Saved to server.";
        }
        catch (Exception ex)
        {
            // Server push failed — still persist locally so the edit isn't lost.
            App.State.Rules.SaveGlobalDefaults(rules);
            DevStatusLabel.Text = "Saved locally, server push failed: " + ex.Message;
        }
    }

    private async void OnAddDefaultRule(object sender, RoutedEventArgs e)
    {
        var pattern = NewPatternBox2.Text.Trim();
        if (string.IsNullOrEmpty(pattern)) { DevStatusLabel.Text = "Enter a pattern."; return; }
        var action = NewActionBox2.SelectedIndex >= 0 && NewActionBox2.SelectedIndex < ActionValues.Length
            ? ActionValues[NewActionBox2.SelectedIndex] : RuleAction.Local;
        _rows.Add(new RuleRow(new PackRule { Pattern = pattern, Action = action }));
        NewPatternBox2.Text = "";
        DevStatusLabel.Text = "";
        await SaveRulesAsync();
    }

    private async void OnDeleteDefaultRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is RuleRow row)
        { _rows.Remove(row); await SaveRulesAsync(); }
    }

    private async void OnResetToHardcoded(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        foreach (var r in PackRuleService.DefaultRules())
            _rows.Add(new RuleRow(r));
        await SaveRulesAsync();
        DevStatusLabel.Text = "Reset to built-in defaults.";
    }

    private async void OnClearAll(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        await SaveRulesAsync();
        DevStatusLabel.Text = "Cleared.";
    }

    private async void OnSaveClose(object sender, RoutedEventArgs e) { await SaveRulesAsync(); Close(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Fire-and-forget — closing the window shouldn't block on a network call.
        _ = SaveRulesAsync();
        base.OnClosing(e);
    }

    // ── quota management ─────────────────────────────────────────────────────

    private async Task LoadQuotasAsync()
    {
        QuotaStatusLabel.Text = "Loading...";
        try
        {
            var infos = await App.State.Api.ListUserQuotasAsync();
            _quotaRows.Clear();
            foreach (var info in infos)
                _quotaRows.Add(new QuotaRow(info));
            QuotaStatusLabel.Text = "";
        }
        catch (Services.ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            QuotaStatusLabel.Text = "⚠ Admin token not active — log out and back in as 'colud' to refresh your JWT.";
        }
        catch (Exception ex) { QuotaStatusLabel.Text = "Load failed: " + ex.Message; }
    }

    private async void OnRefreshQuotas(object sender, RoutedEventArgs e) => await LoadQuotasAsync();

    private async void OnSaveQuota(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is QuotaRow row)
            await ApplyQuotaAsync(row);
    }

    private async void OnSetUnlimited(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is QuotaRow row)
        {
            row.QuotaMb = "";   // empty = unlimited
            await ApplyQuotaAsync(row);
        }
    }

    private async Task ApplyQuotaAsync(QuotaRow row)
    {
        QuotaStatusLabel.Text = $"Saving {row.Username}...";
        try
        {
            long? quotaBytes = null;
            if (!string.IsNullOrWhiteSpace(row.QuotaMb))
            {
                if (!double.TryParse(row.QuotaMb, out var mb) || mb < 0)
                { QuotaStatusLabel.Text = "Quota must be a positive number (MB) or empty for unlimited."; return; }
                quotaBytes = (long)(mb * 1024 * 1024);
            }
            var updated = await App.State.Api.SetUserQuotaAsync(row.UserId, new SetQuotaRequest(quotaBytes));
            row.UpdateFrom(updated);
            QuotaStatusLabel.Text = $"Saved: {row.Username} → {(quotaBytes is null ? "unlimited" : $"{row.QuotaMb} MB")}";
        }
        catch (Exception ex) { QuotaStatusLabel.Text = "Save failed: " + ex.Message; }
    }
}

// ── row view-models ──────────────────────────────────────────────────────────

public sealed class QuotaRow(UserQuotaInfo info) : INotifyPropertyChanged
{
    public Guid   UserId   { get; } = info.UserId;
    public string Username { get; } = info.Username;

    private long   _usedBytes  = info.UsedBytes;
    private long?  _quotaBytes = info.QuotaBytes;
    private string _quotaMb    = info.QuotaBytes is null ? "" : ((double)info.QuotaBytes.Value / (1024 * 1024)).ToString("F0");

    public string UsedLabel    => FormatBytes(_usedBytes);
    public string PercentLabel =>
        _quotaBytes is null || _quotaBytes == 0 ? "—"
        : $"{(double)_usedBytes / _quotaBytes.Value * 100:F1}%";

    public Brush QuotaColor =>
        _quotaBytes is null ? Brushes.Gray
        : _usedBytes >= _quotaBytes.Value ? Brushes.DarkRed
        : _usedBytes >= _quotaBytes.Value * 0.9 ? Brushes.DarkOrange
        : Brushes.Black;

    public string QuotaMb
    {
        get => _quotaMb;
        set { _quotaMb = value; OnPropertyChanged(); }
    }

    public void UpdateFrom(UserQuotaInfo updated)
    {
        _usedBytes  = updated.UsedBytes;
        _quotaBytes = updated.QuotaBytes;
        _quotaMb    = updated.QuotaBytes is null ? "" : ((double)updated.QuotaBytes.Value / (1024 * 1024)).ToString("F0");
        OnPropertyChanged(nameof(UsedLabel));
        OnPropertyChanged(nameof(PercentLabel));
        OnPropertyChanged(nameof(QuotaColor));
        OnPropertyChanged(nameof(QuotaMb));
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1024                    => $"{bytes} B",
            < 1024 * 1024             => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024     => $"{bytes / (1024.0 * 1024):F1} MB",
            _                         => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
