using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <remarks>
/// This window is modal, so it hosts its own <see cref="IDialogHost"/> overlay — a confirmation
/// drawn on the main window would appear behind it and could never be clicked.
/// </remarks>
public partial class PackRulesDialog : Window, IDialogHost
{
    private readonly string _packRoot;
    private readonly Guid? _packId;     // set when the pack is shared (for server sync)
    private readonly bool _isOwner;
    private readonly ObservableCollection<RuleRow> _rows = new();

    // Exposed as static so XAML ItemsSource binding can reference it.
    public static readonly RuleAction[] ActionValues =
        [RuleAction.Local, RuleAction.Shared, RuleAction.Ignored];

    public PackRulesDialog(string packRoot, string packName, Guid? packId = null, bool isOwner = false)
    {
        InitializeComponent();
        Title = $"File rules - {packName}";
        _packRoot = packRoot;
        _packId = packId;
        _isOwner = isOwner;

        RulesGrid.ItemsSource = _rows;
        NewActionBox.ItemsSource = ActionValues;
        NewActionBox.SelectedIndex = 0;

        Load();
        ApplyReadOnlyState();
    }

    /// <summary>True when edits here would only ever be written to this PC.</summary>
    /// <remarks>
    /// On a hosted pack the rules belong to the owner: <see cref="PushToServerAsync"/> is the only
    /// thing that makes a rule change reach the other collaborators, and the server refuses it from
    /// anyone else. A collaborator editing the grid used to see the change save and stick, while in
    /// fact it lived on their disk alone until the next sync overwrote it — so the grid is locked
    /// instead of quietly lying.
    /// </remarks>
    private bool IsReadOnlyForMe => _packId.HasValue && !_isOwner;

    private void ApplyReadOnlyState()
    {
        if (!IsReadOnlyForMe) return;

        RulesGrid.IsReadOnly = true;
        NewPatternBox.IsEnabled = false;
        NewActionBox.IsEnabled = false;
        AddRuleButton.IsEnabled = false;
        ResetDefaultsButton.IsEnabled = false;
        DeleteColumn.Visibility = Visibility.Collapsed;
        StatusLabel.Text = "File rules for a hosted instance are set by its owner.";
    }

    private void Load()
    {
        _rows.Clear();
        foreach (var r in App.State.Rules.Load(_packRoot))
            _rows.Add(new RuleRow(r));
    }

    private void Save()
    {
        if (IsReadOnlyForMe) return;

        RulesGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        var rules = _rows.Select(r => new PackRule { Pattern = r.Pattern, Action = r.Action }).ToList();
        App.State.Rules.Save(_packRoot, rules);

        // Push to server so all collaborators get the updated rules
        if (_packId.HasValue && _isOwner)
            _ = PushToServerAsync(rules);
    }

    private async Task PushToServerAsync(List<PackRule> rules)
    {
        try
        {
            await App.State.Api.PushPackRulesAsync(_packId!.Value, PackRuleService.ToShared(rules));
            StatusLabel.Text = "Rules saved and synced to server.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Saved locally but server sync failed: {ex.Message}";
        }
    }

    private void OnNewPatternKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        OnAddRule(sender, e);
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        if (IsReadOnlyForMe) return;

        var pattern = NewPatternBox.Text.Trim();
        if (string.IsNullOrEmpty(pattern)) { StatusLabel.Text = "Enter a pattern."; return; }

        // Two rules for the same pattern is not an error the matcher can resolve usefully — the
        // earlier one always wins — so replace rather than pile up a dead duplicate.
        if (_rows.FirstOrDefault(r => string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase))
            is { } existing)
        {
            existing.Action = SelectedNewAction();
            NewPatternBox.Text = "";
            StatusLabel.Text = $"Updated the existing rule for '{pattern}'.";
            Save();
            return;
        }

        _rows.Add(new RuleRow(new PackRule { Pattern = pattern, Action = SelectedNewAction() }));
        NewPatternBox.Text = "";
        StatusLabel.Text = "";
        Save();
    }

    private RuleAction SelectedNewAction() =>
        NewActionBox.SelectedIndex >= 0 && NewActionBox.SelectedIndex < ActionValues.Length
            ? ActionValues[NewActionBox.SelectedIndex]
            : RuleAction.Local;

    private async void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not RuleRow row) return;
        try
        {
            // On a shared pack, deleting a rule immediately pushes to the server for every
            // collaborator, so confirm first to avoid a one-click destructive sync.
            if (_packId.HasValue && _isOwner &&
                !await AppDialog.ConfirmAsync(this, "Remove rule",
                    $"Remove the rule for '{row.Pattern}'?" + Environment.NewLine + Environment.NewLine
                        + "This syncs to everyone who shares this instance.",
                    "Remove", "Cancel", danger: true))
                return;

            _rows.Remove(row);
            Save();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Couldn't remove that rule: " + ex.Message;
        }
    }

    private async void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await AppDialog.ConfirmAsync(this, "Reset rules",
                    _packId.HasValue && _isOwner
                        ? $"Replace all {_rows.Count} rules with the defaults?"
                          + Environment.NewLine + Environment.NewLine
                          + "This syncs to everyone who shares this instance."
                        : $"Replace all {_rows.Count} rules with the defaults?",
                    "Reset", "Cancel", danger: true))
                return;

            _rows.Clear();
            foreach (var r in PackRuleService.DefaultRules())
                _rows.Add(new RuleRow(r));
            Save();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Couldn't reset the rules: " + ex.Message;
        }
    }

    /// <remarks>Closing runs the single save in <see cref="OnClosing"/>; saving here as well wrote
    /// the same list twice and pushed it to the server twice.</remarks>
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e) { Save(); base.OnClosing(e); }

    // ── in-window dialog host ────────────────────────────────────────────────

    public Task<bool> ShowConfirmAsync(string title, string message,
        string confirmText = "Yes", string cancelText = "Cancel", bool danger = false)
    {
        var overlay = new DialogOverlay();
        overlay.Configure(title, message, confirmText, cancelText, danger);
        return ShowOverlayAsync(overlay);
    }

    public Task ShowMessageAsync(string title, string message, string okText = "OK")
    {
        var overlay = new DialogOverlay();
        overlay.Configure(title, message, okText, null, false);
        return ShowOverlayAsync(overlay);
    }

    private async Task<bool> ShowOverlayAsync(DialogOverlay overlay)
    {
        DialogLayer.Children.Add(overlay);
        DialogLayer.Visibility = Visibility.Visible;
        try { return await overlay.Result; }
        finally
        {
            DialogLayer.Children.Remove(overlay);
            if (DialogLayer.Children.Count == 0) DialogLayer.Visibility = Visibility.Collapsed;
        }
    }
}

public sealed class RuleRow : INotifyPropertyChanged
{
    private string _pattern;
    private RuleAction _action;

    public RuleRow(PackRule r) { _pattern = r.Pattern; _action = r.Action; }

    public string Pattern
    {
        get => _pattern;
        set { _pattern = value; OnPropertyChanged(); }
    }

    public RuleAction Action
    {
        get => _action;
        set { _action = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
