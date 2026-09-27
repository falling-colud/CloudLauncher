using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// The card shown where an instance's sharing controls would be, for an instance that is on this PC
/// only: why they are missing, and the one step that brings them (sign in, or add it to the account).
/// </summary>
/// <remarks>Built in code so the instance page and File Management's Share tab can each drop one in
/// without a XAML file of its own.</remarks>
public sealed class LocalInstanceNote : SlateBorder
{
    private readonly MainWindow _shell;
    private readonly Guid _packId;
    private readonly Func<PackSummary, Task>? _onAdded;
    private readonly Button _action;
    private readonly TextBlock _status;

    /// <param name="onAdded">Run once the instance is on the account, to swap this card for the real
    /// controls.</param>
    public LocalInstanceNote(MainWindow shell, Guid packId, Func<PackSummary, Task>? onAdded = null)
    {
        _shell = shell;
        _packId = packId;
        _onAdded = onAdded;
        SetResourceReference(StyleProperty, "Card");
        Margin = new Thickness(0, 0, 0, 14);
        Padding = new Thickness(22, 18, 22, 18);

        var signedIn = App.State.Api.IsSignedIn;
        var title = new TextBlock { Text = "On this PC only", Margin = new Thickness(0, 0, 0, 4) };
        title.SetResourceReference(StyleProperty, "H3");

        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            Text = signedIn
                ? "This instance was made without a CloudLauncher account, so it lives on this PC only. Add it "
                  + "to your account to share it, invite people to it or sync it to your other PCs. Its files "
                  + "stay here until you choose to host them."
                : "This instance is not on a CloudLauncher account, so it can't be shared or synced. Playing, "
                  + "mods, worlds and everything else work the same. Sign in to add it to an account."
        };
        body.SetResourceReference(StyleProperty, "Muted");

        _action = new Button
        {
            Content = signedIn ? "Add to my account" : "Sign in",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _action.SetResourceReference(StyleProperty, "AccentButton");
        _action.Click += OnAction;

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        _status.SetResourceReference(StyleProperty, "Caption");

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(body);
        stack.Children.Add(_action);
        stack.Children.Add(_status);
        Child = stack;
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (!App.State.Api.IsSignedIn)
        {
            _shell.NavigateToLogin();
            return;
        }

        _action.IsEnabled = false;
        Say("Adding it to your account...", error: false);
        try
        {
            var added = await App.State.Api.AddLocalPackToAccountAsync(_packId);
            Say($"'{added.Name}' is on your account now.", error: false);
            // The Instances card still says "on this PC" otherwise, until its next refresh.
            _shell.AddOrUpdatePackList(added);
            if (_onAdded is not null) await _onAdded(added);
        }
        catch (ApiException ex)
        {
            AppLog.LogError(nameof(LocalInstanceNote), ex);
            Say("It could not be added: " + (ApiClient.ServerSentence(ex) ?? ex.Message), error: true);
            _action.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(LocalInstanceNote), ex);
            Say("It could not be added. The full error is in the launcher log.", error: true);
            _action.IsEnabled = true;
        }
    }

    private void Say(string text, bool error)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "TextSecondaryBrush");
        _status.Visibility = Visibility.Visible;
    }
}
