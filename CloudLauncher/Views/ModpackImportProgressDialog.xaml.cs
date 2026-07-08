using System.IO;
using System.Windows;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

public partial class ModpackImportProgressDialog : Window
{
    private readonly string _path;

    public ModpackImportProgressDialog(string path, string suggestedName)
    {
        InitializeComponent();
        _path = path;
        NameBox.Text = suggestedName;
        Title = $"Import — {Path.GetFileName(path)}";
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Enter an instance name."; return; }

        ImportButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        Progress.Value = 0;
        CurrentProgress.Visibility = Visibility.Collapsed;
        CurrentProgressHeader.Visibility = Visibility.Collapsed;
        OverallProgressLabel.Text = "Starting import...";
        OverallProgressPercent.Text = "";
        CurrentProgressLabel.Text = "";
        CurrentProgressPercent.Text = "";
        LogBox.Text = "";
        var log = new Progress<string>(line => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }));
        var importProgress = new Progress<ImportProgress>(UpdateImportProgress);

        try
        {
            if (_path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                await App.State.ModpackImport.ImportMrpackAsync(_path, name, log, importProgress);
            else
                await App.State.ModpackImport.ImportCurseForgeZipAsync(_path, name, log, importProgress);

            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            CurrentProgress.IsIndeterminate = false;
            CurrentProgress.Value = 0;
            CurrentProgress.Visibility = Visibility.Collapsed;
            CurrentProgressHeader.Visibility = Visibility.Collapsed;
            OverallProgressLabel.Text = "Import complete";
            OverallProgressPercent.Text = "100%";
            StatusLabel.Text = "Import complete!";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Failed: " + ex.Message;
            LogBox.AppendText(ex.ToString() + Environment.NewLine);
        }
        finally { ImportButton.IsEnabled = true; }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdateImportProgress(ImportProgress info)
    {
        OverallProgressLabel.Text = info.TotalLabel;
        if (info.TotalFraction < 0)
        {
            Progress.IsIndeterminate = true;
            OverallProgressPercent.Text = "";
        }
        else
        {
            Progress.IsIndeterminate = false;
            Progress.Value = info.TotalFraction * 100;
            OverallProgressPercent.Text = $"{(int)(info.TotalFraction * 100)}%";
        }

        if (info.CurrentFraction < 0 && string.IsNullOrWhiteSpace(info.CurrentLabel))
        {
            CurrentProgress.IsIndeterminate = false;
            CurrentProgress.Value = 0;
            CurrentProgress.Visibility = Visibility.Collapsed;
            CurrentProgressHeader.Visibility = Visibility.Collapsed;
            CurrentProgressLabel.Text = "";
            CurrentProgressPercent.Text = "";
            return;
        }

        CurrentProgress.Visibility = Visibility.Visible;
        CurrentProgressHeader.Visibility = Visibility.Visible;
        CurrentProgressLabel.Text = string.IsNullOrWhiteSpace(info.CurrentLabel)
            ? "Current stage"
            : info.CurrentLabel;

        if (info.CurrentFraction < 0)
        {
            CurrentProgress.IsIndeterminate = true;
            CurrentProgressPercent.Text = "";
        }
        else
        {
            CurrentProgress.IsIndeterminate = false;
            CurrentProgress.Value = info.CurrentFraction * 100;
            CurrentProgressPercent.Text = $"{(int)(info.CurrentFraction * 100)}%";
        }
    }
}
