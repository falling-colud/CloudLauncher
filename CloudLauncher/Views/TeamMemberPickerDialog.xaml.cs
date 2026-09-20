using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CloudLauncher.Views;

/// <summary>
/// A searchable "which member?" picker, built the same way as <see cref="PackPickerDialog"/>: type
/// two letters, press Enter, done.
/// </summary>
/// <remarks>
/// Ownership transfer is the reason this exists. The alternative — "right-click the row you want and
/// choose Make owner" — works only once the person has already found the row, and a team with twenty
/// members is a scroll. Taking the choice inside the action keeps "transfer ownership" a single
/// gesture from the team's own context menu, where someone looking for it will start.
/// </remarks>
public partial class TeamMemberPickerDialog : Window
{
    private readonly List<TeamMemberRow> _all;

    /// <summary>The chosen member, or null when the dialog was cancelled.</summary>
    public TeamMemberRow? Selected { get; private set; }

    public TeamMemberPickerDialog(
        IReadOnlyList<TeamMemberRow> members,
        string title,
        string description,
        string actionText = "Choose")
    {
        InitializeComponent();
        Title = title;
        TitleLabel.Text = title;
        DescriptionLabel.Text = description;
        ApplyButton.Content = actionText;
        _all = members.ToList();
        ApplyFilter();
        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>Rebinds to the rows matching the search box and selects the first, so Enter alone
    /// picks the obvious answer.</summary>
    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim();
        var rows = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(r => r.Username.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        MemberList.ItemsSource = rows;
        MemberList.SelectedIndex = rows.Count > 0 ? 0 : -1;
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>Down-arrow moves from the search box into the list so a filtered set can be walked
    /// from the keyboard.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || MemberList.Items.Count == 0) return;
        if (MemberList.SelectedIndex < 0) MemberList.SelectedIndex = 0;
        MemberList.UpdateLayout();
        (MemberList.ItemContainerGenerator.ContainerFromIndex(MemberList.SelectedIndex) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    /// <summary>Double-click picks the row under the pointer — not whatever happens to be selected,
    /// which is what a double-click on the empty space below the list would otherwise mean.</summary>
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

    private void Accept()
    {
        if (MemberList.SelectedItem is not TeamMemberRow row) return;
        Selected = row;
        DialogResult = true;
        Close();
    }
}
