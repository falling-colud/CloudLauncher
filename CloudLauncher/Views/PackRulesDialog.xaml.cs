using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

public partial class PackRulesDialog : Window
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
    }

    private void Load()
    {
        _rows.Clear();
        foreach (var r in App.State.Rules.Load(_packRoot))
            _rows.Add(new RuleRow(r));
    }

    private void Save()
    {
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

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var pattern = NewPatternBox.Text.Trim();
        if (string.IsNullOrEmpty(pattern)) { StatusLabel.Text = "Enter a pattern."; return; }
        var action = NewActionBox.SelectedIndex >= 0 && NewActionBox.SelectedIndex < ActionValues.Length
            ? ActionValues[NewActionBox.SelectedIndex]
            : RuleAction.Local;

        _rows.Add(new RuleRow(new PackRule { Pattern = pattern, Action = action }));
        NewPatternBox.Text = "";
        StatusLabel.Text = "";
        Save();
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is RuleRow row)
        {
            // On a shared pack, deleting a rule immediately pushes to the server for every
            // collaborator, so confirm first to avoid a one-click destructive sync.
            if (_packId.HasValue && _isOwner &&
                MessageBox.Show(this, $"Remove the rule for '{row.Pattern}'? This syncs to all collaborators.",
                    "Remove rule", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _rows.Remove(row);
            Save();
        }
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "Replace all rules with the defaults?", "Reset rules",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _rows.Clear();
        foreach (var r in PackRuleService.DefaultRules())
            _rows.Add(new RuleRow(r));
        Save();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Save();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e) { Save(); base.OnClosing(e); }
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
