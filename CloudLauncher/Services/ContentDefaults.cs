using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// The rule on one library item that makes it a default: a file every compatible instance gets.
/// </summary>
/// <remarks>
/// <para>Off by default: <see cref="Enabled"/> false is an ordinary library item applied by hand,
/// which <see cref="ContentDefaultsService.MigrateManualCompatibility"/> relies on.
/// <see cref="Activate"/> also switches the item on in <c>options.txt</c> or the shader config.</para>
/// <para>Instances are matched by name globs (which survive an instance being re-made) or by exact
/// id. Exclude always beats include, and empty rules match everything.</para>
/// </remarks>
public sealed class ContentDefaultPolicy
{
    /// <summary>True when this item is one of the user's defaults: every compatible instance gets it.
    /// False leaves it an ordinary library item, applied by hand.</summary>
    public bool Enabled { get; set; }

    /// <summary>Also switch it on, not just place the file. Resource packs and shader packs only; a
    /// world has no "on".</summary>
    public bool Activate { get; set; }

    /// <summary>Where a resource pack sits in the instance's stack: higher wins, like a z-index.</summary>
    /// <remarks>Only compared between defaults. Packs the user already has in the stack are never
    /// moved (see <see cref="ContentActivationService"/>).</remarks>
    public int Order { get; set; }

    /// <summary>Minecraft versions this item is for, comma separated, in the spelling
    /// <c>ContentBundleSummary.McVersionsCsv</c> already uses. Empty means any.</summary>
    public string? McVersionsCsv { get; set; }

    /// <summary>Loaders this item is for, comma separated ("fabric,neoforge"). Empty means any.</summary>
    public string? LoadersCsv { get; set; }

    /// <summary>Globs on the instance name that this item is limited to. Empty means every instance.</summary>
    public List<string> IncludeInstances { get; set; } = new();

    /// <summary>Globs on the instance name that never get this item. Beats every include.</summary>
    public List<string> ExcludeInstances { get; set; } = new();

    /// <summary>Exact instances this item is limited to. Empty means every instance.</summary>
    public List<Guid> IncludePackIds { get; set; } = new();

    /// <summary>Exact instances that never get this item. Beats every include.</summary>
    public List<Guid> ExcludePackIds { get; set; } = new();

    /// <summary>Shader packs: require an instance that can load one (Iris, Oculus or OptiFine). On
    /// by default, since anywhere else the file does nothing.</summary>
    public bool RequireShaderLoader { get; set; } = true;

    /// <summary>Mirror of <c>ContentLibraryService.SetKeepLocal</c>, applied when the item is added as
    /// a default: never upload this item from a shared instance. Defaults to true.</summary>
    /// <remarks>The launch overlay hard-links <c>local/</c> into <c>game/</c>, and
    /// <c>PackRuleService.DefaultRules</c> shares <c>resourcepacks/</c> and <c>shaderpacks/</c>, so without
    /// this a default placed in a shared instance would be uploaded to every collaborator. Uploading it
    /// should be an explicit choice.</remarks>
    public bool KeepLocal { get; set; } = true;

    /// <summary>True when no include list narrows this item down, so it is offered to every instance
    /// that passes the version and loader rules.</summary>
    public bool AppliesToAllInstances => IncludeInstances.Count == 0 && IncludePackIds.Count == 0;

    /// <summary>True when the user has written any rule at all, i.e. the item is not simply
    /// "everywhere".</summary>
    public bool HasAnyRule =>
        !string.IsNullOrWhiteSpace(McVersionsCsv) || !string.IsNullOrWhiteSpace(LoadersCsv)
        || IncludeInstances.Count > 0 || ExcludeInstances.Count > 0
        || IncludePackIds.Count > 0 || ExcludePackIds.Count > 0;

    /// <summary>One line for a row subtitle: versions, loaders and instance opt-outs.</summary>
    public string RuleSummary => ContentCompatibility.Describe(this);

    /// <summary>A separate object with the same values, for an editor that must be able to cancel.</summary>
    public ContentDefaultPolicy Clone() => new()
    {
        Enabled = Enabled,
        Activate = Activate,
        Order = Order,
        McVersionsCsv = McVersionsCsv,
        LoadersCsv = LoadersCsv,
        IncludeInstances = new List<string>(IncludeInstances),
        ExcludeInstances = new List<string>(ExcludeInstances),
        IncludePackIds = new List<Guid>(IncludePackIds),
        ExcludePackIds = new List<Guid>(ExcludePackIds),
        RequireShaderLoader = RequireShaderLoader,
        KeepLocal = KeepLocal,
    };

    /// <summary>A policy that does nothing: an ordinary, manual library item.</summary>
    /// <remarks>Leaves <see cref="KeepLocal"/> true: the policy editor starts from this, and the flag
    /// has no effect until the policy is enabled.</remarks>
    public static ContentDefaultPolicy Off() => new();

    /// <summary>
    /// A default seeded from one instance's version and loader, as "add from pack, make it a default"
    /// produces. Without the seeding, a 1.21 pack would land in 1.16 instances.
    /// </summary>
    public static ContentDefaultPolicy FromInstance(PackSummary instance, bool activate = false) => new()
    {
        Enabled = true,
        Activate = activate,
        McVersionsCsv = ContentDefaults.VersionCsvFor(instance),
        LoadersCsv = ContentDefaults.LoaderCsvFor(instance),
    };
}

/// <summary>
/// The rule on one folder on the Worlds, Resource packs, Shader packs or Mods page: where the
/// things in it may be used.
/// </summary>
/// <remarks>
/// <para>Separate from <see cref="ContentDefaultPolicy"/>: activation, order, shader loader and
/// KeepLocal stay per-item decisions that a folder can't override.</para>
/// <para>An empty rule admits everything, so an unedited folder changes nothing. It intersects with
/// the item's own rule (<see cref="ContentCompatibility.FolderAdmits"/>), and per-instance choices
/// (<see cref="InstanceContentOverrides"/>) beat both.</para>
/// </remarks>
public sealed class ContentFolderRule
{
    /// <summary>True when this folder's narrowing is consulted at all. False leaves the folder
    /// admitting every instance while keeping whatever the user typed.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minecraft versions the things in this folder are for, comma separated, in the spelling
    /// <see cref="ContentCompatibility.VersionMatches"/> reads: exact, family, wildcard or a range.
    /// Empty means any.</summary>
    public string? McVersionsCsv { get; set; }

    /// <summary>Loaders the things in this folder are for, comma separated ("fabric,neoforge"). Empty
    /// means any.</summary>
    public string? LoadersCsv { get; set; }

    /// <summary>Globs on the instance name this folder is limited to. Empty means every instance.</summary>
    public List<string> IncludeInstances { get; set; } = new();

    /// <summary>Globs on the instance name that never get anything from this folder. Beats every
    /// include, on this side and on the item's.</summary>
    public List<string> ExcludeInstances { get; set; } = new();

    /// <summary>Exact instances this folder is limited to. Empty means every instance.</summary>
    public List<Guid> IncludePackIds { get; set; } = new();

    /// <summary>Exact instances that never get anything from this folder.</summary>
    public List<Guid> ExcludePackIds { get; set; } = new();

    /// <summary>
    /// Which folder this rule came from, filled in by <see cref="ContentFolderRules.Effective"/>.
    /// </summary>
    /// <remarks>Not stored (the dictionary key is the name). Lets the matcher name the folder when it
    /// refuses an instance, so the user can find the rule to change.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FolderName { get; set; }

    /// <summary>
    /// Further folder rules that must also admit an instance.
    /// </summary>
    /// <remarks>An item can be in several folders and must satisfy all of their rules. Csv rules can't
    /// be merged into one (joining version lists would OR them), so the extra folders are carried here
    /// and checked by <see cref="ContentCompatibility.FolderAdmits"/>. Not stored; only set on the object
    /// <see cref="ContentFolderRules.Effective"/> builds.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<ContentFolderRule> AndAlso { get; set; } = new();

    /// <summary>True when no include list narrows this folder down, so everything in it is offered to
    /// every instance that passes the version and loader rules.</summary>
    public bool AppliesToAllInstances => IncludeInstances.Count == 0 && IncludePackIds.Count == 0;

    /// <summary>True when the user has written any narrowing at all. A folder with none admits
    /// everything, whatever <see cref="Enabled"/> says.</summary>
    public bool HasAnyRule =>
        !string.IsNullOrWhiteSpace(McVersionsCsv) || !string.IsNullOrWhiteSpace(LoadersCsv)
        || IncludeInstances.Count > 0 || ExcludeInstances.Count > 0
        || IncludePackIds.Count > 0 || ExcludePackIds.Count > 0;

    /// <summary>True when this rule narrows anything: enabled and non-empty.</summary>
    public bool IsActive => Enabled && HasAnyRule;

    /// <summary>One line for a chip tooltip summarising the rule.</summary>
    public string RuleSummary => ContentCompatibility.Describe(this);

    /// <summary>A separate object with the same values, for an editor that must be able to cancel.</summary>
    /// <remarks><see cref="AndAlso"/> is copied shallowly: its entries belong to other folders, and
    /// editors never modify them.</remarks>
    public ContentFolderRule Clone() => new()
    {
        Enabled = Enabled,
        McVersionsCsv = McVersionsCsv,
        LoadersCsv = LoadersCsv,
        IncludeInstances = new List<string>(IncludeInstances),
        ExcludeInstances = new List<string>(ExcludeInstances),
        IncludePackIds = new List<Guid>(IncludePackIds),
        ExcludePackIds = new List<Guid>(ExcludePackIds),
        FolderName = FolderName,
        AndAlso = new List<ContentFolderRule>(AndAlso),
    };
}

/// <summary>What one step of a <see cref="ContentDefaultPlan"/> would do, or why it will not.</summary>
public enum ContentStepKind
{
    /// <summary>Put the file into the instance (hard-link it in, or unpack a bundle).</summary>
    Apply = 0,
    /// <summary>Copy a world template into the instance's <c>saves/</c>. Never a link, since a save
    /// can't be shared (see <see cref="ContentLibraryService"/>).</summary>
    CopyWorld,
    /// <summary>Write it into options.txt or the shader config.</summary>
    Activate,
    /// <summary>Take it back out: it fell out of compatibility, or this instance opted out.</summary>
    RemoveApply,
    /// <summary>Switch off something this engine switched on earlier. Never anything the user chose.</summary>
    Deactivate,
    /// <summary>The instance has its own file of that name, which wins at launch, or its own edited
    /// copy of ours. Nothing is written.</summary>
    BlockedShadowed,
    /// <summary>A shader pack with nothing in the instance that can load one.</summary>
    BlockedNoLoader,
    /// <summary>The game is running in this instance, so nothing may be written into it.</summary>
    BlockedRunning,
    /// <summary>Two shader defaults want the same instance, and only one shader can be active.</summary>
    BlockedShaderConflict,
    /// <summary>The user excluded this item from this instance, or opted the instance out entirely.</summary>
    SkippedExcluded,
    /// <summary>The item's own rules do not cover this instance.</summary>
    SkippedIncompatible,
    /// <summary>Already in the state the rules ask for.</summary>
    NothingToDo
}

/// <summary>One thing the reconciler would do to one instance, and the sentence explaining it.</summary>
/// <param name="Why">A sentence for the user, shown on the pages.</param>
public sealed record ContentDefaultStep(LibraryItem Item, ContentStepKind Kind, string Why)
{
    /// <summary>True for a step that wants to touch the disk, which is what the busy check and the
    /// "apply now" button care about.</summary>
    public bool Writes => Kind is ContentStepKind.Apply or ContentStepKind.CopyWorld
        or ContentStepKind.Activate or ContentStepKind.RemoveApply or ContentStepKind.Deactivate;

    /// <summary>True when something is stopping this step; worth showing the user, unlike a skip.</summary>
    public bool IsBlocked => Kind is ContentStepKind.BlockedShadowed or ContentStepKind.BlockedNoLoader
        or ContentStepKind.BlockedRunning or ContentStepKind.BlockedShaderConflict;

    /// <summary>Short label for the row: "Will be added", "Blocked", "Already there".</summary>
    public string KindLabel => Kind switch
    {
        ContentStepKind.Apply => "will be added",
        ContentStepKind.CopyWorld => "will be copied in",
        ContentStepKind.Activate => "will be switched on",
        ContentStepKind.RemoveApply => "will be removed",
        ContentStepKind.Deactivate => "will be switched off",
        ContentStepKind.BlockedShadowed => "blocked - this instance has its own",
        ContentStepKind.BlockedNoLoader => "blocked - no shader loader here",
        ContentStepKind.BlockedRunning => "blocked - the game is running",
        ContentStepKind.BlockedShaderConflict => "blocked - another shader default won",
        ContentStepKind.SkippedExcluded => "turned off for this instance",
        ContentStepKind.SkippedIncompatible => "not for this instance",
        _ => "nothing to do"
    };
}

/// <summary>Everything the defaults would do to one instance, worked out without writing anything.</summary>
public sealed record ContentDefaultPlan(Guid PackId, string PackName, IReadOnlyList<ContentDefaultStep> Steps)
{
    /// <summary>An empty plan for an instance that has no defaults to think about.</summary>
    public static ContentDefaultPlan Empty(Guid packId, string packName) => new(packId, packName, []);

    public IEnumerable<ContentDefaultStep> Of(ContentStepKind kind) => Steps.Where(s => s.Kind == kind);

    /// <summary>The steps worth putting in front of the user: something is stopping them.</summary>
    public IEnumerable<ContentDefaultStep> Blocked => Steps.Where(s => s.IsBlocked);

    /// <summary>True when running this plan would change anything on disk.</summary>
    public bool HasWork => Steps.Any(s => s.Writes);

    /// <summary>One status-bar line, in the same counts-then-the-one-problem shape
    /// <c>LibraryApplyResult.Summary</c> reports in.</summary>
    public string Summary()
    {
        var parts = new List<string>();
        var add = Steps.Count(s => s.Kind is ContentStepKind.Apply or ContentStepKind.CopyWorld);
        var on = Steps.Count(s => s.Kind == ContentStepKind.Activate);
        var off = Steps.Count(s => s.Kind == ContentStepKind.Deactivate);
        var gone = Steps.Count(s => s.Kind == ContentStepKind.RemoveApply);
        var blocked = Steps.Count(s => s.IsBlocked);

        if (add > 0) parts.Add($"{add} to add");
        if (on > 0) parts.Add($"{on} to switch on");
        if (off > 0) parts.Add($"{off} to switch off");
        if (gone > 0) parts.Add($"{gone} to remove");
        if (blocked > 0)
        {
            var first = Steps.First(s => s.IsBlocked);
            parts.Add($"{blocked} blocked - {first.Item.DisplayName}: {first.Why}");
        }
        return parts.Count == 0 ? "Everything is already as your defaults ask." : string.Join("  ·  ", parts);
    }
}

/// <summary>
/// The one rule about when the launcher may write content into an instance.
/// </summary>
/// <remarks>
/// <para><see cref="MinecraftInstanceService.IsBusy"/> is already true while Launching, because
/// <c>BeginLaunch</c> runs before files are prepared. The launch path must still write then: the JVM
/// hasn't started and options.txt hasn't been read.</para>
/// <para>A Running game is never safe: it holds the save open and rewrites options.txt from memory
/// on exit, so a write underneath it is lost and can clobber the player's settings.</para>
/// </remarks>
public static class ContentWriteGate
{
    /// <summary>True when it is safe to write into <paramref name="packId"/> right now.</summary>
    /// <param name="duringLaunch">True for the launch path, which is allowed to write while the
    /// instance is Launching. False everywhere else, where Launching means somebody else is already
    /// preparing this instance.</param>
    /// <param name="why">A sentence for the user when the answer is no; empty when it is yes.</param>
    public static bool Allows(MinecraftInstanceService instances, Guid packId, bool duringLaunch, out string why)
    {
        why = "";
        var status = instances.GetStatus(packId);
        if (status == MinecraftInstanceStatus.Running)
        {
            why = "Minecraft is running in this instance - its files are left alone until it closes.";
            return false;
        }
        if (status == MinecraftInstanceStatus.Launching && !duringLaunch)
        {
            why = "This instance is launching - its files are left alone until that finishes.";
            return false;
        }
        return true;
    }
}

/// <summary>
/// The csv format shared by the policy, the bundle DTOs and the store filters.
/// </summary>
public static class ContentDefaults
{
    private static readonly char[] Separators = [',', ';'];

    /// <summary>The non-empty, trimmed entries of a csv. An empty or null csv gives nothing, which
    /// every caller reads as "no rule".</summary>
    public static List<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Back to a csv, or null when empty. The matcher treats null and "" alike, and null is
    /// how "no rule" is stored.</summary>
    public static string? Join(IEnumerable<string>? values)
    {
        var list = (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : string.Join(",", list);
    }

    /// <summary>A version rule matching only this instance's version, for seeding a default from it.</summary>
    public static string? VersionCsvFor(PackSummary instance) =>
        string.IsNullOrWhiteSpace(instance.MinecraftVersion) ? null : instance.MinecraftVersion.Trim();

    /// <summary>The loader rule "this instance's loader". Vanilla (<see cref="LoaderKind.None"/>)
    /// produces no rule at all: "no loader" is not a loader to filter on, and writing one would stop
    /// the item reaching any modded instance.</summary>
    public static string? LoaderCsvFor(PackSummary instance) =>
        instance.Loader == LoaderKind.None ? null : instance.Loader.ToString();
}
