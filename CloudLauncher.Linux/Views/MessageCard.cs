using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CloudLauncher.Views;

/// <summary>A question or a message as a card over the page (the Windows launcher's AppDialog).</summary>
public static class MessageCard
{
    public static async Task<bool> ConfirmAsync(MainWindow shell, string title, string message,
        string yes = "OK", string no = "Cancel", bool danger = false)
    {
        var done = new TaskCompletionSource<bool>();
        var yesButton = new Button { Content = yes, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        yesButton.Classes.Add(danger ? "destructive" : "primary");
        var noButton = new Button { Content = no, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        yesButton.Click += (_, _) => done.TrySetResult(true);
        noButton.Click += (_, _) => done.TrySetResult(false);
        var card = Build(title, message, noButton, yesButton);
        return await shell.ShowCardAsync(card, done.Task, () => done.TrySetResult(false));
    }

    public static async Task AlertAsync(MainWindow shell, string title, string message)
    {
        var done = new TaskCompletionSource<bool>();
        var ok = new Button { Content = "OK", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("primary");
        ok.Click += (_, _) => done.TrySetResult(true);
        await shell.ShowCardAsync(Build(title, message, ok), done.Task, () => done.TrySetResult(true));
    }

    /// <summary>A card with a title, a wrapped message and buttons on the right.</summary>
    public static Border Build(string title, string message, params Button[] buttons)
    {
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
        heading.Classes.Add("h2");
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
        body.Classes.Add("muted");
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new(0, 20, 0, 0)
        };
        foreach (var b in buttons) row.Children.Add(b);
        var card = new Border
        {
            Width = 460,
            Child = new StackPanel { Children = { heading, body, row } }
        };
        card.Classes.Add("dialog");
        return card;
    }
}
