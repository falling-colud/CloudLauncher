using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using CloudLauncher.Views;
using static LinuxShots.Program;

namespace LinuxShots;

/// <summary>Scenarios for instances and the instance page: "instances", "instance", "create-instance",
/// and "instances-signed-out" (run that one with -SignedOut).</summary>
public static class ShotsInstances
{
    // The fake account's instances (tools/smoke/showcase/FakeWorld.cs and Seed.cs).
    private static readonly Guid Hearthstone = G(100), Skyline = G(101), Workshop = G(105);
    private static readonly Guid LocalInstance = Guid.Parse("5a5a5a5a-0000-4000-8000-00000000c10d");

    private static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");

    /// <summary>Runs scenario <paramref name="name"/> if it is one of this file's; false otherwise.</summary>
    public static bool TryRun(string name)
    {
        Action? scenario = name switch
        {
            "instances" => InstancesList,
            "instance" => OneInstance,
            "create-instance" => CreateCard,
            "instances-signed-out" => SignedOut,
            _ => null
        };
        if (scenario is null) return false;
        UseFixedLists();
        scenario();
        return true;
    }

    /// <summary>The version lists and Java installations come from here, so the harness never goes
    /// online and never starts a Java to ask its version.</summary>
    private static void UseFixedLists()
    {
        CreateInstanceCard.MinecraftVersionSource = _ => Task.FromResult(new List<string>
            { "1.21.4", "1.21.3", "1.21.1", "1.21", "1.20.6", "1.20.4", "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2" });
        CreateInstanceCard.LoaderVersionSource = (loader, mc, _) => Task.FromResult(loader switch
        {
            LoaderKind.Fabric => new List<string> { "0.16.9", "0.16.7", "0.16.5" },
            LoaderKind.Forge => new List<string> { "54.0.16", "54.0.12" },
            LoaderKind.NeoForge => mc == "1.21.4"
                ? new List<string> { "21.4.60-beta", "21.4.47-beta" }
                : new List<string> { "21.1.215", "21.1.200" },
            _ => new List<string>()
        });
        InstancePage.JavaInstallsSource = () => Task.FromResult<IReadOnlyList<LaunchService.JavaInstall>>(
        [
            new("/usr/lib/jvm/java-21-openjdk/bin/java", 21, false),
            new("/usr/lib/jvm/java-17-openjdk/bin/java", 17, false)
        ]);
    }

    // ── the list ─────────────────────────────────────────────────────────────

    private static void InstancesList()
    {
        // One instance with an icon of its own, so the icon path is on the picture too.
        WriteIcon(Skyline);
        Shell.NavigateToInstances();
        Pump(2500);
        Shoot("instances");

        // An import running into one instance: its card shows the bar with Pause and Stop.
        var import = PackJobs.Start(Workshop, PackJobKind.Download, "Tinker's Workshop");
        ProgressHub.Report(Workshop, 0.42, "Downloading mods 21/50", 0.6, "create-6.0.4.jar");
        FlushProgress();
        Pump(600);
        Shoot("instances-progress");
        PackJobs.Finish(import);
        ProgressHub.Clear(Workshop);
        Pump(300);

        // A launch on its way (registered only; no game starts): Play turns into Stop.
        using (App.State.Instances.BeginLaunch(Hearthstone))
        {
            Pump(400);
            Shoot("instances-launching");
        }
        Pump(300);

        // A search that finds nothing.
        if (Shell.CurrentPage is InstancesPage page && page.FindControl<TextBox>("SearchBox") is { } search)
        {
            search.Text = "lighthouse";
            Pump(400);
            Shoot("instances-no-match");
            search.Text = "";
            Pump(200);
        }

        // A narrow window: fewer columns, the search box gives way.
        Narrow(() => Shoot("instances-narrow"));

        // The card menu's Delete on an instance the account owns, up to its question (then Cancel).
        if (Shell.CurrentPage is InstancesPage list
            && Find<InstanceCard>(list).FirstOrDefault(c => c.Id == Workshop) is { } workshop)
        {
            typeof(InstancesPage).GetMethod("DeleteAsync", BindingFlags.NonPublic | BindingFlags.Instance)?
                .Invoke(list, [workshop]);
            Pump(500);
            Shoot("instances-delete");
            CloseCard();
        }
    }

    // ── one instance ─────────────────────────────────────────────────────────

    private static void OneInstance()
    {
        Shell.NavigateToInstances();
        Pump(1200);
        var page = new InstancePage(Hearthstone, "Hearthstone Valley");
        Shell.Navigate(page);
        Pump(2500);
        Shoot("instance");
        Narrow(() => Shoot("instance-narrow"));

        var tabs = page.FindControl<TabControl>("Tabs")!;
        tabs.SelectedIndex = 1;
        Pump(900);
        Shoot("instance-options");

        tabs.SelectedIndex = 2;
        Pump(900);
        Shoot("instance-log");

        var logs = page.FindControl<ListBox>("LogList")!;
        if (logs.Items.OfType<InstanceLogSource>().FirstOrDefault(r => r.Name == "latest.log") is { } latest)
        {
            logs.SelectedItem = latest;
            Pump(1200);
            Shoot("instance-log-file");
        }

        // What a launch prints, as the Game Output row shows it while the game runs.
        foreach (var line in new[]
                 {
                     "Launching pack 'Hearthstone Valley' (1.21.1, NeoForge)",
                     "Resolving Minecraft version...",
                     "Version: neoforge-21.1.215",
                     "Account: mara (online)",
                     "Selected Java: /usr/lib/jvm/java-21-openjdk/bin/java (Java 21)",
                     "Files ready. Starting Minecraft...",
                     "Minecraft started (PID 48213).",
                     "[minecraft] [21:04:12] [main/INFO] [net.neoforged.fml.loading.FMLLoader/]: Loading 212 mods",
                     "[minecraft] [21:04:19] [Render thread/INFO] [net.minecraft.client.Minecraft/]: Setting user: mara",
                     "[minecraft] [21:04:31] [Render thread/INFO] [net.minecraft.client.sounds.SoundEngine/]: Sound engine started"
                 })
            InstanceLog.Append(Hearthstone, line);
        logs.SelectedIndex = 0;
        Pump(900);
        Shoot("instance-log-game");

        // A transfer running for it: the header's bar, Pause and Stop in Play's place.
        tabs.SelectedIndex = 0;
        var job = PackJobs.Start(Hearthstone, PackJobKind.Sync, "Hearthstone Valley");
        ProgressHub.Report(Hearthstone, 0.63, "Downloading 214/340", 0.4, "sodium-neoforge-0.6.5.jar");
        FlushProgress();
        Pump(700);
        Shoot("instance-progress");
        PackJobs.Finish(job);
        ProgressHub.Clear(Hearthstone);
        Pump(300);

        // A launch on its way (registered only; no game starts): Play gives way to Cancel.
        using (App.State.Instances.BeginLaunch(Hearthstone))
        {
            ProgressHub.Report(Hearthstone, -1, "Installing files...");
            FlushProgress();
            Pump(400);
            Shoot("instance-launching");
            ProgressHub.Clear(Hearthstone);
        }
        Pump(300);

        // No Minecraft account yet: what Play offers.
        _ = InstancePlay.OfferMinecraftAccountAsync((_, _) => { });
        Pump(500);
        Shoot("instance-no-account");
        CloseCard();
    }

    // ── the create card ──────────────────────────────────────────────────────

    private static void CreateCard()
    {
        Shell.NavigateToInstances();
        Pump(1500);
        _ = CreateInstanceCard.ShowAsync(Shell);
        Pump(700);
        Shoot("create-instance");

        if (Find<CreateInstanceCard>(Shell).FirstOrDefault() is { } card)
        {
            card.FindControl<TextBox>("NameBox")!.Text = "Lantern Nights";
            card.FindControl<TextBox>("SummaryBox")!.Text = "Cosy survival with cooking, farming and far too many lanterns.";
            card.FindControl<ComboBox>("McVersionBox")!.SelectedIndex = 2;
            card.FindControl<ComboBox>("LoaderBox")!.SelectedIndex = 3;
            card.FindControl<ComboBox>("VisibilityBox")!.SelectedIndex = 1;
            Pump(700);
            Shoot("create-instance-filled");
        }
        CloseCard();
    }

    // ── signed out ───────────────────────────────────────────────────────────

    private static void SignedOut()
    {
        Shell.NavigateToInstances();
        Pump(2500);
        Shoot("instances-signed-out");

        _ = CreateInstanceCard.ShowAsync(Shell);
        Pump(700);
        Shoot("create-instance-signed-out");
        CloseCard();

        var page = new InstancePage(LocalInstance, "Weekend Survival");
        Shell.Navigate(page);
        Pump(2000);
        Shoot("instance-signed-out");

        // Its Options: no sharing here, and the step that brings it (sign in).
        page.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
        Pump(900);
        page.FindControl<ScrollViewer>("OptionsScroll")?.ScrollToEnd();
        Pump(400);
        Shoot("instance-signed-out-options");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>ProgressHub batches its reports on a DispatcherTimer, and this harness's pump runs the
    /// dispatcher's jobs but not its timers: hand the batch out now, as the timer would.</summary>
    private static void FlushProgress() =>
        typeof(ProgressHub).GetMethod("FlushPending", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);

    /// <summary>Runs <paramref name="shoot"/> with the window near its narrowest (MinWidth 820).</summary>
    private static void Narrow(Action shoot)
    {
        var width = Shell.Width;
        Shell.Width = 860;
        Pump(700);
        shoot();
        Shell.Width = width;
        Pump(500);
    }

    /// <summary>Closes the card on top the way Escape does (its own Cancel).</summary>
    private static void CloseCard()
    {
        var cancel = Find<Button>(Shell).LastOrDefault(b => b.IsEffectivelyVisible
                                                            && (b.Content as string) is "Cancel");
        cancel?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(300);
    }

    /// <summary>A small pixel-art icon in the instance's .cloudlauncher folder, where the launcher keeps
    /// an instance's own icon.</summary>
    private static void WriteIcon(Guid packId)
    {
        try
        {
            var path = App.State.PackAssets.IconPathFor(packId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                void Fill(string colour, double x, double y, double w, double h) =>
                    ctx.FillRectangle(new SolidColorBrush(Color.Parse(colour)), new Rect(x, y, w, h));
                Fill("#2E5C8A", 0, 0, 64, 40);       // sky
                Fill("#3D73A8", 0, 0, 64, 16);
                Fill("#F2C14E", 44, 8, 10, 10);      // sun
                Fill("#5E8C3A", 0, 40, 64, 8);       // grass
                Fill("#7A5536", 0, 48, 64, 16);      // dirt
                Fill("#9AA3AD", 12, 16, 6, 24);      // crane mast
                Fill("#9AA3AD", 12, 16, 34, 4);      // crane arm
                Fill("#4A4F55", 38, 20, 2, 12);      // cable
                Fill("#C9733C", 34, 32, 10, 8);      // load
                Fill("#3A3F45", 0, 44, 64, 2);       // rail
            }
            bitmap.Save(path);
            Log("icon " + path);
        }
        catch (Exception ex)
        {
            Log("icon not written: " + ex.Message);
        }
    }
}
