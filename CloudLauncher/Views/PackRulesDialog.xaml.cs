using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <remarks>
/// This window is modal, so it hosts its own <see cref="IDialogHost"/> overlay; a confirmation drawn
/// on the main window would appear behind it and couldn't be clicked.
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
        _ = RecountAsync();
    }

    /// <summary>
    /// The instance's live files, for the per-rule match counts and the path tester.
    /// </summary>
    /// <remarks>Derived from the pack root rather than <see cref="PackFolderService"/>, since this
    /// dialog also opens for local instances that have no pack id.</remarks>
    private string GameDir => System.IO.Path.Combine(_packRoot, "game");

    /// <summary>
    /// Fills in how many files each rule actually wins.
    /// </summary>
    /// <remarks>Counted per rule object (<see cref="PackRuleService.Match"/> returns the winning
    /// instance), so two rules with the same pattern text stay separate. Shows an ellipsis until the
    /// walk returns, since a premature zero would mean "matches nothing".</remarks>
    private async Task RecountAsync()
    {
        foreach (var row in _rows) row.MatchLabel = "...";

        var gameDir = GameDir;
        if (!Directory.Exists(gameDir))
        {
            foreach (var row in _rows) row.MatchLabel = "";
            StatusLabel.Text = "This instance has no game folder yet, so there is nothing to count against.";
            return;
        }

        var snapshot = _rows.Select(r => new PackRule { Pattern = r.Pattern, Action = r.Action }).ToList();

        try
        {
            var (counts, unmatched, total) = await Task.Run(() =>
            {
                var map = new Dictionary<PackRule, int>();
                var files = App.State.Packs.ListRelativeFiles(gameDir);
                var none = 0;
                foreach (var rel in files)
                {
                    var match = App.State.Rules.Match(rel, snapshot);
                    if (match.Rule is null) { none++; continue; }
                    map[match.Rule] = map.GetValueOrDefault(match.Rule) + 1;
                }
                return (snapshot.Select(r => map.GetValueOrDefault(r)).ToList(), none, files.Count);
            });

            for (var i = 0; i < _rows.Count && i < counts.Count; i++)
                _rows[i].MatchLabel = counts[i] == 0 ? "none" : counts[i].ToString("N0");

            var note = unmatched > 0
                ? $"{total:N0} file(s) in this instance · {unmatched:N0} match no rule and stay on this PC."
                : $"{total:N0} file(s) in this instance · every one is covered by a rule.";
            // The read-only notice is the more important of the two, so the counts join it rather
            // than replace it.
            StatusLabel.Text = IsReadOnlyForMe
                ? "File rules for a hosted instance are set by its owner. " + note
                : note;
        }
        catch (Exception ex)
        {
            AppLog.LogError("rules.count", ex);
            foreach (var row in _rows) row.MatchLabel = "";
            StatusLabel.Text = "Could not read the instance to count matches - see the Logs tab.";
        }
    }

    /// <summary>Answers "what happens to this exact path" against the rules as they stand now.</summary>
    private void OnTestPathChanged(object sender, TextChangedEventArgs e)
    {
        var path = TestPathBox.Text.Trim().Replace('\\', '/').TrimStart('/');
        if (path.Length == 0) { TestResultLabel.Text = ""; return; }

        var rules = _rows.Select(r => new PackRule { Pattern = r.Pattern, Action = r.Action }).ToList();
        var match = App.State.Rules.Match(path, rules);

        if (match.Rule is null)
        {
            TestResultLabel.Text = "No rule matches. It stays on this PC and is never uploaded.";
            return;
        }

        var outcome = match.Action switch
        {
            RuleAction.Shared => "uploaded with this instance",
            RuleAction.Ignored => "never uploaded and never downloaded",
            _ => "kept on this PC"
        };
        var privately = PrivateAssetPolicy.IsPrivate(path, App.State.Settings)
            ? " Held back from upload anyway: it is a private path."
            : "";
        TestResultLabel.Text = $"'{match.Rule.Pattern}' wins > {outcome}.{privately}";
    }

    /// <summary>True when edits here would only ever be written to this PC.</summary>
    /// <remarks>On a hosted pack the rules belong to the owner: only <see cref="PushToServerAsync"/>
    /// reaches other collaborators, and the server refuses it from anyone else. So for collaborators
    /// the grid is locked rather than saving changes the next sync would overwrite.</remarks>
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

        // With two rules for the same pattern the earlier one always wins, so replace rather than add
        // a dead duplicate.
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
        _ = RecountAsync();
    }

    /// <summary>An edited pattern matches a different set of files, so the counts beside it are
    /// wrong the moment the cell is committed.</summary>
    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => _ = RecountAsync()), DispatcherPriority.Background);

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
            _ = RecountAsync();
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
            _ = RecountAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Couldn't reset the rules: " + ex.Message;
        }
    }

    /// <remarks>Closing runs the save in <see cref="OnClosing"/>, so don't save here too.</remarks>
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
    private string _matchLabel = "...";

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

    /// <summary>How many of the instance's files this rule wins, as text: a number, "none", "..."
    /// while it is still being counted, or empty when there is nothing to count against.</summary>
    public string MatchLabel
    {
        get => _matchLabel;
        set { _matchLabel = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
