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
            case "planning-select":
            {
                // Shift-drag selection boxes and group drags with sections in them, through the view's own
                // code (reflection on the private handlers). Results are PASS/FAIL lines in the log.
                var shell = Program.MakeShell(1280, 820);
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
                shell.Show();
                Program.Pump(1500);
                var board = new CloudLauncher.Services.PlanBoard { Name = "Select", SectionMembershipSet = true };
                CloudLauncher.Services.PlanNode Add(string id, CloudLauncher.Services.PlanNodeKind kind, double x, double y,
                    double w, double h, string? section = null)
                {
                    var n = new CloudLauncher.Services.PlanNode { Id = id, Kind = kind, X = x, Y = y, W = w, H = h, Title = id, SectionId = section };
                    board.Nodes.Add(n);
                    return n;
                }
                var note = CloudLauncher.Services.PlanNodeKind.Note;
                var sec = CloudLauncher.Services.PlanNodeKind.Section;
                Add("secA", sec, 288, 48, 552, 216);
                Add("a1", note, 312, 96, 240, 48, "secA");
                Add("a2", note, 576, 96, 240, 48, "secA");
                Add("secB", sec, 288, 336, 552, 216);
                Add("b1", note, 312, 384, 240, 48, "secB");
                Add("loose", note, 24, 48, 216, 48);
                var plans = new CloudLauncher.Services.PackPlans { Boards = { board }, LastBoardId = board.Id };
                var file = System.IO.Path.Combine(App.State.Packs.GameDir(FakeWorld.Hearthstone), ".cloudlauncher", "plans.json");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                System.IO.File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(plans));
                App.State.ModPlans.Invalidate(FakeWorld.Hearthstone);
                var view = new CloudLauncher.Views.ModPlanView();
                var host = new Window
                {
                    Left = -24000, Top = -24000, Width = 1100, Height = 700, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Content = view,
                };
                host.Show();
                Program.Pump(800);
                view.Load(FakeWorld.Hearthstone, Array.Empty<CloudLauncher.Services.PackMod>(), shell);
                Program.Pump(1200);

                var t = typeof(CloudLauncher.Views.ModPlanView);
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                object? Call(string m, params object?[] a) => t.GetMethod(m, F)!.Invoke(view, a);
                var selected = (HashSet<string>)t.GetField("_selected", F)!.GetValue(view)!;
                var live = (CloudLauncher.Services.PlanBoard)t.GetField("_board", F)!.GetValue(view)!;
                CloudLauncher.Services.PlanNode Node(string id) => live.Nodes.Single(n => n.Id == id);
                void Check(string name, bool ok, string detail = "") => Program.Log($"{(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : detail)}");
                string Sel() => string.Join(",", selected.OrderBy(s => s));
                void Sweep(Point from, Point to)
                {
                    Call("ClearSelection");
                    Call("BeginMarquee", from);
                    Call("UpdateMarquee", to);
                    Call("EndMarquee");
                }

                // From empty canvas across the top of section A and into its first card.
                Sweep(new Point(250, 20), new Point(400, 120));
                Check("box from outside takes the section it reaches", selected.Contains("secA") && selected.Contains("a1"), Sel());
                Check("...and not what it didn't reach", !selected.Contains("a2") && !selected.Contains("secB"), Sel());
                // Starting inside section A's body: only its cards.
                Sweep(new Point(300, 160), new Point(830, 90));
                Check("box started inside a section leaves that section out", !selected.Contains("secA")
                    && selected.Contains("a1") && selected.Contains("a2"), Sel());
                // Everything: both sections, all cards.
                Sweep(new Point(10, 10), new Point(900, 600));
                Check("box over everything takes both sections", selected.SetEquals(new[] { "secA", "secB", "a1", "a2", "b1", "loose" }), Sel());

                // Dragging the loose card with that selection moves every card exactly once.
                var carried = (List<CloudLauncher.Services.PlanNode>)Call("CarriedWith", Node("loose"))!;
                Check("drag carries each other card once", carried.Count == 5 && carried.Select(n => n.Id).Distinct().Count() == 5,
                    string.Join(",", carried.Select(n => n.Id)));
                // Section A plus the loose card selected, dragging the section: its cards come too, once.
                selected.Clear(); selected.Add("secA"); selected.Add("loose");
                carried = (List<CloudLauncher.Services.PlanNode>)Call("CarriedWith", Node("secA"))!;
                Check("dragging a selected section brings the rest of the selection and its own cards",
                    carried.Select(n => n.Id).OrderBy(s => s).SequenceEqual(new[] { "a1", "a2", "loose" }), string.Join(",", carried.Select(n => n.Id)));

                // A real move through DragTo: everything shifts by the same whole cells, nothing twice.
                Sweep(new Point(10, 10), new Point(900, 600));
                var before = live.Nodes.ToDictionary(n => n.Id, n => (n.X, n.Y));
                var dragNode = Node("loose");
                t.GetField("_dragNode", F)!.SetValue(view, dragNode);
                t.GetField("_dragStart", F)!.SetValue(view, new Point(0, 0));
                t.GetField("_dragOrigX", F)!.SetValue(view, dragNode.X);
                t.GetField("_dragOrigY", F)!.SetValue(view, dragNode.Y);
                var carriedField = t.GetField("_dragCarried", F)!;
                var listType = carriedField.FieldType;
                var list = Activator.CreateInstance(listType)!;
                foreach (var n in (List<CloudLauncher.Services.PlanNode>)Call("CarriedWith", dragNode)!)
                    listType.GetMethod("Add")!.Invoke(list, new object[] { (n, n.X, n.Y) });
                carriedField.SetValue(view, list);
                Call("DragTo", new Point(48, 96));
                var moved = live.Nodes.Select(n => (n.Id, dx: n.X - before[n.Id].X, dy: n.Y - before[n.Id].Y)).ToList();
                Check("every card moved by the same amount", moved.Select(m => (m.dx, m.dy)).Distinct().Count() == 1 && moved[0].dx != 0,
                    string.Join(" ", moved.Select(m => $"{m.Id}:{m.dx},{m.dy}")));
                var up = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                    System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
                t.GetMethod("OnCardUp", F)!.Invoke(view, new object?[] { view, up });
                live = (CloudLauncher.Services.PlanBoard)t.GetField("_board", F)!.GetValue(view)!;
                Check("cards keep their sections after the group drop", Node("a1").SectionId == "secA" && Node("a2").SectionId == "secA"
                    && Node("b1").SectionId == "secB" && Node("loose").SectionId is null,
                    $"a1={Node("a1").SectionId} b1={Node("b1").SectionId} loose={Node("loose").SectionId}");
                Program.Pump(400);
                Program.Shoot(view, "planning-select", 1);
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
                    && page.FindName(Environment.GetEnvironmentVariable("CL_SETTINGS_TARGET") is { Length: > 0 } t ? t : "PixelFontOptions") is FrameworkElement options)
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
