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
/// Used from seven places (Mods, Worlds, the world browser, Resource packs, the resource-pack browser,
/// Shaders and the Minecraft defaults panel), which is why the constructor takes its wording as
/// arguments rather than knowing any of them: every caller asks the same question about a different
/// noun. The rows it binds are a projection, not the <see cref="PackSummary"/> objects themselves, so
/// the filter can match on the version label the user can actually see.
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
    /// The selection is what makes the dialog work by keyboard: Apply is the default button, so Enter
    /// reaches it from anywhere including the search box — but <see cref="Accept"/> only closes when
    /// something is selected, and this dialog used to open with nothing selected, so Enter and a click
    /// on Apply both did nothing at all until the user thought to click a row. Selecting the first
    /// match after every filter means the obvious gesture (type two letters, press Enter) picks the
    /// obvious instance.
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
        // The container only exists once the list has been laid out; on a freshly filtered list it has
        // not been, so ask for it after the current layout pass rather than failing silently.
        PackList.UpdateLayout();
        (PackList.ItemContainerGenerator.ContainerFromIndex(PackList.SelectedIndex) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// Double-clicking a row picks it. The hit test matters: the list now opens with a row selected,
    /// so a double-click on the empty space below the last row would otherwise apply whatever happened
    /// to be selected — a destination the user never pointed at.
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

    /// <summary>Takes the selected row as the answer and closes. A no-op when the filter matched
    /// nothing, which is the one case where there is genuinely nothing to apply.</summary>
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

    /// <summary>Whether this row survives the search box. Matches the version label as well as the
    /// name, because "1.21.1" or "neoforge" is how someone with four copies of a pack tells them
    /// apart — those are the only words on the row, and both are visible.</summary>
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || VersionLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
}
