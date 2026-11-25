using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>
/// The Microsoft sign-in code: the user opens microsoft.com/link (here or on a phone), types the code and
/// signs in there; the card closes by itself once the launcher has the account.
/// </summary>
public static class DeviceCodeCard
{
    /// <summary>Puts the card up and returns at once; it closes when the sign-in finishes either way, or
    /// when the user cancels (which cancels <paramref name="cancel"/>).</summary>
    public static void Show(MainWindow shell, DeviceCodePrompt prompt, CancellationTokenSource cancel)
    {
        var closed = new TaskCompletionSource<bool>();
        void Close() => Dispatcher.UIThread.Post(() => closed.TrySetResult(true));
        DeviceCodeSignIn.Finished += Close;

        var title = new TextBlock { Text = "Sign in with Microsoft" };
        title.Classes.Add("h2");
        var intro = new TextBlock
        {
            Text = "Open the Microsoft page below in any browser, on this computer or your phone, enter the code "
                   + "and sign in with the account that owns Minecraft. The launcher carries on by itself.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 10, 0, 0)
        };
        intro.Classes.Add("muted");

        var code = new TextBlock
        {
            Text = prompt.UserCode,
            FontSize = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            LetterSpacing = 4
        };
        code.Classes.Add("h1");
        var codeBox = new Border { Padding = new(18, 14), Margin = new(0, 18, 0, 6), Child = code };
        codeBox.Classes.Add("well");

        var link = new TextBlock
        {
            Text = prompt.VerificationUri,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        link.Classes.Add("caption");

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
        status.Classes.Add("caption");

        var open = new Button { Content = "Open the page and copy the code" };
        open.Classes.Add("primary");
        open.Click += async (_, _) =>
        {
            await CopyAsync(shell, prompt.UserCode);
            status.Text = SafeLaunch.OpenUrl(prompt.VerificationUri)
                ? "The code is copied: paste it on the page that opened."
                : "Your browser could not be opened. Go to " + prompt.VerificationUri + " and enter the code.";
        };
        var copy = new Button { Content = "Copy code" };
        copy.Click += async (_, _) =>
        {
            await CopyAsync(shell, prompt.UserCode);
            status.Text = "Copied.";
        };
        var cancelButton = new Button { Content = "Cancel" };
        cancelButton.Click += (_, _) => { cancel.Cancel(); closed.TrySetResult(false); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new(0, 18, 0, 0),
            Children = { cancelButton, copy, open }
        };

        var card = new Border
        {
            Width = 480,
            Child = new StackPanel { Children = { title, intro, codeBox, link, status, buttons } }
        };
        card.Classes.Add("dialog");

        // A timer line, so a code that is about to run out says so.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var left = prompt.ExpiresAt - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) { status.Text = "The code expired. Cancel and start the sign-in again."; return; }
            if (string.IsNullOrEmpty(status.Text) || status.Text.StartsWith("The code works for", StringComparison.Ordinal))
                status.Text = $"The code works for {(int)left.TotalMinutes}:{left.Seconds:00} more.";
        };
        timer.Start();

        _ = Run();

        async Task Run()
        {
            try
            {
                await shell.ShowCardAsync(card, closed.Task, () => { cancel.Cancel(); closed.TrySetResult(false); });
            }
            finally
            {
                timer.Stop();
                DeviceCodeSignIn.Finished -= Close;
            }
        }
    }

    private static async Task CopyAsync(Control anchor, string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(anchor)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) { AppLog.Log("account", "Could not copy the sign-in code: " + ex.Message); }
    }
}
