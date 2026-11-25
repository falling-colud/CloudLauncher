using Avalonia.Controls;
using CloudLauncher;
using CloudLauncher.Views;
using static LinuxShots.Program;

namespace LinuxShots;

/// <summary>The pages to shoot. Add a case per page or state worth looking at.</summary>
public static class Scenarios
{
    public static void Run(string name)
    {
        switch (name)
        {
            case "shell":
                Shoot("shell");
                break;
            case "signin":
                Shell.Navigate(new SignInPage(), clearHistory: true);
                Pump(800);
                Shoot("signin");
                break;
            case "device-code":
                DeviceCodeCard.Show(Shell, new DeviceCodePrompt("ABCD-EFGH", "https://www.microsoft.com/link",
                    DateTimeOffset.UtcNow.AddMinutes(15)), new CancellationTokenSource());
                Pump(800);
                Shoot("device-code");
                CloseTopCard();
                break;
            case "confirm":
                _ = Shell.ConfirmAsync("Delete instance?", "Weekend Survival goes to the trash with its worlds and mods.", "Delete", danger: true);
                Pump(600);
                Shoot("confirm");
                CloseTopCard();
                break;
            default:
                // Each area of the launcher keeps its own scenarios in its own file.
                if (!ShotsInstances.TryRun(name) && !ShotsMods.TryRun(name) && !ShotsAccount.TryRun(name))
                    Log("unknown scenario " + name);
                break;
        }
    }

    /// <summary>Escape on the window: the card on top cancels, so the next scenario starts clean.</summary>
    private static void CloseTopCard()
    {
        Shell.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.Escape
        });
        Pump(400);
        if (Shell.IsCardOpen) Log("FAIL a card is still open after Escape");
    }

    /// <summary>The scenario list, for the log.</summary>
    public static readonly string[] Names = ["shell", "signin", "device-code", "confirm"];
}
