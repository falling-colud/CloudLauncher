using System.Collections.Specialized;
using System.Text.Json;
using CloudLauncher.Shared;

namespace Showcase;

/// <summary>A small fictional CloudLauncher account: Mara, her friends, their instances and the
/// things they host. Everything the fake server answers comes from here, built from the real DTOs so
/// the shapes can never drift from what the launcher reads.</summary>
public static class FakeWorld
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── people ───────────────────────────────────────────────────────────────
    public static readonly Guid Mara = G(1), Jun = G(2), Tobi = G(3), Ivy = G(4), Sol = G(5);
    public static string Name(Guid id) => id == Mara ? "mara" : id == Jun ? "jun" : id == Tobi ? "tobi" : id == Ivy ? "ivy" : "sol";

    public static readonly Guid FridayCrew = G(20), BuildTeam = G(21);

    // ── instances ────────────────────────────────────────────────────────────
    public static readonly Guid Hearthstone = G(100), Skyline = G(101), DeepDark = G(102), FridaySmp = G(103),
                                VanillaPlus = G(104), Workshop = G(105), IvysGarden = G(106), Lighthouse = G(107);

    public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public sealed record Instance(Guid Id, string Name, Guid Owner, PackVisibility Visibility, bool IsShared,
        string Mc, LoaderKind Loader, string LoaderVersion, PackPermissions MyPermissions, bool InMyLibrary,
        TimeSpan Age, string Summary,
        (Guid User, PackPermissions P)[] Collaborators, (Guid Team, PackPermissions P)[] Teams);

    public static readonly Instance[] Instances =
    [
        new(Hearthstone, "Hearthstone Valley", Mara, PackVisibility.Private, true, "1.21.1", LoaderKind.NeoForge, "21.1.215",
            PackPermissions.Full, true, TimeSpan.FromHours(3), "Slow, cosy survival with farming, cooking and a lot of lanterns.",
            [(Jun, PackPermissions.Contributor), (Tobi, PackPermissions.ReadOnly)], [(FridayCrew, PackPermissions.ReadOnly)]),
        new(Skyline, "Skyline Engineering", Mara, PackVisibility.Public, true, "1.21.1", LoaderKind.NeoForge, "21.1.215",
            PackPermissions.Full, true, TimeSpan.FromDays(2), "Trains, cranes and a sky that is never quite big enough.",
            [], []),
        new(DeepDark, "Deep Dark Expedition", Jun, PackVisibility.Private, true, "1.20.1", LoaderKind.Forge, "47.3.0",
            PackPermissions.Contributor, true, TimeSpan.FromHours(20), "Jun's cave-diving pack. Bring torches.",
            [(Mara, PackPermissions.Contributor)], []),
        new(FridaySmp, "Friday Night SMP", Tobi, PackVisibility.Team, true, "1.21.1", LoaderKind.Fabric, "0.16.9",
            PackPermissions.ReadOnly, true, TimeSpan.FromDays(1), "The server pack for Friday nights.",
            [], [(FridayCrew, PackPermissions.ReadOnly)]),
        new(VanillaPlus, "Vanilla Plus", Mara, PackVisibility.Private, false, "1.21.4", LoaderKind.Fabric, "0.16.9",
            PackPermissions.Full, true, TimeSpan.FromDays(6), "Sodium, Iris and nothing that changes the game.",
            [], []),
        new(Workshop, "Tinker's Workshop", Mara, PackVisibility.Private, true, "1.20.1", LoaderKind.Forge, "47.3.0",
            PackPermissions.Full, true, TimeSpan.FromDays(9), "Test bench for modpack ideas.",
            [], []),
        new(IvysGarden, "Ivy's Garden", Ivy, PackVisibility.Private, true, "1.21.1", LoaderKind.NeoForge, "21.1.215",
            PackPermissions.ReadOnly, false, TimeSpan.FromHours(7), "Flowers, bees and far too many decorative blocks.",
            [(Mara, PackPermissions.ReadOnly)], []),
    ];

    public static PackSummary Summary(Instance i) => new(
        i.Id, i.Name, i.Summary, i.Owner, Name(i.Owner), i.Visibility, i.IsShared, false, i.Mc, i.Loader, i.LoaderVersion,
        Now - i.Age - TimeSpan.FromDays(30), Now - i.Age, i.MyPermissions, i.Summary,
        i.Owner, Name(i.Owner), Now - i.Age);

    public static PackDetail Detail(Instance i)
    {
        var pending = i.Id == Hearthstone
            ? new List<PackInvitationEntry>
            {
                new(G(900), i.Id, i.Name, Sol, "sol", Mara, "mara", PackPermissions.ReadOnly, "Friday's world is on this one",
                    Now - TimeSpan.FromHours(5), Now + TimeSpan.FromDays(6), null, null, false, "tok-sol-hearth"),
                new(G(901), i.Id, i.Name, null, null, Mara, "mara", PackPermissions.ReadOnly, null,
                    Now - TimeSpan.FromDays(1), Now + TimeSpan.FromDays(13), null, null, true, "tok-link-hearth")
            }
            : new List<PackInvitationEntry>();
        return new PackDetail(i.Id, i.Name, i.Summary, i.Owner, Name(i.Owner), i.Visibility, i.IsShared, false, i.Mc,
            i.Loader, i.LoaderVersion, Now - i.Age - TimeSpan.FromDays(30), Now - i.Age, i.MyPermissions,
            i.Collaborators.Select(c => new PackCollaboratorEntry(c.User, Name(c.User), c.P)).ToList(),
            i.Teams.Select(t => new PackTeamEntry(t.Team, TeamName(t.Team), t.P, TeamSize(t.Team), TeamMembers(t.Team))).ToList(),
            [], i.Summary, i.Owner, Name(i.Owner), Now - i.Age,
            i.Owner == Mara && i.Id == Hearthstone ? "tok-link-hearth" : null, pending);
    }

    public static string TeamName(Guid t) => t == FridayCrew ? "Friday Crew" : "Build Team";
    public static int TeamSize(Guid t) => t == FridayCrew ? 4 : 2;
    public static IReadOnlyList<string> TeamMembers(Guid t) => t == FridayCrew ? ["tobi", "mara", "jun", "sol"] : ["ivy", "mara"];

    // ── hosted things ────────────────────────────────────────────────────────
    public enum Fam { Mod, World, Rp, Bundle }

    public sealed record Hosted(Fam Family, Guid Id, string Name, Guid Owner, PackVisibility Visibility,
        PackPermissions MyPermissions, int Versions, TimeSpan Age, string Summary, BundleKind Kind,
        (Guid User, PackPermissions P)[] Collaborators, (Guid Team, PackPermissions P)[] Teams,
        bool SharedDirect, bool SharedTeam, bool ShareLink = false);

    public static readonly Hosted[] HostedItems =
    [
        new(Fam.Mod, G(300), "Lantern Tweaks", Mara, PackVisibility.Private, PackPermissions.Full, 4, TimeSpan.FromHours(9),
            "Small fixes to how lanterns hang and glow.", default, [(Jun, PackPermissions.Contributor)], [], false, false),
        new(Fam.Mod, G(301), "Quiet Ambience", Mara, PackVisibility.Public, PackPermissions.Full, 7, TimeSpan.FromDays(3),
            "Turns down the cave noises. Only the cave noises.", default, [], [], false, false),
        new(Fam.Mod, G(302), "Old Test Mod", Mara, PackVisibility.Private, PackPermissions.Full, 1, TimeSpan.FromDays(40),
            "Nothing to see here.", default, [], [], false, false),
        new(Fam.Mod, G(303), "Compass HUD", Jun, PackVisibility.Private, PackPermissions.Contributor, 3, TimeSpan.FromHours(30),
            "A compass strip at the top of the screen.", default, [(Mara, PackPermissions.Contributor)], [], true, false),
        new(Fam.World, G(310), "Valley Survival", Mara, PackVisibility.Private, PackPermissions.Full, 12, TimeSpan.FromHours(4),
            "Our Hearthstone Valley world, backed up after every session.", default, [], [(FridayCrew, PackPermissions.ReadOnly)], false, false),
        new(Fam.World, G(311), "Skyblock Start", Tobi, PackVisibility.Team, PackPermissions.ReadOnly, 2, TimeSpan.FromDays(4),
            "Tobi's skyblock island, before anyone touched it.", default, [], [(FridayCrew, PackPermissions.ReadOnly)], false, true),
        new(Fam.Rp, G(320), "Soft Ores", Mara, PackVisibility.Public, PackPermissions.Full, 5, TimeSpan.FromDays(5),
            "Ores you can spot from across a cave without them shouting.", default, [], [], false, false),
        new(Fam.Rp, G(321), "Ivy Leaves", Ivy, PackVisibility.Private, PackPermissions.ReadOnly, 2, TimeSpan.FromHours(50),
            "Bushier leaves.", default, [(Mara, PackPermissions.ReadOnly)], [], true, false),
        new(Fam.Bundle, G(330), "Valley Shaders", Mara, PackVisibility.Private, PackPermissions.Full, 3, TimeSpan.FromHours(12),
            "Our shader settings, tuned for the valley at dusk.", BundleKind.ShaderPack, [(Jun, PackPermissions.ReadOnly)], [], false, false, ShareLink: true),
        new(Fam.Bundle, G(331), "Crew Scripts", Tobi, PackVisibility.Private, PackPermissions.Contributor, 6, TimeSpan.FromHours(26),
            "KubeJS recipes for the Friday pack.", BundleKind.KubeJsBundle, [], [(FridayCrew, PackPermissions.Contributor)], false, true),
    ];

    static HostedModSummary ModSummary(Hosted h) => new(h.Id, Slug(h.Name), h.Name, h.Summary, h.Owner, Name(h.Owner), h.Visibility,
        null, "1.21.1", "neoforge", 120 * h.Versions, Now - h.Age - TimeSpan.FromDays(60), Now - h.Age, h.MyPermissions, h.Versions);
    static SharedWorldSummary WorldSummary(Hosted h) => new(h.Id, Slug(h.Name), h.Name, h.Summary, h.Owner, Name(h.Owner), h.Visibility,
        null, "1.21.1", Now - h.Age - TimeSpan.FromDays(60), Now - h.Age, h.MyPermissions, h.Versions);
    static HostedResourcePackSummary RpSummary(Hosted h) => new(h.Id, Slug(h.Name), h.Name, h.Summary, h.Owner, Name(h.Owner), h.Visibility,
        null, "1.21.1", 80 * h.Versions, Now - h.Age - TimeSpan.FromDays(60), Now - h.Age, h.MyPermissions, h.Versions);
    static ContentBundleSummary BundleSummary(Hosted h) => new(h.Id, h.Kind, Slug(h.Name), h.Name, h.Summary, h.Owner, Name(h.Owner),
        h.Visibility, null, BundleTargets.DefaultFor(h.Kind), "1.21.1", "neoforge", 40 * h.Versions,
        Now - h.Age - TimeSpan.FromDays(60), Now - h.Age, h.MyPermissions, h.Versions);

    static List<PackCollaboratorEntry> Collabs(Hosted h) =>
        h.Collaborators.Select(c => new PackCollaboratorEntry(c.User, Name(c.User), c.P)).ToList();
    static List<PackTeamEntry> Teams(Hosted h) =>
        h.Teams.Select(t => new PackTeamEntry(t.Team, TeamName(t.Team), t.P, TeamSize(t.Team), TeamMembers(t.Team))).ToList();

    static IEnumerable<Hosted> Source(Fam fam, string source) => HostedItems.Where(h => h.Family == fam).Where(h => source switch
    {
        "Personal" => h.Owner == Mara,
        "Shared" => h.SharedDirect,
        "Team" => h.SharedTeam,
        _ => h.Visibility == PackVisibility.Public
    });

    // ── invitations ──────────────────────────────────────────────────────────
    static MyInvitations MyInvites() => new(
        [new PackInvitationEntry(G(910), Lighthouse, "Sol's Lighthouse", Mara, "mara", Sol, "sol", PackPermissions.Contributor,
            "Come help with the keeper's cottage", Now - TimeSpan.FromMinutes(40), Now + TimeSpan.FromDays(7), null, null, false, "tok-sol-light")],
        [],
        [new BundleInvitationEntry(G(911), G(332), "Garden Config Set", BundleKind.ConfigBundle, Mara, "mara", Ivy, "ivy",
            PackPermissions.ReadOnly, null, Now - TimeSpan.FromHours(2), null, null, null, false, "tok-ivy-cfg")]);

    // ── routing ──────────────────────────────────────────────────────────────

    /// <summary>Answers one request, or null for a 404.</summary>
    public static object? Answer(string method, string path, NameValueCollection q)
    {
        var source = q["source"] ?? "Public";
        if (method != "GET") return null;

        switch (path)
        {
            case "auth/me": return new UserSummary(Mara, "mara", true);
            case "packs": return Instances.Where(i => i.InMyLibrary).Select(Summary).ToList();
            case "teams":
                return new List<TeamSummary>
                {
                    new(FridayCrew, "Friday Crew", Tobi, "tobi", 4, 1, 0, 1, 0, 1, TeamRole.Member),
                    new(BuildTeam, "Build Team", Ivy, "ivy", 2, 0, 0, 0, 0, 0, TeamRole.Admin)
                };
            case "invitations": return MyInvites();
            case "teams/invitations": return new List<TeamInvitationEntry>();
            case "packs/browse":
            {
                var items = source switch
                {
                    "Shared" => Instances.Where(i => i.Owner != Mara && i.Collaborators.Any(c => c.User == Mara)),
                    "Team" => Instances.Where(i => i.Owner != Mara && i.Teams.Any()),
                    "Personal" => Instances.Where(i => i.Owner == Mara),
                    _ => Instances.Where(i => i.Visibility == PackVisibility.Public)
                };
                var list = items.Select(Summary).ToList();
                return new PackBrowsePage(list, 0, 100, list.Count);
            }
            case "mods/browse":
            {
                var list = Source(Fam.Mod, source).Select(ModSummary).ToList();
                return new ModBrowsePage(list, 0, 100, list.Count);
            }
            case "worlds/browse":
            {
                var list = Source(Fam.World, source).Select(WorldSummary).ToList();
                return new WorldBrowsePage(list, 0, 100, list.Count);
            }
            case "resourcepacks/browse":
            {
                var list = Source(Fam.Rp, source).Select(RpSummary).ToList();
                return new ResourcePackBrowsePage(list, 0, 100, list.Count);
            }
            case "bundles":
            {
                var list = Source(Fam.Bundle, source).Select(BundleSummary).ToList();
                return new BundleBrowsePage(list, 0, 100, list.Count);
            }
        }

        var parts = path.Split('/');
        if (parts.Length == 2 && Guid.TryParse(parts[1], out var id))
        {
            switch (parts[0])
            {
                case "packs" when Instances.FirstOrDefault(i => i.Id == id) is { } inst: return Detail(inst);
                case "mods" when HostedItems.FirstOrDefault(h => h.Id == id && h.Family == Fam.Mod) is { } m:
                    return new HostedModDetail(m.Id, Slug(m.Name), m.Name, m.Summary, m.Summary, m.Owner, Name(m.Owner), m.Visibility,
                        null, "1.21.1", "neoforge", Now - m.Age - TimeSpan.FromDays(60), Now - m.Age, m.MyPermissions, [], Collabs(m), Teams(m));
                case "worlds" when HostedItems.FirstOrDefault(h => h.Id == id && h.Family == Fam.World) is { } w:
                    return new SharedWorldDetail(w.Id, Slug(w.Name), w.Name, w.Summary, w.Summary, w.Owner, Name(w.Owner), w.Visibility,
                        null, "1.21.1", Now - w.Age - TimeSpan.FromDays(60), Now - w.Age, w.MyPermissions, [], Collabs(w), Teams(w));
                case "resourcepacks" when HostedItems.FirstOrDefault(h => h.Id == id && h.Family == Fam.Rp) is { } r:
                    return new HostedResourcePackDetail(r.Id, Slug(r.Name), r.Name, r.Summary, r.Summary, r.Owner, Name(r.Owner), r.Visibility,
                        null, "1.21.1", Now - r.Age - TimeSpan.FromDays(60), Now - r.Age, r.MyPermissions, [], Collabs(r), Teams(r));
                case "bundles" when HostedItems.FirstOrDefault(h => h.Id == id && h.Family == Fam.Bundle) is { } b:
                    return new ContentBundleDetail(b.Id, b.Kind, Slug(b.Name), b.Name, b.Summary, b.Summary, b.Owner, Name(b.Owner),
                        b.Visibility, null, BundleTargets.DefaultFor(b.Kind), "1.21.1", "neoforge", 40 * b.Versions,
                        Now - b.Age - TimeSpan.FromDays(60), Now - b.Age, b.MyPermissions, [], Collabs(b), Teams(b),
                        b.ShareLink && b.Owner == Mara ? "tok-link-shaders" : null);
            }
        }
        if (parts.Length == 3 && parts[0] == "bundles" && parts[2] == "invitations" && Guid.TryParse(parts[1], out var bid)
            && HostedItems.FirstOrDefault(h => h.Id == bid) is { Owner: var owner } && owner == Mara)
        {
            return new List<BundleInvitationEntry>
            {
                new(G(920), bid, "Valley Shaders", BundleKind.ShaderPack, null, null, Mara, "mara", PackPermissions.ReadOnly,
                    null, Now - TimeSpan.FromDays(2), null, null, null, true, "tok-link-shaders")
            };
        }
        return null;
    }

    public static string Slug(string s) => new(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    public static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
}
