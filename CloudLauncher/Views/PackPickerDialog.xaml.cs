using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The launcher's shared "which instance?" picker: a searchable list of packs that hands back the one
/// the user chose in <see cref="SelectedPackId"/>, or nothing if they cancelled.
/// </summary>
/// <remarks>
/// Used from several pages, so callers pass in the wording. Rows are a projection of
/// <see cref="PackSummary"/> so the filter can match the version label shown on screen.
/// </remarks>
public partial class PackPickerDialog : Window
{
    /// <summary>All rows, in display order. The ListBox is bound to a filtered view of this.</summary>
    private readonly List<PackPickerRow> _all;

    public Guid? SelectedPackId { get; private set; }

    public PackPickerDialog(
        IReadOnlyList<PackSummary> packs,
        string title = "Apply defaults to...",
        string description = "Pick an instance - the options.txt inside its game folder will be merged with the launcher defaults.",
        string actionText = "Apply")
    {
        InitializeComponent();
        TitleLabel.Text = title;
        DescriptionLabel.Text = description;
        ApplyButton.Content = actionText;
        _all = packs
            .OrderBy(p => p.Name)
            .Select(p => new PackPickerRow(p))
            .ToList();
        ApplyFilter();
        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>
    /// Rebinds the list to the rows matching the search box, and puts the selection back on the first
    /// of them.
    /// </summary>
    /// <remarks>
    /// Apply is the default button but <see cref="Accept"/> needs a selection, so this lets the user type
    /// a few letters and press Enter.
    /// </remarks>
    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim();
        var rows = string.IsNullOrEmpty(q) ? _all : _all.Where(r => r.Matches(q)).ToList();
        PackList.ItemsSource = rows;
        PackList.SelectedIndex = rows.Count > 0 ? 0 : -1;
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();
        ApplyFilter();
    }

    private void OnSearchFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdatePlaceholder();

    private void UpdatePlaceholder() =>
        SearchPlaceholder.Visibility =
            string.IsNullOrEmpty(SearchBox.Text) && !SearchBox.IsKeyboardFocused
                ? Visibility.Visible
                : Visibility.Collapsed;

    /// <summary>
    /// Down-arrow moves from the search box into the list, so a filtered-down set can be walked
    /// without reaching for the mouse. Everything else is left to the TextBox.
    /// </summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down) return;
        if (PackList.Items.Count == 0) return;
        if (PackList.SelectedIndex < 0) PackList.SelectedIndex = 0;
        // The item container doesn't exist until the filtered list has been laid out.
        PackList.UpdateLayout();
        (PackList.ItemContainerGenerator.ContainerFromIndex(PackList.SelectedIndex) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// Double-clicking a row picks it. Clicks on empty space are ignored: the list always has a
    /// selection, so they would apply a row the user never pointed at.
    /// </summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem) { Accept(); return; }
            if (d is ListBox) return;
        }
    }

    private void OnApply(object sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    /// <summary>Takes the selected row as the answer and closes. Does nothing when the filter matched
    /// nothing.</summary>
    private void Accept()
    {
        if (PackList.SelectedItem is not PackPickerRow row) return;
        SelectedPackId = row.Id;
        DialogResult = true;
        Close();
    }
}

public sealed class PackPickerRow
{
    public Guid Id { get; }
    public string Name { get; }
    public string VersionLabel { get; }
    public PackPickerRow(PackSummary p)
    {
        Id = p.Id;
        Name = p.Name;
        VersionLabel = p.IsEmpty
            ? "Empty"
            : p.Loader == LoaderKind.None
                ? $"MC {p.MinecraftVersion}"
                : $"MC {p.MinecraftVersion} · {p.Loader}";
    }

    /// <summary>Whether this row matches the search box. Checks the version label as well as the name,
    /// so copies of a pack can be told apart by "1.21.1" or "neoforge".</summary>
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || VersionLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
}
