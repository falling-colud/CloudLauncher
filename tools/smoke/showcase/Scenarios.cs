using System.Windows;
using System.Windows.Controls;
using CloudLauncher;

namespace Showcase;

public static class Scenarios
{
    public static void Run(string name)
    {
        switch (name)
        {
            case "sharing": Sharing(); break;
            case "logs": Logs(); break;
            case "open": Open(); break;
            case "files":
            {
                // The instance page's Files tab (Hearthstone has logs and a crash report on disk).
                // The tab's loaders await Task.Run; without the dispatcher's context installed here they
                // resume on a pool thread, fail to touch their labels, and the tab reads "Measuring...".
                var shell = Program.MakeShell(1280, 820);
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
                shell.Show();
                Program.Pump(2500);
                shell.OpenPackDetail(FakeWorld.Hearthstone, "Hearthstone Valley");
                Program.Pump(3000);
                if (((Frame)shell.FindName("SideFrame")).Content is Page page && page.FindName("DetailTabs") is TabControl tabs)
                {
                    foreach (TabItem item in tabs.Items)
                        if (item.Header?.ToString() == "Files") { tabs.SelectedItem = item; break; }
                    Program.Pump(4000);
                    Program.Shoot((FrameworkElement)shell.Content, "files-tab", 1);
                    if (page.FindName("FilesTab") is TabItem { Content: FrameworkElement body })
                        Program.Shoot(body, "files-tab-body-2x", 2);
                }
                else Program.Log("no instance page found");
                shell.Hide();
                Program.Pump(300);
                break;
            }
            case "serve":
            {
                // Only the fake server and the seeded profile, for the real published exe to run against
                // (CL_PROFILE=showcase). Stops when a file called "stop" appears in the output folder.
                var stop = System.IO.Path.Combine(Program.Out, "stop");
                var until = DateTime.UtcNow.AddMinutes(15);
                Program.Log("serving");
                while (!System.IO.File.Exists(stop) && DateTime.UtcNow < until) Program.Pump(500);
                break;
            }
            case "instances":
            {
                // Instances page as cards, as a list, and back to cards (the grid must reflow).
                // The narrow window is its own scenario: a second MainWindow in one process dies hard.
                var shell = Program.MakeShell(1280, 820);
                shell.Show();
                Program.Pump(2500);
                if (shell.FindName("MainFrame") is Frame { Content: Page page })
                {
                    Program.Click(page, "LayoutCardsButton");
                    Program.Pump(900);
                    Program.Shoot((FrameworkElement)shell.Content, "instances-cards", 1);
                    Program.Click(page, "LayoutListButton");
                    Program.Pump(1200);
                    Program.Shoot((FrameworkElement)shell.Content, "instances-list", 1);
                    Program.Click(page, "LayoutCardsButton");
                    Program.Pump(1200);
                    Program.Shoot((FrameworkElement)shell.Content, "instances-cards-again", 1);
                }
                else Program.Log("no instances page found");
                shell.Hide();
                Program.Pump(300);
                break;
            }
            case "fontsample":
            {
                // Renders the launcher with a heading font from a folder, for comparing pixel fonts.
                // CL_SAMPLE_FONT_DIR, CL_SAMPLE_FONT_FAMILY, CL_SAMPLE_H1/H2/BRAND (sizes),
                // CL_SAMPLE_TEXT=1 to use it for all text too, CL_SAMPLE_TAG for the file name.
                string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";
                double Size(string k, double d) => double.TryParse(Env(k), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
                var dir = Env("CL_SAMPLE_FONT_DIR").TrimEnd('\\') + "\\";
                var family = new System.Windows.Media.FontFamily(new Uri(dir), "./#" + Env("CL_SAMPLE_FONT_FAMILY") + ", Segoe UI");
                var res = Application.Current.Resources;
                res["HeadingFont"] = family;
                res["BrandFont"] = family;
                res["H1FontSize"] = Size("CL_SAMPLE_H1", 27.5);
                res["H2FontSize"] = Size("CL_SAMPLE_H2", 22);
                res["BrandFontSize"] = Size("CL_SAMPLE_BRAND", 16.5);
                if (Env("CL_SAMPLE_TEXT") == "1") res["UiFont"] = family;
                var shell = Program.MakeShell(1100, 640);
                shell.Show();
                Program.Pump(2500);
                if (shell.FindName("MainFrame") is Frame { Content: Page page })
                {
                    Program.Click(page, "LayoutListButton");
                    Program.Pump(1200);
                }
                Program.Shoot((FrameworkElement)shell.Content, "font-" + Env("CL_SAMPLE_TAG"), 1);
                shell.Hide();
                Program.Pump(300);
                break;
            }
            case "planning":
            {
                // A planning board with notes at several sizes, including ones squeezed to their title.
                var shell = Program.MakeShell(1280, 820);
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
                shell.Show();
                Program.Pump(2000);
                var board = new CloudLauncher.Services.PlanBoard { Name = "Launch plan" };
                void Note(double x, double y, double w, double h, string title, string body = "")
                    => board.Nodes.Add(new CloudLauncher.Services.PlanNode
                    {
                        Kind = CloudLauncher.Services.PlanNodeKind.Note, X = x, Y = y, W = w, H = h, Title = title, Body = body,
                    });
                Note(48, 48, 240, 30, "Style and standards decided");
                Note(48, 144, 240, 44, "Pick a theme", "Cosy farming with a bit of tech. Keep the mod count under 250 and avoid anything that needs a separate launcher.");
                Note(336, 48, 240, 0, "Default note", "A note at its normal size.");
                Note(336, 240, 240, 60, "One line of text", "This description is longer than one line, so the second line should end in an ellipsis.");
                Note(624, 48, 144, 30, "Small");
                var plans = new CloudLauncher.Services.PackPlans { Boards = { board }, LastBoardId = board.Id };
                var file = System.IO.Path.Combine(App.State.Packs.GameDir(FakeWorld.Hearthstone), ".cloudlauncher", "plans.json");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                System.IO.File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(plans));
                var view = new CloudLauncher.Views.ModPlanView();
                var host = new Window
                {
                    Left = -24000, Top = -24000, Width = 1000, Height = 560, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Content = view,
                };
                host.Show();
                Program.Pump(800);
                view.Load(FakeWorld.Hearthstone, Array.Empty<CloudLauncher.Services.PackMod>(), shell);
                Program.Pump(1500);
                Program.Shoot(view, "planning-board", 1);
                host.Close();
                shell.Hide();
                Program.Pump(300);
                break;
            }
            case "settings-look":
            {
                var shell = Program.MakeShell(1280, 900);
                shell.Show();
                Program.Pump(2500);
                shell.OpenSettings();
                Program.Pump(2500);
                if (((Frame)shell.FindName("SideFrame")).Content is Page page
                    && Program.Find<ScrollViewer>(page).FirstOrDefault() is { } scroller
                    && page.FindName("PixelFontOptions") is FrameworkElement options)
                {
                    options.BringIntoView();
                    Program.Pump(800);
                }
                Program.Shoot((FrameworkElement)shell.Content, "settings-look", 1);
                shell.Hide();
                Program.Pump(300);
                break;
            }
            case "instances-narrow":
            {
                var shell = Program.MakeShell(900, 700);
                shell.Show();
                Program.Pump(2500);
                if (shell.FindName("MainFrame") is Frame { Content: Page page })
                {
                    Program.Click(page, "LayoutListButton");
                    Program.Pump(1200);
                    Program.Shoot((FrameworkElement)shell.Content, "instances-list-narrow", 1);
                    Program.Click(page, "LayoutCardsButton");
                    Program.Pump(600);
                }
                shell.Hide();
                Program.Pump(300);
                break;
            }
            case "config":
            {
                var shell = Program.MakeShell(1280, 820);
                shell.Show();
                Program.Pump(2500);
                Program.Click(shell, "NavConfigs");
                Program.Pump(3000);
                Program.Shoot((FrameworkElement)shell.Content, "config-heading", 1);
                shell.Hide();
                Program.Pump(300);
                break;
            }
            default: Program.Log("unknown scenario " + name); break;
        }
    }

    private static void Sharing()
    {
        var shell = Program.MakeShell(1280, 820);
        shell.Show();
        Program.Pump(2500);
        Program.Click(shell, "NavSharing");
        Program.Pump(4500);
        var root = (FrameworkElement)shell.Content;
        Program.Shoot(root, "sharing-overview", 1);

        // The same list, filtered to one direction and one kind, to check the filters work.
        if (shell.FindName("MainFrame") is Frame { Content: Page hub }
            && Program.Find<CloudLauncher.Views.SharingOverviewPanel>(hub).FirstOrDefault() is { } panel)
        {
            var direction = (ComboBox)panel.FindName("DirectionBox");
            direction.SelectedIndex = 2; // Shared with you
            Program.Pump(700);
            Program.Shoot(root, "sharing-with-you", 1);
            direction.SelectedIndex = 0;
            var kind = (ComboBox)panel.FindName("KindBox");
            kind.SelectedIndex = 5; // Shader packs
            Program.Pump(700);
            Program.Shoot(root, "sharing-shaders", 1);
            kind.SelectedIndex = 0;
            Program.Pump(500);
            Program.Shoot(panel, "sharing-panel-2x", 2);
            // The whole list, past the fold: the scroller's content renders at its full height.
            if (panel.FindName("ContentScroller") is ScrollViewer { Content: FrameworkElement all })
                Program.Shoot(all, "sharing-list-full", 1);
        }
        else Program.Log("no overview panel found");

        shell.Hide();
        Program.Pump(300);
    }

    /// <summary>Every family's Open, as the Overview's rows call it, one after another.</summary>
    private static void Open()
    {
        var shell = Program.MakeShell(1280, 820);
        shell.Show();
        Program.Pump(2500);
        Program.Click(shell, "NavSharing");
        Program.Pump(3500);
        var root = (FrameworkElement)shell.Content;
        var targets = new (CloudLauncher.Services.SharedFamily Family, Guid Id, string Name)[]
        {
            (CloudLauncher.Services.SharedFamily.World, FakeWorld.G(311), "Skyblock Start"),
            (CloudLauncher.Services.SharedFamily.Bundle, FakeWorld.G(330), "Valley Shaders"),
            (CloudLauncher.Services.SharedFamily.Mod, FakeWorld.G(303), "Compass HUD"),
            (CloudLauncher.Services.SharedFamily.ResourcePack, FakeWorld.G(321), "Ivy Leaves"),
            (CloudLauncher.Services.SharedFamily.Instance, FakeWorld.IvysGarden, "Ivy's Garden"),
        };
        foreach (var (family, id, name) in targets)
        {
            CloudLauncher.Views.SharingActions.Open(shell, family, id, name);
            Program.Pump(3500);
            Program.Shoot(root, "open-" + family.ToString().ToLowerInvariant(), 1);
            shell.CloseSidePanel();
            Program.Pump(900);
        }
        shell.Hide();
        Program.Pump(300);
    }

    private static void Logs()
    {
        var shell = Program.MakeShell(1280, 820);
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
        shell.Show();
        Program.Pump(2500);
        shell.OpenPackDetail(FakeWorld.Hearthstone, "Hearthstone Valley");
        Program.Pump(3000);
        if (((Frame)shell.FindName("SideFrame")).Content is Page page && page.FindName("DetailTabs") is TabControl tabs)
        {
            foreach (TabItem item in tabs.Items)
                if (item.Header?.ToString() == "Logs") { tabs.SelectedItem = item; break; }
            Program.Pump(2500);
            var root = (FrameworkElement)shell.Content;
            foreach (var width in new[] { 1600, 1280, 1040, 880 })
            {
                shell.Width = width;
                Program.Pump(1500);
                Program.Shoot(root, $"logs-{width}", 1);
            }
        }
        else Program.Log("no detail tabs");
        shell.Hide();
        Program.Pump(300);
    }
}
