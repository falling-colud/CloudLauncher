using System.Windows;

namespace CloudLauncher.Views;

public partial class ConfirmDeleteDialog : Window
{
    /// <param name="isLocal">The instance is on this PC only, so deleting it removes its files too.</param>
    public ConfirmDeleteDialog(string packName, bool isOwner, bool isLocal = false)
    {
        InitializeComponent();
        PackNameLabel.Text = packName;

        if (isLocal)
        {
            BodyLabel.Text = "This instance is only on this PC, so deleting it deletes its files: mods, worlds, "
                             + "settings and everything else in its folder.";
            FilesNote.Text = "The folder goes to the Recycle Bin, so it can be restored from there.";
            return;
        }

        if (isOwner) return;

        Title = "Remove instance";
        HeaderLabel.Text = "Remove this instance?";
        BodyLabel.Text =
            "This removes the pack from your instance list. The original pack and other users are not affected.";
        DeleteButton.Content = "Remove instance";
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
