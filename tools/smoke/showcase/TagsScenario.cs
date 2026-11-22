using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CloudLauncher;
using CloudLauncher.Services;
using CloudLauncher.Views;

namespace Showcase;

/// <summary>
/// A pack's own mod tags (right-click a mod > Tags), through the real metadata service, mod inventory,
/// options menu and list views, over three seeded instances: one whose mods.json defines tags (written
/// by hand the way a pack tool writes it, with keys this launcher doesn't know), one without tags, and
/// one whose file was written carelessly. Covers: which mod ids a jar declares; loading; the menu with
/// and without tags and on the global scope; ticking a tag on one mod and on a selection (mixed, one
/// save); a jar with two mod ids and one with none, for tags and early access; rename, colour, add and
/// remove; a mod's Side, which is its own flag and nothing else: a "Sides" key that a file holds (a pack
/// tool once wrote one) is not the launcher's, so it gives no mod a side and comes through every save
/// as it was written, a Side set in the menu included; what the saved file looks like and that unknown
/// keys survive it; the pills and the tag filter on the pack's mod list, on a dark and a light look; and
/// New tag / Rename / Colour / Remove through the menu's own cards. Results are PASS/FAIL lines in the
/// log; the last line counts the failures.
/// </summary>
public static class TagsScenario
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _fails;
    private static int _changed;

    private static Guid Tagged => FakeWorld.Workshop;     // its mods.json defines tags
    private static Guid Plain => FakeWorld.VanillaPlus;   // no tags, no mods.json to begin with
    private static Guid Odd => FakeWorld.Skyline;         // a mods.json written carelessly by hand

    /// <summary>The "Sides" key of the first pack's seeded file, as <see cref="Key"/> gives it back: mod
    /// id -> side, as a pack tool once wrote it. Not sorted, and with a jar's two ids on different sides:
    /// a launcher that read the key would have sorted it, added to it and given gamma and duo a side.</summary>
    private const string ForeignSides = "{\"gamma\":\"Client\",\"duo_forge\":\"Server\",\"mix_a\":\"Client\",\"mix_b\":\"Server\"}";

    private static ModMetadataService Meta => App.State.ModMetadata;
    private static PackModInventory Inventory => App.State.ModInventory;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Program.Log($"{(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : detail)}");
    }

    private static T Await<T>(Task<T> task)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!task.IsCompleted && DateTime.UtcNow < until) Program.Pump(40);
        return task.GetAwaiter().GetResult();
    }

    private static bool WaitFor(Func<bool> condition, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until) Program.Pump(40);
        return condition();
    }

    // ── the files ────────────────────────────────────────────────────────────

    private static string MetaPath(Guid pack) =>
        Path.Combine(App.State.Packs.GameDir(pack), ".cloudlauncher", "mods.json");

    private static JsonElement Doc(Guid pack) => JsonDocument.Parse(File.ReadAllText(MetaPath(pack))).RootElement.Clone();

    /// <summary>One top-level key of the saved file as compact JSON, or "(none)" when it isn't there.</summary>
    private static string Key(Guid pack, string key) =>
        Doc(pack).TryGetProperty(key, out var value) ? JsonSerializer.Serialize(value) : "(none)";

    private static string Defs(Guid pack) => string.Join(",", Meta.TagDefs(pack).Select(t => t.Name + t.Color));
    private static string Names(IEnumerable<ModTag> tags) => string.Join(",", tags.Select(t => t.Name));

    /// <summary>A mod jar with the metadata given: a NeoForge mods.toml, a fabric.mod.json, both or
    /// neither.</summary>
    private static string Jar(Guid pack, string fileName, string? neoForgeToml = null, string? fabricJson = null)
    {
        var dir = Path.Combine(App.State.Packs.GameDir(pack), "mods");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        Put("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n");
        if (neoForgeToml is not null) Put("META-INF/neoforge.mods.toml", neoForgeToml);
        if (fabricJson is not null) Put("fabric.mod.json", fabricJson);
        return path;
    }

    /// <summary>A mods.toml declaring these mods, with a dependency block (whose modId is not the
    /// jar's) before or after them.</summary>
    private static string Toml(bool dependenciesFirst, string newLine, params string[] modIds)
    {
        var deps = $"[[dependencies.{modIds[0]}]]\nmodId=\"neoforge\"\ntype=\"required\"\nversionRange=\"[21.1,)\"\nside=\"BOTH\"\n";
        var mods = string.Concat(modIds.Select(id => $"[[mods]]\nmodId=\"{id}\"\nversion=\"1.0.0\"\ndisplayName=\"{id}\"\n"));
        var text = "modLoader=\"javafml\"\nloaderVersion=\"[4,)\"\nlicense=\"MIT\"\n" + (dependenciesFirst ? deps + mods : mods + deps);
        return text.Replace("\n", newLine);
    }

    /// <summary>Counts the saves of one mods.json: each is a temp file renamed over it.</summary>
    private sealed class SaveCounter : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private int _count;

        public SaveCounter(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(file)!) { NotifyFilter = NotifyFilters.FileName };
            var name = Path.GetFileName(file);
            _watcher.Renamed += (_, e) =>
            {
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _count);
            };
            _watcher.EnableRaisingEvents = true;
        }

        /// <summary>The saves since the last call, once the watcher has had time to report them.</summary>
        public int Take()
        {
            var seen = -1;
            for (var i = 0; i < 20 && seen != Volatile.Read(ref _count); i++)
            {
                seen = Volatile.Read(ref _count);
                Program.Pump(120);
            }
            return Interlocked.Exchange(ref _count, 0);
        }

        public void Dispose() => _watcher.Dispose();
    }

    // ── the menu ─────────────────────────────────────────────────────────────

    private static ModOptionsContext Ctx(Guid pack, IReadOnlyList<PackMod> all, Window? owner = null) => new()
    {
        PackId = pack, Inventory = Inventory, Owner = owner, AllMods = all, OnChanged = () => _changed++,
    };

    private static MenuItem? Find(ItemsControl parent, string header) =>
        parent.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == header);

    private static int IndexOf(ItemsControl parent, string header) =>
        parent.Items.Cast<object>().ToList().FindIndex(o => o is MenuItem { Header: string h } && h == header);

    private static string Headers(ItemsControl parent) => string.Join(" | ",
        parent.Items.Cast<object>().Select(o => o is Separator ? "-" : (o as MenuItem)?.Header as string ?? "?"));

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        Program.Pump(30);
    }

    private static string Tip(FrameworkElement e) => e.ToolTip as string ?? "";

    // ── the scenario ─────────────────────────────────────────────────────────

    public static void Run()
    {
        // Everything is seeded before a window exists, so nothing has read these packs yet.
        var nl = Environment.NewLine;
        var alphaJar = Jar(Tagged, "alpha-1.0.jar", Toml(false, "\n", "alpha"));
        Jar(Tagged, "beta-1.0.jar", Toml(false, "\n", "beta"));
        var gammaJar = Jar(Tagged, "gamma-1.0.jar", Toml(true, "\n", "gamma"));
        var twinsJar = Jar(Tagged, "twins-1.0.jar", Toml(false, "\r\n", "twin_one", "twin_two"));
        var duoJar = Jar(Tagged, "duo-1.0.jar", Toml(true, "\n", "duo_forge"), "{ \"schemaVersion\": 1, \"id\": \"duo_fabric\", \"version\": \"1.0.0\" }");
        Jar(Tagged, "delta-1.0.jar", Toml(false, "\n", "Delta"));
        Jar(Tagged, "mixed-1.0.jar", Toml(false, "\n", "mix_a", "mix_b"));
        var noidJar = Jar(Tagged, "noid-1.0.jar");
        Jar(Plain, "p1-1.0.jar", Toml(false, "\n", "p1"));
        Jar(Plain, "p2-1.0.jar", Toml(false, "\n", "p2"));

        // A mods.json as a pack tool or a person writes it. The two tag keys; a "Sides" key (mod id ->
        // side) such as a pack tool once wrote, naming mods whose own flags say nothing (gamma; duo
        // under one of its two ids; "mixed" with one id each way): it is not the launcher's, so it must
        // give none of them a side and stay in the file as it stands here; flags with a Side of their
        // own (alpha, twins); early access on the second id of a two-id jar; and keys from a launcher
        // newer than this one at every level.
        Directory.CreateDirectory(Path.GetDirectoryName(MetaPath(Tagged))!);
        File.WriteAllText(MetaPath(Tagged), """
            {
              "Version": 2,
              "Mods": {
                "file:alpha-1.0.jar": { "Side": 1, "FutureFlag": { "x": 1 } },
                "file:twins-1.0.jar": { "Side": 2 },
                "file:beta-1.0.jar": { "Priority": 2 }
              },
              "Categories": [ { "Name": "Library", "Builtin": true, "FutureCategoryField": "kept" } ],
              "Advanced": {
                "AutoDownloadDependencies": true, "CascadeDisableDependents": true, "CascadeDisableLibraries": true,
                "WarnOnUpdateIncompatible": true, "ShowDependencyLines": true, "DefaultClusterMode": 0, "FutureAdvanced": 7
              },
              "CustomLayout": {},
              "CategoryLayout": {},
              "CollapsedCategories": [],
              "TagDefs": [ { "Name": "Lite", "Color": "#5B9BD5" }, { "Name": "Vanilla", "Color": "#7FB069", "FutureTagField": [1, 2] } ],
              "Tags": { "alpha": ["Lite"], "gamma": ["Vanilla"] },
              "Sides": { "gamma": "Client", "duo_forge": "Server", "mix_a": "Client", "mix_b": "Server" },
              "EarlyAccess": ["alpha", "twin_two"],
              "FutureKey": { "hello": [1, 2, 3] }
            }
            """);
        foreach (var pack in new[] { Tagged, Plain, Odd }) Meta.Invalidate(pack);

        var shell = Program.MakeShell(1280, 820);
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(shell.Dispatcher));
        shell.Show();
        Program.Pump(1500);

        // ── which mod ids a jar declares ──
        static string Ids(string jar) => string.Join(",", GlobalPackService.JarModIds(jar).OrderBy(s => s, StringComparer.Ordinal));
        Check("a jar's mod id is its [[mods]] block's, not a dependency's", Ids(alphaJar) == "alpha", Ids(alphaJar));
        Check("...also when the dependencies stand first in the file", Ids(gammaJar) == "gamma", Ids(gammaJar));
        Check("a jar with two [[mods]] blocks declares both ids (CRLF file)", Ids(twinsJar) == "twin_one,twin_two", Ids(twinsJar));
        Check("a Fabric id and a NeoForge id in one jar are both its ids", Ids(duoJar) == "duo_fabric,duo_forge", Ids(duoJar));
        Check("a jar without metadata declares none", Ids(noidJar) == "", Ids(noidJar));

        // ── loading ──
        using var saves = new SaveCounter(MetaPath(Tagged));
        var mods = Await(Inventory.LoadAsync(Tagged, includeLocal: true));
        PackMod Mod(string file) => mods.Single(m => m.FileName == file);
        var alpha = Mod("alpha-1.0.jar");
        var beta = Mod("beta-1.0.jar");
        var gamma = Mod("gamma-1.0.jar");
        var twins = Mod("twins-1.0.jar");
        var duo = Mod("duo-1.0.jar");
        var delta = Mod("delta-1.0.jar");
        var mixed = Mod("mixed-1.0.jar");
        var noid = Mod("noid-1.0.jar");
        var loadSaves = saves.Take();

        Check("the pack's tags load in their order, with their colours", Defs(Tagged) == "Lite#5B9BD5,Vanilla#7FB069", Defs(Tagged));
        Check("each mod is stamped with the tags its ids carry", Names(alpha.Tags) == "Lite" && Names(gamma.Tags) == "Vanilla"
            && beta.Tags.Count == 0 && noid.Tags.Count == 0, $"alpha={Names(alpha.Tags)} gamma={Names(gamma.Tags)} beta={Names(beta.Tags)}");
        Check("a mod's tooltip names its tags", alpha.TooltipText.Contains("Tags: Lite"), alpha.TooltipText);
        Check("early access as before, and for a jar marked under its second id", alpha.IsEarlyAccess && twins.IsEarlyAccess && !beta.IsEarlyAccess);
        Check("a mod's Side is what its own flags say", alpha.Meta.Side == ModSide.Client && twins.Meta.Side == ModSide.Server
            && beta.Meta.Side == ModSide.Both, $"alpha={alpha.Meta.Side} twins={twins.Meta.Side} beta={beta.Meta.Side}");
        Check("a Sides key in the file gives no mod a side: gamma, duo and mixed are named there and stay at Both",
            gamma.Meta.Side == ModSide.Both && !gamma.IsSideRestricted && duo.Meta.Side == ModSide.Both && mixed.Meta.Side == ModSide.Both,
            $"gamma={gamma.Meta.Side} duo={duo.Meta.Side} mixed={mixed.Meta.Side}");
        Check("reading a pack's mods writes nothing", loadSaves == 0 && Key(Tagged, "Sides") == ForeignSides, $"{loadSaves} saves, {Key(Tagged, "Sides")}");

        // ── the saved file: unknown keys, order, shape ──
        // The launcher writes back the file it read: the round trip of every key it doesn't know.
        Meta.Save(Tagged);
        Check("(the launcher saves the file once)", saves.Take() == 1);
        var doc = Doc(Tagged);
        string Raw(params string[] path)
        {
            var e = doc;
            foreach (var p in path)
            {
                if (e.ValueKind == JsonValueKind.Array) e = e[int.Parse(p)];
                else if (!e.TryGetProperty(p, out e)) return "(none)";
            }
            return JsonSerializer.Serialize(e);
        }
        Check("a top-level key this launcher doesn't know survives the save", Raw("FutureKey") == "{\"hello\":[1,2,3]}", Raw("FutureKey"));
        Check("...so does one in a mod's flags", Raw("Mods", "file:alpha-1.0.jar", "FutureFlag") == "{\"x\":1}", Raw("Mods", "file:alpha-1.0.jar"));
        Check("...in the pack's settings", Raw("Advanced", "FutureAdvanced") == "7", Raw("Advanced"));
        Check("...in a category", Raw("Categories", "0", "FutureCategoryField") == "\"kept\"", Raw("Categories"));
        Check("...and in a tag's definition", Raw("TagDefs", "1", "FutureTagField") == "[1,2]", Raw("TagDefs"));
        Check("a Sides key is one of them: it comes through the save as it was written, every entry, in the file's own order",
            Raw("Sides") == ForeignSides, Raw("Sides"));
        Check("...and a mod's Side is in its flags and nowhere else: none was added for the mods the key names",
            Raw("Mods", "file:alpha-1.0.jar", "Side") == "1" && Raw("Mods", "file:twins-1.0.jar", "Side") == "2"
            && Raw("Mods", "file:gamma-1.0.jar") == "(none)" && Raw("Mods", "file:duo-1.0.jar") == "(none)" && Raw("Mods", "file:mixed-1.0.jar") == "(none)",
            Raw("Mods"));
        var order = string.Join(",", doc.EnumerateObject().Select(p => p.Name));
        Check("the two tag keys stand before EarlyAccess, the keys the launcher doesn't know last",
            order == "Version,Mods,Categories,Advanced,CustomLayout,CategoryLayout,CollapsedCategories,TagDefs,Tags,EarlyAccess,Sides,FutureKey", order);
        Check("TagDefs is a list of Name and Color", Raw("TagDefs", "0") == "{\"Name\":\"Lite\",\"Color\":\"#5B9BD5\"}", Raw("TagDefs", "0"));
        Check("Tags is mod id -> list of tag names", Raw("Tags") == "{\"alpha\":[\"Lite\"],\"gamma\":[\"Vanilla\"]}", Raw("Tags"));
        var text = File.ReadAllText(MetaPath(Tagged));
        Check("the keys are laid out with two spaces and one value per line, as the pack tools write them",
            text.Contains($"  \"Tags\": {{{nl}    \"alpha\": [{nl}      \"Lite\"{nl}    ],{nl}"),
            text[Math.Max(0, text.IndexOf("\"Tags\"", StringComparison.Ordinal))..]);

        // ── the menu in a pack with tags ──
        var menu = ModOptionsMenu.Build(new[] { beta }, Ctx(Tagged, mods));
        var tags = Find(menu, "Tags");
        Check("a pack that defines tags has a Tags item", tags is not null, Headers(menu));
        if (tags is null) { Program.Log("tags checks stopped: no Tags item"); shell.Hide(); return; }
        Check("...where Early access was, after Extra", IndexOf(menu, "Tags") == IndexOf(menu, "Extra (optional)") + 1, Headers(menu));
        Check("...and Early access is inside it, not beside it", Find(menu, "Early access") is null, Headers(menu));
        Check("the section: the tags, Early access, then New tag and Edit tags",
            Headers(tags) == "Lite | Vanilla | Early access | - | New tag... | Edit tags", Headers(tags));
        var lite = Find(tags, "Lite")!;
        var vanilla = Find(tags, "Vanilla")!;
        var early = Find(tags, "Early access")!;
        Check("nothing ticked on a mod without tags", lite.InputGestureText == "" && vanilla.InputGestureText == "" && tags.InputGestureText == "",
            $"'{lite.InputGestureText}' '{vanilla.InputGestureText}' '{tags.InputGestureText}'");
        Check("a tag item stays open on click and shows the tag's colour", vanilla.StaysOpenOnClick
            && vanilla.Icon is Border { Background: SolidColorBrush { Color: { R: 0x7F, G: 0xB0, B: 0x69 } } });
        _changed = 0;
        saves.Take();
        Click(vanilla);
        Check("a click puts the tag on the mod", beta.HasTag("Vanilla") && Key(Tagged, "Tags").Contains("\"beta\":[\"Vanilla\"]"), Key(Tagged, "Tags"));
        Check("...ticks the item and names the tag beside Tags", vanilla.InputGestureText == "✓" && tags.InputGestureText == "Vanilla",
            $"'{vanilla.InputGestureText}' '{tags.InputGestureText}'");
        var afterClick = saves.Take();
        Check("...with one save and one refresh of the view", afterClick == 1 && _changed == 1, $"{afterClick} saves, {_changed} refreshes");
        Click(early);
        Check("Early access in the section is the mark the launcher always had", beta.IsEarlyAccess
            && Meta.EarlyAccessIds(Tagged).Contains("beta") && tags.InputGestureText == "Vanilla, Early access", tags.InputGestureText);
        Click(early);
        Click(vanilla);
        Check("a second click takes each off again", !beta.IsEarlyAccess && beta.Tags.Count == 0 && !Key(Tagged, "Tags").Contains("beta")
            && vanilla.InputGestureText == "" && tags.InputGestureText == "", Key(Tagged, "Tags") + " '" + tags.InputGestureText + "'");

        // ── a selection ──
        menu = ModOptionsMenu.Build(new[] { alpha, beta, gamma }, Ctx(Tagged, mods));
        tags = Find(menu, "Tags")!;
        lite = Find(tags, "Lite")!;
        vanilla = Find(tags, "Vanilla")!;
        Check("selection: a tag only some of them have is not ticked", lite.InputGestureText == "" && vanilla.InputGestureText == ""
            && tags.InputGestureText == "", $"'{lite.InputGestureText}' '{vanilla.InputGestureText}'");
        _changed = 0;
        saves.Take();
        Click(lite);
        Check("...a click gives it to all of them", alpha.HasTag("Lite") && beta.HasTag("Lite") && gamma.HasTag("Lite") && lite.InputGestureText == "✓");
        afterClick = saves.Take();
        Check("...in one save for the whole selection", afterClick == 1 && _changed == 1, $"{afterClick} saves, {_changed} refreshes");
        Check("a mod's tags are kept in the order the pack defines them", Key(Tagged, "Tags").Contains("\"gamma\":[\"Lite\",\"Vanilla\"]")
            && Names(gamma.Tags) == "Lite,Vanilla", Key(Tagged, "Tags"));
        Click(lite);
        afterClick = saves.Take();
        Check("...the next click takes it off all of them, their other tags kept", !alpha.HasTag("Lite") && !beta.HasTag("Lite")
            && Names(gamma.Tags) == "Vanilla" && lite.InputGestureText == "" && afterClick == 1, $"{Key(Tagged, "Tags")} {afterClick} saves");
        Check("...and a mod left with no tag is not left in the file", Key(Tagged, "Tags") == "{\"gamma\":[\"Vanilla\"]}", Key(Tagged, "Tags"));
        Meta.SetTag(Tagged, alpha.JarModIds, "Lite", true);
        Inventory.StampTags(Tagged, mods);

        // ── jars without a mod id ──
        menu = ModOptionsMenu.Build(new[] { noid }, Ctx(Tagged, mods));
        tags = Find(menu, "Tags")!;
        lite = Find(tags, "Lite")!;
        Check("a jar without a mod id: the tag items are disabled", !lite.IsEnabled && !Find(tags, "Vanilla")!.IsEnabled && !Find(tags, "Early access")!.IsEnabled);
        Check("...and say why, also while disabled", Tip(lite).Contains("declares no mod id") && ToolTipService.GetShowOnDisabled(lite), Tip(lite));
        Check("...while New tag and Edit tags stay usable", Find(tags, "New tag...")!.IsEnabled && Find(tags, "Edit tags")!.IsEnabled);
        menu = ModOptionsMenu.Build(new[] { beta, noid }, Ctx(Tagged, mods));
        tags = Find(menu, "Tags")!;
        lite = Find(tags, "Lite")!;
        Check("in a selection such a jar is left out, and the tooltip says so", lite.IsEnabled && Tip(lite).Contains("1 of the 2 selected jars"), Tip(lite));
        Click(lite);
        Check("...the rest are tagged, nothing is written for it", beta.HasTag("Lite") && noid.Tags.Count == 0 && lite.InputGestureText == "✓"
            && !Key(Tagged, "Tags").Contains("\"\""), Key(Tagged, "Tags"));
        Click(lite);

        // ── a jar with two mod ids, and an id in another casing ──
        menu = ModOptionsMenu.Build(new[] { twins }, Ctx(Tagged, mods));
        tags = Find(menu, "Tags")!;
        lite = Find(tags, "Lite")!;
        Click(lite);
        Check("a jar with two mod ids gets the tag under both", twins.HasTag("Lite") && Key(Tagged, "Tags").Contains("\"twin_one\":[\"Lite\"]")
            && Key(Tagged, "Tags").Contains("\"twin_two\":[\"Lite\"]"), Key(Tagged, "Tags"));
        Meta.SetTag(Tagged, new[] { "twin_one" }, "Lite", false);
        Inventory.StampTags(Tagged, mods);
        Check("...and still carries it while one of them has it", twins.HasTag("Lite") && Names(Meta.TagsFor(Tagged, new[] { "TWIN_TWO" })) == "Lite",
            Key(Tagged, "Tags"));
        menu = ModOptionsMenu.Build(new[] { twins }, Ctx(Tagged, mods));
        tags = Find(menu, "Tags")!;
        lite = Find(tags, "Lite")!;
        early = Find(tags, "Early access")!;
        Check("...which the menu shows ticked, like its early access (marked under the second id only)",
            lite.InputGestureText == "✓" && early.InputGestureText == "✓", $"'{lite.InputGestureText}' '{early.InputGestureText}'");
        Click(lite);
        Check("...until it is taken off the jar, which clears every id", !twins.HasTag("Lite") && !Key(Tagged, "Tags").Contains("twin_"), Key(Tagged, "Tags"));
        Click(early);
        Check("early access comes off both ids the same way", !twins.IsEarlyAccess && Key(Tagged, "EarlyAccess") == "[\"alpha\"]", Key(Tagged, "EarlyAccess"));
        Click(early);
        Check("...and goes on both", twins.IsEarlyAccess && Key(Tagged, "EarlyAccess") == "[\"alpha\",\"twin_one\",\"twin_two\"]", Key(Tagged, "EarlyAccess"));
        Click(early);
        menu = ModOptionsMenu.Build(new[] { delta }, Ctx(Tagged, mods));
        Click(Find(Find(menu, "Tags")!, "Vanilla")!);
        Check("mod ids are stored in lower case, whatever the jar's spelling", delta.HasTag("Vanilla")
            && Key(Tagged, "Tags").Contains("\"delta\":[\"Vanilla\"]") && !Key(Tagged, "Tags").Contains("Delta"), Key(Tagged, "Tags"));

        // ── rename, colour, add, remove ──
        Check("rename: not onto another tag's name", !Meta.RenameTag(Tagged, "Lite", "vanilla") && Defs(Tagged).StartsWith("Lite#"), Defs(Tagged));
        Check("rename: not to Early access", !Meta.RenameTag(Tagged, "Lite", " early  ACCESS "));
        Check("rename: not a tag the pack doesn't have", !Meta.RenameTag(Tagged, "Nope", "Else"));
        saves.Take();
        Check("rename: Lite becomes Light", Meta.RenameTag(Tagged, "Lite", "  Light "));
        Inventory.StampTags(Tagged, mods);
        Check("...in its place, colour kept", Defs(Tagged) == "Light#5B9BD5,Vanilla#7FB069", Defs(Tagged));
        Check("...on every mod that carried it", alpha.HasTag("Light") && !alpha.HasTag("Lite") && Key(Tagged, "Tags").Contains("\"alpha\":[\"Light\"]")
            && !File.ReadAllText(MetaPath(Tagged)).Contains("\"Lite\""), Key(Tagged, "Tags"));
        Check("...with one save", saves.Take() == 1);
        Check("a tag is found whatever the casing", Meta.RenameTag(Tagged, "LIGHT", "Lite") && Defs(Tagged).StartsWith("Lite#"), Defs(Tagged));
        Inventory.StampTags(Tagged, mods);

        Check("colour: six hex digits are taken, stored upper case", Meta.SetTagColor(Tagged, "vanilla", "3ec8c1")
            && Meta.TagDefs(Tagged)[1].Color == "#3EC8C1", Defs(Tagged));
        Check("colour: anything else is refused", !Meta.SetTagColor(Tagged, "Vanilla", "teal") && !Meta.SetTagColor(Tagged, "Vanilla", null)
            && Meta.TagDefs(Tagged)[1].Color == "#3EC8C1", Defs(Tagged));

        var temp = Meta.AddTag(Tagged, "  Temp   tag ");
        Check("a new tag: name tidied, the next unused accent as its colour, last in the list", temp is { Name: "Temp tag", Color: "#5B9DF9" }
            && Defs(Tagged) == "Lite#5B9BD5,Vanilla#3EC8C1,Temp tag#5B9DF9", Defs(Tagged));
        Check("...adding it again gives the one there is", Meta.AddTag(Tagged, "temp TAG") is { Name: "Temp tag" } && Meta.TagDefs(Tagged).Count == 3);
        Check("...a tag can't be called Early access, or nothing", Meta.AddTag(Tagged, "early access") is null && Meta.AddTag(Tagged, "   ") is null
            && Meta.TagDefs(Tagged).Count == 3);
        Meta.SetTag(Tagged, alpha.JarModIds.Concat(beta.JarModIds), "Temp tag", true);
        Check("only a tag the pack defines can be set", !Meta.SetTag(Tagged, beta.JarModIds, "Undefined", true) && !Key(Tagged, "Tags").Contains("Undefined"));
        saves.Take();
        Check("remove: the tag goes", Meta.RemoveTag(Tagged, "TEMP TAG") && Defs(Tagged) == "Lite#5B9BD5,Vanilla#3EC8C1", Defs(Tagged));
        Inventory.StampTags(Tagged, mods);
        Check("...off every mod, their other tags kept", Names(alpha.Tags) == "Lite" && beta.Tags.Count == 0
            && !Key(Tagged, "Tags").Contains("Temp") && !Key(Tagged, "Tags").Contains("beta"), Key(Tagged, "Tags"));
        Check("...with one save", saves.Take() == 1);

        // ── a mod's Side: its own flag, set in the menu as it always was; nothing is kept by mod id ──
        MenuItem Side(ContextMenu from, string header) => Find(Find(from, "Side")!, header)!;
        string SideFlag(string file) => Doc(Tagged).GetProperty("Mods").TryGetProperty("file:" + file, out var flags)
            && flags.TryGetProperty("Side", out var stored) ? stored.GetRawText() : "(none)";
        menu = ModOptionsMenu.Build(new[] { gamma }, Ctx(Tagged, mods));
        Check("the Side menu shows the mod's own flag: gamma is at Client + Server, whatever the Sides key calls it",
            Side(menu, "Client + Server").InputGestureText == "✓" && Side(menu, "Client only").InputGestureText == ""
            && Find(menu, "Side")!.InputGestureText == "", $"'{Find(menu, "Side")!.InputGestureText}'");
        saves.Take();
        Click(Side(menu, "Server only"));
        afterClick = saves.Take();
        Check("Side > Server only sets the mod's flag, with one save", gamma.Meta.Side == ModSide.Server && SideFlag("gamma-1.0.jar") == "2"
            && afterClick == 1, $"{gamma.Meta.Side}, flag {SideFlag("gamma-1.0.jar")}, {afterClick} saves");
        Check("...and writes nothing by mod id: the Sides key still calls gamma Client, as it was written", Key(Tagged, "Sides") == ForeignSides,
            Key(Tagged, "Sides"));
        Click(Side(menu, "Client + Server"));
        Check("Client + Server puts the mod back at Both, and its flags out of the file", gamma.Meta.Side == ModSide.Both
            && SideFlag("gamma-1.0.jar") == "(none)" && Key(Tagged, "Sides") == ForeignSides, $"{gamma.Meta.Side}, flag {SideFlag("gamma-1.0.jar")}");
        menu = ModOptionsMenu.Build(new[] { twins, noid }, Ctx(Tagged, mods));
        Click(Side(menu, "Client only"));
        Check("a selection: every mod's own flag, a jar without a mod id like any other", twins.Meta.Side == ModSide.Client
            && noid.Meta.Side == ModSide.Client && SideFlag("twins-1.0.jar") == "1" && SideFlag("noid-1.0.jar") == "1"
            && Key(Tagged, "Sides") == ForeignSides, $"twins={twins.Meta.Side} noid={noid.Meta.Side} {Key(Tagged, "Sides")}");

        // An older launcher saves the file: it has no place for a key it doesn't know and drops it, the
        // two tag keys and the Sides key alike.
        var stripped = JsonNode.Parse(File.ReadAllText(MetaPath(Tagged)))!.AsObject();
        foreach (var key in new[] { "TagDefs", "Tags", "Sides" }) stripped.Remove(key);
        File.WriteAllText(MetaPath(Tagged), stripped.ToJsonString());
        Meta.Invalidate(Tagged);
        saves.Take();
        mods = Await(Inventory.LoadAsync(Tagged, includeLocal: true));
        (alpha, beta, gamma, twins, duo, delta, mixed, noid) = (Mod("alpha-1.0.jar"), Mod("beta-1.0.jar"), Mod("gamma-1.0.jar"), Mod("twins-1.0.jar"),
            Mod("duo-1.0.jar"), Mod("delta-1.0.jar"), Mod("mixed-1.0.jar"), Mod("noid-1.0.jar"));
        var afterOlder = saves.Take();
        Check("after an older launcher's save the tags are gone: the pack is back to no Tags item", Meta.TagDefs(Tagged).Count == 0 && alpha.Tags.Count == 0
            && Find(ModOptionsMenu.Build(new[] { alpha }, Ctx(Tagged, mods)), "Tags") is null && Key(Tagged, "Tags") == "(none)");
        Check("...the mods' sides are not: they are flags that launcher knows, and reading them back writes nothing",
            alpha.Meta.Side == ModSide.Client && twins.Meta.Side == ModSide.Client && noid.Meta.Side == ModSide.Client && gamma.Meta.Side == ModSide.Both
            && afterOlder == 0 && Key(Tagged, "Sides") == "(none)",
            $"alpha={alpha.Meta.Side} twins={twins.Meta.Side} noid={noid.Meta.Side} gamma={gamma.Meta.Side}, {afterOlder} saves, Sides {Key(Tagged, "Sides")}");
        Check("...and the keys that launcher did know are as they were", Meta.EarlyAccessIds(Tagged).SetEquals(new[] { "alpha" })
            && Key(Tagged, "FutureKey") == "{\"hello\":[1,2,3]}", Key(Tagged, "EarlyAccess"));

        // ── a pack without tags ──
        using (var plainSaves = new SaveCounter(MetaPath(Plain)))
        {
            var plain = Await(Inventory.LoadAsync(Plain, includeLocal: true));
            var p1 = plain.Single(m => m.FileName == "p1-1.0.jar");
            Check("a pack without tags: reading its mods writes nothing", plainSaves.Take() == 0 && !File.Exists(MetaPath(Plain)));
            menu = ModOptionsMenu.Build(new[] { p1 }, Ctx(Plain, plain));
            Check("...its menu has no Tags item", Find(menu, "Tags") is null, Headers(menu));
            Check("...and Early access where it has always been, after Extra", IndexOf(menu, "Early access") == IndexOf(menu, "Extra (optional)") + 1, Headers(menu));
            Click(Find(menu, "Early access")!);
            var plainText = File.ReadAllText(MetaPath(Plain));
            Check("...which works as before", p1.IsEarlyAccess && Key(Plain, "EarlyAccess") == "[\"p1\"]", Key(Plain, "EarlyAccess"));
            Check("...and its mods.json has neither tag key, nor a Sides key", !plainText.Contains("TagDefs") && !plainText.Contains("\"Tags\"")
                && !plainText.Contains("\"Sides\""), plainText);
            Click(Side(menu, "Client only"));
            Check("a Side set there is the mod's flag, as it always was: nothing by mod id, no key added to the file",
                p1.Meta.Side == ModSide.Client && Doc(Plain).GetProperty("Mods").TryGetProperty("file:p1-1.0.jar", out var p1Flags)
                && p1Flags.TryGetProperty("Side", out var p1Side) && p1Side.GetInt32() == 1
                && Key(Plain, "Sides") == "(none)" && Key(Plain, "Tags") == "(none)" && Key(Plain, "TagDefs") == "(none)",
                $"{p1.Meta.Side} {Key(Plain, "Mods")} Sides {Key(Plain, "Sides")}");

            Meta.AddTag(Plain, "Lite");
            menu = ModOptionsMenu.Build(new[] { p1 }, Ctx(Plain, plain));
            Check("the first tag a pack defines brings the Tags item", Find(menu, "Tags") is not null && Find(menu, "Early access") is null, Headers(menu));
            Check("...and both tag keys, Tags there even while empty, and nothing else", Key(Plain, "TagDefs") == "[{\"Name\":\"Lite\",\"Color\":\"#5B9DF9\"}]"
                && Key(Plain, "Tags") == "{}" && Key(Plain, "Sides") == "(none)", $"{Key(Plain, "TagDefs")} {Key(Plain, "Tags")} {Key(Plain, "Sides")}");
            Meta.RemoveTag(Plain, "Lite");
            menu = ModOptionsMenu.Build(new[] { p1 }, Ctx(Plain, plain));
            Check("with its last tag removed the pack's menu is plain again", Find(menu, "Tags") is null && Find(menu, "Early access") is not null, Headers(menu));
            Check("...while the file goes on saying so: the keys stay, empty", Key(Plain, "TagDefs") == "[]" && Key(Plain, "Tags") == "{}",
                $"{Key(Plain, "TagDefs")} {Key(Plain, "Tags")}");

            menu = ModOptionsMenu.Build(new[] { p1 }, Ctx(ModMetadataService.GlobalScope, plain));
            Check("the global defaults' menu has neither Tags nor Early access", Find(menu, "Tags") is null && Find(menu, "Early access") is null, Headers(menu));
        }

        // ── a file written carelessly ──
        Directory.CreateDirectory(Path.GetDirectoryName(MetaPath(Odd))!);
        File.WriteAllText(MetaPath(Odd), """
            {
              "Version": 2,
              "Mods": { "file:x.jar": { "Priority": 3 } },
              "TagDefs": [ "Lite", { "Name": "Vanilla", "Color": 7 }, 5, { "Color": "#112233" }, { "name": " lower ", "color": "#445566" }, { "Name": "LITE" } ],
              "Tags": { "Alpha": "Lite", "ALPHA": ["Vanilla", "lite"], "beta": [1, "Orphan"], "gamma": 5, "": ["Lite"] },
              "Sides": { "Alpha": "client", "beta": 2, "gamma": "both", "delta": true },
              "EarlyAccess": []
            }
            """);
        Meta.Invalidate(Odd);
        Check("a carelessly written file still loads: the mods' flags are not lost", Meta.Load(Odd).Mods.TryGetValue("file:x.jar", out var x) && x.Priority == 3);
        Check("...tag definitions: a bare string is a name, a nameless one, a repeat and a number are skipped",
            Names(Meta.TagDefs(Odd)) == "Lite,Vanilla,lower,Orphan", Names(Meta.TagDefs(Odd)));
        Check("...a tag on a mod that nothing defines gets a definition and a colour", Meta.TagDefs(Odd).Single(t => t.Name == "Orphan").Color is { Length: 7 });
        Check("...tags: ids lower-cased and merged, a bare string is one tag, the rest skipped", Names(Meta.TagsFor(Odd, new[] { "alpha" })) == "Lite,Vanilla"
            && Names(Meta.TagsFor(Odd, new[] { "beta" })) == "Orphan" && Meta.TagsFor(Odd, new[] { "gamma", "" }).Count == 0,
            Names(Meta.TagsFor(Odd, new[] { "alpha" })) + " / " + Names(Meta.TagsFor(Odd, new[] { "beta" })));
        // The Sides key of that file is none of the launcher's business, whatever it holds: saved, it is
        // what was written, neither tidied (an id's casing, a number, a word that is no side) nor dropped.
        Meta.Save(Odd);
        Check("...a Sides key is not read at all: it is written back as it stood, odd values and all",
            Key(Odd, "Sides") == "{\"Alpha\":\"client\",\"beta\":2,\"gamma\":\"both\",\"delta\":true}", Key(Odd, "Sides"));
        File.WriteAllText(MetaPath(Odd), "{ \"Version\": 2, \"Mods\": { \"file:x.jar\": { \"Priority\": 3 } }, \"TagDefs\": null, \"Tags\": null, \"Sides\": null }");
        Meta.Invalidate(Odd);
        Check("...and null for the keys reads as a pack without tags", Meta.TagDefs(Odd).Count == 0 && !Meta.HasTags(Odd)
            && Meta.Load(Odd).Mods.ContainsKey("file:x.jar"));

        // ── the pack's mod list: a tool writes the two tag keys back (launcher closed), then the page reads
        // them. The file gets a Sides key again too, as one may still hold it: the page must show no mod
        // with a side from it, and every save the page makes below must leave it as it is. ──
        const string pageSides = "{\"delta\":\"Client\",\"gamma\":\"Server\"}";
        var seeded = JsonNode.Parse(File.ReadAllText(MetaPath(Tagged)))!.AsObject();
        seeded["TagDefs"] = JsonNode.Parse("[{\"Name\":\"Lite\",\"Color\":\"#5B9BD5\"},{\"Name\":\"Vanilla\",\"Color\":\"#7FB069\"}]");
        seeded["Tags"] = JsonNode.Parse("{\"alpha\":[\"Lite\"],\"gamma\":[\"Lite\",\"Vanilla\"],\"delta\":[\"Vanilla\"]}");
        seeded["Sides"] = JsonNode.Parse(pageSides);
        File.WriteAllText(MetaPath(Tagged), seeded.ToJsonString());
        Meta.Invalidate(Tagged);

        var detail = FakeWorld.Detail(FakeWorld.Instances.Single(i => i.Id == Tagged));
        var page = new ModManagementView(shell, detail);
        var host = new Window
        {
            Left = -24000, Top = -24000, Width = 1180, Height = 860, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Frame { Content = page, NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden },
        };
        host.Show();
        IReadOnlyList<PackMod> PageMods() => (IReadOnlyList<PackMod>)typeof(ModManagementView).GetProperty("Mods", F)!.GetValue(page)!;
        PackMod OnPage(string file) => PageMods().Single(m => m.FileName == file);
        var list = (ItemsControl)page.FindName("ModItems");
        List<TextBlock> Pills() => Program.Find<TextBlock>(list).Where(t => t.DataContext is ModTag).ToList();
        string PillNames() => string.Join(",", Pills().Select(t => t.Text).OrderBy(s => s, StringComparer.Ordinal));
        Check("(the pack's mod list loads its mods)", WaitFor(() => PageMods().Count == 8 && Pills().Count == 4, 20000), $"{PageMods().Count} mods, {Pills().Count} pills");
        Program.Pump(1500);
        Check("the list draws a pill for every tag of every mod", PillNames() == "Lite,Lite,Vanilla,Vanilla", PillNames());
        Check("a Sides key gives no row a side: delta and gamma are named there and show as they are, at Both",
            OnPage("delta-1.0.jar").Meta.Side == ModSide.Both && !OnPage("delta-1.0.jar").IsSideRestricted
            && OnPage("gamma-1.0.jar").Meta.Side == ModSide.Both, $"delta={OnPage("delta-1.0.jar").Meta.Side} gamma={OnPage("gamma-1.0.jar").Meta.Side}");

        static double Lum(Color c) => ThemeService.Luminance(c);
        (Color Bg, Color Fg) Tones(string name)
        {
            var pill = Pills().First(t => t.Text == name);
            var bg = (VisualTreeHelper.GetParent(pill) as Border)?.Background as SolidColorBrush;
            return (bg?.Color ?? default, (pill.Foreground as SolidColorBrush)?.Color ?? default);
        }
        var surface = (Application.Current.Resources["Surface2Brush"] as SolidColorBrush)?.Color ?? Colors.Black;
        var dark = Tones("Lite");
        Check("a pill is a wash of the tag's own colour", dark.Bg is { A: 0x33, R: 0x5B, G: 0x9B, B: 0xD5 }, dark.Bg.ToString());
        Check("...with text of that hue that reads on the row", dark.Fg.A == 255 && Lum(dark.Fg) - Lum(surface) >= 0.3,
            $"{dark.Fg} on {surface}: {Lum(dark.Fg) - Lum(surface):0.00}");
        Program.Shoot((FrameworkElement)host.Content, "tags-list", 1);

        var filter = (ComboBox)page.FindName("FlagFilterBox");
        string FilterEntries() => string.Join(" | ", filter.Items.OfType<ComboBoxItem>().Skip(9).Select(i => i.Content as string));
        void PickFilter(string label)
        {
            filter.SelectedItem = filter.Items.OfType<ComboBoxItem>().First(i => i.Content as string == label);
            Program.Pump(300);
        }
        string Shown() => string.Join(",", list.Items.OfType<PackMod>().Select(m => m.FileName[..^8]).OrderBy(s => s, StringComparer.Ordinal));
        Check("the flag filter lists the pack's tags and Early access after its own entries",
            FilterEntries() == "Tag: Lite | Tag: Vanilla | Early access", FilterEntries());
        PickFilter("Tag: Lite");
        Check("filtering by a tag shows the mods that carry it", Shown() == "alpha,gamma", Shown());
        PickFilter("Tag: Vanilla");
        Check("...each tag its own", Shown() == "delta,gamma", Shown());
        PickFilter("Early access");
        Check("...and Early access the marked ones", Shown() == "alpha", Shown());
        PickFilter("Client-only");
        Check("the Client-only filter goes by the mods' own flags: the Sides key adds none (delta is not there)", Shown() == "alpha,noid,twins", Shown());
        PickFilter("All mods");
        var search = (TextBox)page.FindName("SearchBox");
        search.Text = "vanil";
        Program.Pump(300);
        Check("the search box finds mods by tag name", Shown() == "delta,gamma", Shown());
        search.Text = "";
        Program.Pump(300);

        // ── New tag, Rename, Colour and Remove, as the menu does them (the page's own context) ──
        var ctx = (ModOptionsContext)typeof(ModManagementView).GetMethod("BuildCtx", F)!.Invoke(page, null)!;
        MenuItem TagsOf(string file) => Find(ModOptionsMenu.Build(new[] { OnPage(file) }, ctx), "Tags")!;
        void Answer(string typed)
        {
            Program.Pump(400);
            var card = Program.Find<InputDialogCard>(shell).FirstOrDefault();
            if (card is null) { Check("(a prompt card is up)", false); return; }
            ((TextBox)card.FindName("InputBox")).Text = typed;
            var ok = Program.Find<Button>(card).First(b => b.Content as string == "OK");
            ok.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, ok));
            Program.Pump(500);
        }
        string? Overlay(bool confirm)
        {
            Program.Pump(400);
            var overlay = Program.Find<DialogOverlay>(shell).FirstOrDefault();
            if (overlay is null) return null;
            var message = ((TextBlock)overlay.FindName("MessageText")).Text;
            var button = (Button)overlay.FindName(confirm ? "ConfirmButton" : "CancelButton");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            Program.Pump(500);
            return message;
        }

        Click(Find(TagsOf("beta-1.0.jar"), "New tag...")!);
        Answer(" Server_Pack ");
        Check("New tag...: the tag is defined, with the next unused accent", Defs(Tagged) == "Lite#5B9BD5,Vanilla#7FB069,Server_Pack#5B9DF9", Defs(Tagged));
        Check("...and put on the mod the menu was opened on", OnPage("beta-1.0.jar").HasTag("Server_Pack") && PillNames().Contains("Server_Pack"), PillNames());
        Check("...the filter follows", FilterEntries() == "Tag: Lite | Tag: Vanilla | Tag: Server_Pack | Early access", FilterEntries());
        var section = TagsOf("beta-1.0.jar");
        Check("an underscore in a tag's name shows in the menu", Find(section, "Server__Pack") is { InputGestureText: "✓" }, Headers(section));
        var edit = Find(section, "Edit tags")!;
        Check("Edit tags lists every tag with Rename, Colour and Remove", Headers(edit) == "Lite | Vanilla | Server__Pack"
            && Headers(Find(edit, "Server__Pack")!) == "Rename... | Colour | - | Remove...", Headers(edit) + " / " + Headers(Find(edit, "Server__Pack") ?? edit));

        Click(Find(Find(edit, "Server__Pack")!, "Rename...")!);
        Answer("Vanilla");
        var refused = Overlay(confirm: true);
        Check("Rename... onto another tag's name is refused, with a reason", refused?.Contains("already has a tag called 'Vanilla'") == true
            && Defs(Tagged).Contains("Server_Pack"), refused ?? "(no message)");
        Click(Find(Find(edit, "Server__Pack")!, "Rename...")!);
        Answer("Servers");
        Check("Rename...: the tag, the mod's pill and the filter all follow", Defs(Tagged) == "Lite#5B9BD5,Vanilla#7FB069,Servers#5B9DF9"
            && OnPage("beta-1.0.jar").HasTag("Servers") && PillNames() == "Lite,Lite,Servers,Vanilla,Vanilla"
            && FilterEntries().Contains("Tag: Servers"), $"{Defs(Tagged)} / {PillNames()} / {FilterEntries()}");

        edit = Find(TagsOf("beta-1.0.jar"), "Edit tags")!;
        var colour = Find(Find(edit, "Servers")!, "Colour")!;
        Check("Colour offers the shared accents, the tag's own ticked, and the full picker", Find(colour, "Blue") is { InputGestureText: "✓" }
            && Find(colour, "Teal") is { InputGestureText: "" } && Find(colour, "Custom...") is not null && Find(colour, "No colour") is null, Headers(colour));
        Click(Find(colour, "Teal")!);
        Program.Pump(300);
        Check("...a click recolours the tag and its pill", Defs(Tagged).EndsWith("Servers#3EC8C1") && Tones("Servers").Bg is { R: 0x3E, G: 0xC8, B: 0xC1 },
            $"{Defs(Tagged)} {Tones("Servers").Bg}");
        Click(Find(colour, "Custom...")!);
        Program.Pump(500);
        if (Program.Find<ColorPickerDialog>(shell).FirstOrDefault() is { } picker)
        {
            Check("the full picker has no \"No colour\" for a tag", ((Button)picker.FindName("ClearButton")).Visibility == Visibility.Collapsed);
            ((TextBox)picker.FindName("HexBox")).Text = "#102030";
            var use = Program.Find<Button>(picker).First(b => b.Content as string == "Use colour");
            use.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, use));
            Program.Pump(500);
            var deep = Tones("Servers");
            Check("...and a colour as dark as the row still gives text that reads", Defs(Tagged).EndsWith("Servers#102030")
                && Lum(deep.Fg) - Lum(surface) >= 0.3, $"{Defs(Tagged)} {deep.Fg}: {Lum(deep.Fg) - Lum(surface):0.00}");
        }
        else Check("(the colour picker card is up)", false);
        Program.Shoot((FrameworkElement)host.Content, "tags-list-edited", 1);

        // ── a light look: the pills are rebuilt with tones for it ──
        var settings = App.State.Settings;
        settings.Look.Style = LookSettings.ClassicStyle;
        settings.Theme = new ThemeSettings { Surface = "#F2F3F5" };
        ThemeService.ApplyLook(settings);
        Program.Pump(600);
        surface = (Application.Current.Resources["Surface2Brush"] as SolidColorBrush)?.Color ?? Colors.White;
        var light = Tones("Lite");
        Check("on a light look the pill is rebuilt: a lighter wash", light.Bg is { A: 0x26, R: 0x5B, G: 0x9B, B: 0xD5 }, light.Bg.ToString());
        Check("...and text darker than the row by enough to read", Lum(surface) - Lum(light.Fg) >= 0.3, $"{light.Fg} on {surface}: {Lum(surface) - Lum(light.Fg):0.00}");
        var deepLight = Tones("Servers");
        Check("...for the dark colour too", Lum(surface) - Lum(deepLight.Fg) >= 0.3, $"{deepLight.Fg}: {Lum(surface) - Lum(deepLight.Fg):0.00}");
        Program.Shoot((FrameworkElement)host.Content, "tags-list-light", 1);
        settings.Look.Style = LookSettings.SlateStyle;
        settings.Theme = new ThemeSettings();
        ThemeService.ApplyLook(settings);
        Program.Pump(600);
        Check("back on the dark look the pills are the dark ones again", Tones("Lite").Bg.A == 0x33, Tones("Lite").Bg.ToString());

        // ── Remove ──
        edit = Find(TagsOf("beta-1.0.jar"), "Edit tags")!;
        Click(Find(Find(edit, "Servers")!, "Remove...")!);
        var asked = Overlay(confirm: false);
        Check("Remove... asks first, saying how many mods lose the tag", asked?.Contains("take it off 1 mod(s)") == true && Defs(Tagged).Contains("Servers"), asked ?? "(no question)");
        Click(Find(Find(edit, "Servers")!, "Remove...")!);
        Overlay(confirm: true);
        Check("...and on Remove the tag, its pills and its filter entry go", Defs(Tagged) == "Lite#5B9BD5,Vanilla#7FB069" && PillNames() == "Lite,Lite,Vanilla,Vanilla"
            && FilterEntries() == "Tag: Lite | Tag: Vanilla | Early access", $"{Defs(Tagged)} / {PillNames()} / {FilterEntries()}");
        Check("after every save the page made (new tag, rename, colours, remove) the file's Sides key is still as the tool wrote it",
            Key(Tagged, "Sides") == pageSides && Key(Tagged, "Tags") == "{\"alpha\":[\"Lite\"],\"delta\":[\"Vanilla\"],\"gamma\":[\"Lite\",\"Vanilla\"]}",
            $"{Key(Tagged, "Sides")} / {Key(Tagged, "Tags")}");

        // ── the shared list pane (the Mods page's rows) draws the same pills ──
        var pane = new ModListPane();
        pane.Bind(new ModListPaneHost { MetaScope = Tagged, Owner = shell });
        var paneHost = new Window
        {
            Left = -24000, Top = -24000, Width = 900, Height = 260, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = pane,
        };
        paneHost.Show();
        foreach (var file in new[] { "gamma-1.0.jar", "beta-1.0.jar" })
            pane.All.Add(new GlobalModRow { Key = OnPage(file).Key, Mod = OnPage(file), Copies = Array.Empty<GlobalModCopy>() });
        pane.ApplyFilterSort();
        Program.Pump(800);
        var panePills = string.Join(",", Program.Find<TextBlock>(pane).Where(t => t.DataContext is ModTag).Select(t => t.Text).OrderBy(s => s, StringComparer.Ordinal));
        Check("the shared list pane draws the same pills", panePills == "Lite,Vanilla", panePills);
        Program.Shoot(pane, "tags-pane", 1);

        Program.Log($"tags checks done: {_fails} failed");
        paneHost.Close();
        host.Close();
        shell.Hide();
        Program.Pump(300);
    }
}
