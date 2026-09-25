using System.ComponentModel;
using System.Windows;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// A window around <see cref="FileEditorPanel"/>: the standalone way to edit an instance's own files.
/// </summary>
/// <remarks>
/// <para>The editor itself is <see cref="FileEditorPanel"/>, so the File Management page can host the
/// same control in a tab. This window only adds the window parts: one window per instance, the dialog
/// overlay for its own confirmations, and the unsaved-changes prompt on close.</para>
/// <para><c>ConfigHubView.HookEditorClose</c> finds this window by its title
/// <c>"Edit files · {packName}"</c> and hooks <see cref="Window.Closed"/> to re-scan the instance, so
/// keep that title format.</para>
/// </remarks>
public partial class FileEditorWindow : Window, IDialogHost
{
    /// <summary>Set once the unsaved-changes prompt has been answered, so our own second
    /// <see cref="Window.Close"/> goes straight through.</summary>
    private bool _forceClose;

    /// <summary>One editor window per instance: opening it twice would let two buffers of the same
    /// file overwrite each other.</summary>
    private static readonly Dictionary<Guid, FileEditorWindow> Instances = new();

    public static void Open(Window? owner, Guid packId, string packName)
    {
        if (Instances.TryGetValue(packId, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new FileEditorWindow(packId, packName) { Owner = owner };
        Instances[packId] = window;
        window.Closed += (_, _) => Instances.Remove(packId);
        window.Show();
    }

    /// <summary>Opens the editor with <paramref name="fullPath"/> loaded. Used when a file is
    /// double-clicked on the instance's Files tab.</summary>
    public static void OpenFileFor(Window? owner, Guid packId, string packName, string fullPath)
    {
        Open(owner, packId, packName);
        if (Instances.TryGetValue(packId, out var window)) window.OpenFile(fullPath);
    }

    public FileEditorWindow(Guid packId, string packName)
    {
        InitializeComponent();
        Title = $"Edit files · {packName}";
        Panel.Initialize(packId, packName);
        Closing += OnClosingWindow;
    }

    /// <summary>Brings a file up in this window's editor.</summary>
    public void OpenFile(string fullPath) => Panel.OpenFile(fullPath);

    private async void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_forceClose) return;
        try
        {
            if (!Panel.HasUnsavedChanges) return;

            // The overlay can't be awaited inside a Closing handler, so cancel this close and close
            // again once the prompt has been answered.
            e.Cancel = true;
            if (!await Panel.ConfirmCloseAsync()) return;

            _forceClose = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.LogError("editor", ex);
        }
    }

    // ── in-window dialogs ────────────────────────────────────────────────────

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
