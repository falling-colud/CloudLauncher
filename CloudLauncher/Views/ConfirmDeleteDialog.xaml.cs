using System.Windows;

namespace CloudLauncher.Views;

public partial class ConfirmDeleteDialog : Window
{
    public ConfirmDeleteDialog(string packName, bool isOwner)
    {
        InitializeComponent();
        PackNameLabel.Text = packName;

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
