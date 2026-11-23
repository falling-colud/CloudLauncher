using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using CloudLauncher.Views;

namespace Showcase;

/// <summary>
/// The boards' Discord tick (ModPlanView.Discord.cs), through the real view against the fake server's
/// Discord channels: no tick without a channel; a tick per board; unticking (saved, sent to the server,
/// marked in the rail, never undone by Ctrl+Z); a change arriving from elsewhere; a channel showing one
/// board; a view-only account; a copy of a kept-out board; and the Show in Discord card's lines.
/// Results are PASS/FAIL lines in the log; the last line counts the failures.
/// </summary>
public static class DiscordScenario
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private static ModPlanView _view = null!;
    private static int _fails;

    private static object? Call(string m, params object?[] a) =>
        typeof(ModPlanView).GetMethods(F).Single(x => x.Name == m && x.GetParameters().Length == a.Length).Invoke(_view, a);

    /// <summary>Pumps the dispatcher until an async method the view started has finished.</summary>
    private static void Await(object? task)
    {
        var t = (Task)task!;
        for (var i = 0; i < 200 && !t.IsCompleted; i++) Program.Pump(50);
        t.GetAwaiter().GetResult();
    }

    private static T Field<T>(string f) => (T)typeof(ModPlanView).GetField(f, F)!.GetValue(_view)!;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Program.Log($"{(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : detail)}");
    }

    private static Guid Pack => FakeWorld.Hearthstone;
    private static PlanBoard Board => Field<PlanBoard>("_board");
    private static PlanBoard Named(string id) => App.State.ModPlans.Boards(Pack).Single(b => b.Id == id);
    private static CheckBox Toggle => (CheckBox)_view.FindName("DiscordToggle");
    private static bool Up => Toggle.Visibility == Visibility.Visible;
    private static string Status => ((TextBlock)_view.FindName("PlanStatus")).Text;

    private static string TipText(FrameworkElement e) =>
        e.ToolTip is ToolTip { Content: TextBlock t } ? t.Text : e.ToolTip?.ToString() ?? "";

    private static PackDiscordLinkDto Link(string? boardId, string label = "#modpack-planning") => new(
        Guid.NewGuid(), "bot", "active", boardId, null, "guild", "chan", label, null, null,
        "http://127.0.0.1/plans/view/x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    /// <summary>What the fake server answers from now on, and the view asked again (as after the card).</summary>
    private static void SetLinks(params PackDiscordLinkDto[] links)
    {
        lock (FakeWorld.DiscordLinks) FakeWorld.DiscordLinks[Pack] = links.ToList();
        Await(Call("LoadDiscordLinksAsync", true));
        Program.Pump(100);
    }

    private static void Click(CheckBox box, bool value)
    {
        box.IsChecked = value;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, box));
        Program.Pump(200);
    }

    private static List<string> Texts(DependencyObject root)
    {
        var found = new List<string>();
        void Walk(object node)
        {
            if (node is TextBlock t && !string.IsNullOrEmpty(t.Text)) found.Add(t.Text);
            if (node is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        Walk(root);
        return found;
    }

    /// <summary>The boards the rail marks as kept out of Discord.</summary>
    private static List<string> Marked() =>
        Field<List<(FrameworkElement Row, PlanBoard Board)>>("_railRows")
            .Where(r => Texts(r.Row).Contains("")).Select(r => r.Board.Name).ToList();

    public static void Run()
    {
        var shell = Program.MakeShell(1280, 820);
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
        shell.Show();
        Program.Pump(1500);

        PlanBoard Make(string id, string name, params string[] notes)
        {
            var b = new PlanBoard { Id = id, Name = name, SectionMembershipSet = true };
            var x = 48.0;
            foreach (var n in notes)
            {
                b.Nodes.Add(new PlanNode { Kind = PlanNodeKind.Note, X = x, Y = 48, W = 240, H = 144, Title = n, Body = "Planning text." });
                x += 288;
            }
            return b;
        }
        var plans = new PackPlans
        {
            Boards = { Make("alpha", "Launch plan", "Pick a theme", "Mod list"), Make("beta", "Ports", "Create addons"), Make("gamma", "Shaders", "Wisp") },
            LastBoardId = "alpha"
        };
        var file = Path.Combine(App.State.Packs.GameDir(Pack), ".cloudlauncher", "plans.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(plans));
        App.State.ModPlans.Invalidate(Pack);
        lock (FakeWorld.DiscordLinks) FakeWorld.DiscordLinks.Remove(Pack);

        _view = new ModPlanView();
        var host = new Window
        {
            Left = -24000, Top = -24000, Width = 1100, Height = 560, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = _view,
        };
        host.Show();
        Program.Pump(800);
        _view.Load(Pack, Array.Empty<PackMod>(), shell);
        Program.Pump(1500);

        // ── no channel ──
        Check("no Discord channel: no tick", !Up, $"{Toggle.Visibility}");
        Check("no Discord channel: nothing marked in the rail", Marked().Count == 0, string.Join(",", Marked()));

        // ── a channel showing every board ──
        SetLinks(Link(null));
        Check("a channel: the tick is up and ticked", Up && Toggle.IsChecked == true && Toggle.IsEnabled,
            $"{Toggle.Visibility} {Toggle.IsChecked} {Toggle.IsEnabled}");
        Check("a channel: the tooltip names it", TipText(Toggle).Contains("Shown in #modpack-planning"), TipText(Toggle));
        Program.Shoot(_view, "planning-discord-ticked", 1);

        Click(Toggle, false);
        Check("unticked: the board is kept out", !Board.ShowInDiscord);
        App.State.ModPlans.Flush(Pack); // the file follows a burst of changes a moment later
        Check("unticked: saved to plans.json",
            ModPlanService.Parse(File.ReadAllText(file))?.Boards.FirstOrDefault(b => b.Id == Board.Id)?.ShowInDiscord == false);
        Check("unticked: the status line says so", Status == "Kept out of Discord.", Status);
        var status = (TextBlock)_view.FindName("PlanStatus");
        var buttonsEnd = ((FrameworkElement)_view.FindName("ToolbarLeft")).ActualWidth;
        var statusStart = status.TranslatePoint(new Point(0, 0), _view).X;
        Check("the status line stays clear of the card buttons", statusStart >= buttonsEnd, $"status at {statusStart:0}, buttons end at {buttonsEnd:0}");
        Check("unticked: marked in the rail, alone", Marked().SequenceEqual(new[] { "Launch plan" }), string.Join(",", Marked()));
        Check("unticked: the tooltip offers to tick it again", TipText(Toggle).Contains("Tick to show this board there again"), TipText(Toggle));
        var sent = false;
        for (var i = 0; i < 60 && !sent; i++)
        {
            Program.Pump(100);
            sent = FakeWorld.StoredPlans(Pack) is { } stored && Regex.Matches(stored, "\"ShowInDiscord\"\\s*:\\s*false").Count == 1;
        }
        Check("unticked: sent to the server", sent, FakeWorld.StoredPlans(Pack) ?? "(nothing stored)");
        Program.Shoot(_view, "planning-discord-unticked", 1);

        // ── not an undo step ──
        var cards = Board.Nodes.Count;
        Call("OnAddNoteCard", _view, new RoutedEventArgs());
        Program.Pump(200);
        System.Windows.Input.Keyboard.ClearFocus();
        Check("(a note was added to undo)", Board.Nodes.Count == cards + 1, $"{Board.Nodes.Count}");
        Call("Undo");
        Program.Pump(200);
        Call("Undo");
        Program.Pump(200);
        Check("Ctrl+Z after unticking takes back the card, never the tick",
            Board.Nodes.Count == cards && !Board.ShowInDiscord && Toggle.IsChecked == false,
            $"cards {Board.Nodes.Count} show={Board.ShowInDiscord} box={Toggle.IsChecked}");

        // ── per board ──
        Call("SwitchToBoard", Named("beta"));
        Program.Pump(300);
        Check("another board: its own tick, ticked", Up && Toggle.IsChecked == true);
        Call("SwitchToBoard", Named("alpha"));
        Program.Pump(300);
        Check("back on the unticked board: unticked", Up && Toggle.IsChecked == false);

        // ── ticked again elsewhere (another PC, a collaborator) ──
        var remote = ModPlanService.Parse(ModPlanService.Serialize(App.State.ModPlans.Load(Pack)))!;
        remote.Boards.Single(b => b.Id == "alpha").ShowInDiscord = true;
        App.State.ModPlans.ReplaceFromSync(Pack, remote);
        Program.Pump(1200);
        Check("ticked elsewhere: the tick follows", Toggle.IsChecked == true && Board.ShowInDiscord, $"{Toggle.IsChecked} {Board.ShowInDiscord}");
        Check("ticked elsewhere: the rail mark goes", Marked().Count == 0, string.Join(",", Marked()));

        // ── a channel showing one board ──
        SetLinks(Link("beta", "#ports"));
        Check("a channel for Ports alone: no tick on the other boards", !Up, $"{Toggle.Visibility}");
        Call("SwitchToBoard", Named("beta"));
        Program.Pump(300);
        Check("...and a tick on Ports, naming that channel", Up && TipText(Toggle).Contains("#ports"), TipText(Toggle));
        Click(Toggle, false);
        Check("unticking Ports keeps it out", !Named("beta").ShowInDiscord);

        // ── the Show in Discord card ──
        SetLinks(Link(null), Link("beta", "#ports"));
        Check("two channels: the tooltip names both", TipText(Toggle).Contains("#modpack-planning and #ports"), TipText(Toggle));
        var keptOut = App.State.ModPlans.Boards(Pack).Where(b => !b.ShowInDiscord).Select(b => (b.Id, b.Name)).ToList();
        var card = new PlanDiscordCard(Pack, Board.Id, Board.Name, keptOut);
        var cardHost = new Window
        {
            Left = -24000, Top = -24000, Width = 640, Height = 900, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = card,
        };
        cardHost.Show();
        Program.Pump(1800);
        var line = ((TextBlock)card.FindName("KeptOutLabel")).Text;
        Check("card: says how to leave a board out and which are", line.Contains("untick Discord at the top of the board")
            && line.Contains("Left out now: Ports."), line);
        var rows = Texts((DependencyObject)card.FindName("LinkList"));
        Check("card: the every-board channel counts what's left out", rows.Contains("Every board but one, kept up to date"), string.Join(" / ", rows));
        Check("card: the Ports channel says its board is kept out",
            rows.Any(t => t.StartsWith("Its board is kept out of Discord")), string.Join(" / ", rows));
        Program.Shoot(card, "planning-discord-card", 1);
        cardHost.Close();
        Program.Pump(200);

        // ── a view-only account ──
        var sync = App.State.PlanSync;
        var states = (System.Collections.IDictionary)typeof(PlanSyncService).GetField("_state", F)!.GetValue(sync)!;
        var ps = states[Pack]!;
        var setState = typeof(PlanSyncService).GetMethod("SetState", F)!;
        ps.GetType().GetField("ViewOnly")!.SetValue(ps, true);
        setState.Invoke(sync, new[] { (object)Pack, ps, PlanSyncState.ViewOnly });
        Program.Pump(200);
        Check("view only: the tick shows, can't be changed, and says why",
            Up && !Toggle.IsEnabled && TipText(Toggle).Contains("Only the pack's editors"), $"{Toggle.IsEnabled} {TipText(Toggle)}");
        ps.GetType().GetField("ViewOnly")!.SetValue(ps, false);
        setState.Invoke(sync, new[] { (object)Pack, ps, PlanSyncState.Synced });
        Program.Pump(200);
        Check("editor again: the tick can be changed", Toggle.IsEnabled);

        // ── a copy of a kept-out board ──
        var copy = App.State.ModPlans.DuplicateBoard(Pack, Named("beta"));
        Check("a copy of a kept-out board stays out of Discord", !copy.ShowInDiscord);
        Check("a copy of a shown board shows", App.State.ModPlans.DuplicateBoard(Pack, Named("gamma")).ShowInDiscord);

        // ── a long message in a short toolbar trims instead of running under the buttons ──
        foreach (var width in new[] { 1250.0, 1100.0 })
        {
            host.Width = width;
            Program.Pump(300);
            status.Text = "Replaced 3 placeholders with their mods (2 note(s) moved onto the mods).";
            Program.Pump(400);
            var left = ((FrameworkElement)_view.FindName("ToolbarLeft")).ActualWidth;
            var at = status.TranslatePoint(new Point(0, 0), _view).X;
            Check($"{width:0} px, long message: trimmed and clear of the card buttons",
                at >= left && status.ActualWidth < 360, $"status {at:0}..{at + status.ActualWidth:0}, buttons end at {left:0}");
            Program.Shoot(_view, $"planning-discord-long-{width:0}", 1);
        }
        host.Width = 1100;

        // ── no channels any more: the tick goes, so do the marks ──
        SetLinks();
        Check("channels removed: no tick", !Up, $"{Toggle.Visibility}");
        Check("channels removed: no marks", Marked().Count == 0, string.Join(",", Marked()));

        Program.Log($"discord checks done: {_fails} failed");
        host.Close();
        shell.Hide();
        Program.Pump(300);
    }
}
