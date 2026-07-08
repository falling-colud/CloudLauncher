using System.Windows;

namespace CloudLauncher;

/// <summary>A window that can host the in-window modal dialog overlay.</summary>
public interface IDialogHost
{
    Task<bool> ShowConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "Cancel", bool danger = false);
    Task ShowMessageAsync(string title, string message, string okText = "OK");
}

/// <summary>
/// Themed in-window dialogs. Routes to the owning window's <see cref="IDialogHost"/> overlay; if
/// none is available (e.g. a tool window), falls back to a plain OS message box so callers never break.
/// </summary>
public static class AppDialog
{
    public static Task<bool> ConfirmAsync(Window? owner, string title, string message,
        string confirmText = "Yes", string cancelText = "Cancel", bool danger = false)
    {
        if (Host(owner) is { } host)
            return host.ShowConfirmAsync(title, message, confirmText, cancelText, danger);

        var result = MessageBox.Show(owner, message, title, MessageBoxButton.YesNo,
            danger ? MessageBoxImage.Warning : MessageBoxImage.Question);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public static Task MessageAsync(Window? owner, string title, string message, string okText = "OK")
    {
        if (Host(owner) is { } host)
            return host.ShowMessageAsync(title, message, okText);

        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    private static IDialogHost? Host(Window? owner) =>
        owner as IDialogHost ?? Application.Current?.MainWindow as IDialogHost;
}
