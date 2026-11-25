using Avalonia.Controls;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// Play and Stop, the same from an instance card and from the instance page: the Windows instance
/// page's launch (PackDetailView.OnLaunch) without its Windows-only extras (the game window the
/// launcher hosts, the ReLauncher, test runs).
/// </summary>
/// <remarks>Messages go to the caller's <c>say</c> callback as plain sentences (true = an error).
/// What the launch prints goes to <see cref="InstanceLog"/>, which the instance page's Log tab
/// shows.</remarks>
public static class InstancePlay
{
    /// <summary>Launches <paramref name="pack"/>. False when it did not start (the reason has been
    /// said).</summary>
    public static async Task<bool> PlayAsync(PackDetail pack, Action<string, bool> say)
    {
        if (App.State.Instances.IsBusy(pack.Id))
        {
            say("Minecraft is already running from this instance.", false);
            return false;
        }
        if (pack.IsEmpty || string.IsNullOrEmpty(pack.MinecraftVersion))
        {
            say("This instance is empty, so there is no Minecraft to start.", true);
            return false;
        }

        if (App.State.MinecraftAccounts.Current is null && !await OfferMinecraftAccountAsync(say))
            return false;

        // Files a CurseForge import could not download: get them first, or play without them.
        if (!await ConfirmManualDownloadsAsync(pack.Id, pack.Name))
        {
            say("", false);
            return false;
        }

        say("Checking Java...", false);
        var (javaOk, javaMessage) = await LaunchService.CheckJavaAsync(
            pack.MinecraftVersion, App.State.Settings.GetJavaPathFor(pack.Id));
        if (!javaOk)
        {
            say(javaMessage, true);
            await MainWindow.Current.AlertAsync("Java Check", javaMessage);
            return false;
        }

        InstanceLog.Clear(pack.Id);
        var log = InstanceLog.ProgressFor(pack.Id);
        MinecraftLaunchHandle? launch = null;
        try
        {
            launch = App.State.Instances.BeginLaunch(pack.Id);
            say("Starting Minecraft...", false);
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);

            // Auto-update before launch, when it is on for this instance (Options > Update Before Play).
            if (pack.IsShared && App.State.Settings.GetAutoUpdateFor(pack.Id, pack.IsShared, pack.OwnerId)
                && pack.EffectivePermissions.HasFlag(PackPermissions.Download))
            {
                log.Report("Auto-update: pulling latest from server...");
                try { await App.State.Packs.DownloadSharedAsync(pack.Id, log, launch.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log.Report("Auto-update failed: " + ex.Message); }
            }

            var process = await App.State.Launcher.LaunchAsync(pack, log, launch.Token);
            if (!launch.Complete(process))
            {
                say("Launch cancelled.", false);
                InstanceLog.Append(pack.Id, "Launch cancelled; stopped Minecraft.");
                return false;
            }

            InstanceLog.Append(pack.Id, $"PID {process.Id} - running.");
            say("Minecraft is running.", false);
            return true;
        }
        catch (OperationCanceledException)
        {
            say("Launch cancelled.", false);
            InstanceLog.Append(pack.Id, "Launch cancelled.");
            ProgressHub.Clear(pack.Id);
            return false;
        }
        catch (Exception ex)
        {
            AppLog.LogError("launch", ex);
            say("Minecraft could not be started: " + Sentence(ex.Message), true);
            InstanceLog.Append(pack.Id, ex.ToString());
            return false;
        }
        finally
        {
            launch?.Dispose();
        }
    }

    /// <summary>Stops the game (or the launch on its way). True when asked to stop.</summary>
    public static bool Stop(Guid packId, Action<string, bool> say)
    {
        try
        {
            App.State.Instances.Stop(packId);
            say("Stopping Minecraft...", false);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.LogError("launch", ex);
            say("Minecraft could not be stopped: " + Sentence(ex.Message), true);
            return false;
        }
    }

    /// <summary>
    /// The download-by-hand card before a launch, when a CurseForge import left files that only its
    /// website hands out (the Windows ManualDownloadsCard.ConfirmLaunchAsync). True to play.
    /// </summary>
    public static async Task<bool> ConfirmManualDownloadsAsync(Guid packId, string packName)
    {
        int waiting;
        try { waiting = await Task.Run(() => App.State.ManualDownloads.Waiting(packId).Count); }
        catch (Exception ex)
        {
            // A list that can't be read never holds up the launch.
            AppLog.LogError("manual-downloads", ex);
            return true;
        }
        if (waiting == 0) return true;
        try { return await ManualDownloadsCard.ShowAsync(MainWindow.Current, packId, packName, forLaunch: true); }
        catch (Exception ex)
        {
            AppLog.LogError("manual-downloads", ex);
            return true;
        }
    }

    /// <summary>
    /// No Minecraft account yet: offers the Microsoft sign-in here (its device-code card comes up by
    /// itself) or the Account page. True once there is an account to play with.
    /// </summary>
    public static async Task<bool> OfferMinecraftAccountAsync(Action<string, bool> say)
    {
        var done = new TaskCompletionSource<int>();
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var accountPage = new Button { Content = "Account Page" };
        var signIn = new Button { Content = "Sign In", MinWidth = 90 };
        signIn.Classes.Add("primary");
        cancel.Click += (_, _) => done.TrySetResult(0);
        accountPage.Click += (_, _) => done.TrySetResult(1);
        signIn.Click += (_, _) => done.TrySetResult(2);
        var card = MessageCard.Build("Minecraft Account",
            "Playing needs a Minecraft account. Sign in with the Microsoft account that owns Minecraft.",
            cancel, accountPage, signIn);
        var choice = await MainWindow.Current.ShowCardAsync(card, done.Task, () => done.TrySetResult(0));

        if (choice == 1)
        {
            MainWindow.Current.Navigate(new AccountPage());
            return false;
        }
        if (choice != 2)
        {
            say("", false);
            return false;
        }

        say("Waiting for the Microsoft sign-in...", false);
        try
        {
            await App.State.MinecraftAccounts.AddMicrosoftAsync();
        }
        catch (OperationCanceledException)
        {
            say("The Microsoft sign-in was cancelled.", false);
            return false;
        }
        catch (Exception ex)
        {
            AppLog.LogError("account", ex);
            say("The Microsoft sign-in did not finish: " + Sentence(ex.Message), true);
            return false;
        }
        if (App.State.MinecraftAccounts.Current is null)
        {
            say("No Minecraft account was added.", true);
            return false;
        }
        say("", false);
        return true;
    }

    /// <summary>An exception message as the end of a sentence.</summary>
    public static string Sentence(string message)
    {
        var text = message.Trim();
        if (text.Length == 0) return "something went wrong.";
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
    }
}
