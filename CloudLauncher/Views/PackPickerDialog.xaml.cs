using System.Windows;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class PackPickerDialog : Window
{
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
        PackList.ItemsSource = packs
            .OrderBy(p => p.Name)
            .Select(p => new PackPickerRow(p))
            .ToList();
        PackList.MouseDoubleClick += (_, _) => { if (PackList.SelectedItem is PackPickerRow) OnApply(this, null!); };
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (PackList.SelectedItem is PackPickerRow row)
        {
            SelectedPackId = row.Id;
            DialogResult = true;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
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
}
