using System.IO;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>What a page is importing. One value per content page's primary action.</summary>
/// <remarks>Separate from <see cref="LibraryKind"/>, <see cref="BundleKind"/> and
/// <see cref="SharedFamily"/>, which each cover only what their own subsystem stores.
/// <see cref="ImportContentSpec"/> maps this onto them.</remarks>
public enum ImportContentKind
{
    Mod,
    ResourcePack,
    ShaderPack,
    World,
    ConfigBundle,
    KubeJsBundle,
    DataPack,
    Modpack
}

/// <summary>Everything the import flow needs to know about one kind of content: what to call it,
/// what to offer in the file picker, where it lands inside an instance, which subsystem stores it,
/// and which hosting fields its server endpoint has.</summary>
/// <remarks>
/// <para>Bundle nouns and folders come from <see cref="ContentBundleService.KindLabel"/> and
/// <see cref="BundleTargets.DefaultFor"/>; the other folder names are Minecraft's own.</para>
/// <para>The library kind is a name parsed by <see cref="ResolveLibraryKind"/>, so a name the
/// <see cref="LibraryKind"/> enum doesn't have resolves to null and the per-kind placement path is
/// used.</para>
/// </remarks>
/// <param name="Kind">The content kind this row describes.</param>
/// <param name="Noun">Sentence-case name of one of these, e.g. "Resource pack". Lower-case it with
/// <c>ToLowerInvariant()</c> mid-sentence ("Host a shader pack").</param>
/// <param name="PluralNoun">Plural of <paramref name="Noun"/>, for "3 shader packs".</param>
/// <param name="FileFilter">An <c>OpenFileDialog.Filter</c>.</param>
/// <param name="AllowsFolder">True when an unpacked folder is as valid a source as an archive: a
/// save folder, an unzipped resource or shader pack, a folder of config files.</param>
/// <param name="InstanceFolder">Where a copy lands inside an instance's <c>game/</c>. Empty for
/// <see cref="ImportContentKind.Modpack"/>, which creates an instance instead.</param>
/// <param name="LibraryKindName">Name of the <see cref="LibraryKind"/> member that stores this, or
/// null when the library is not the right home for it.</param>
/// <param name="LibraryPlacesIntoInstances">True when <see cref="ContentLibraryService.ApplyAsync"/>
/// also puts the file into instances. Worlds are copied rather than linked, and config and KubeJS
/// bundles unpack into a tree of files, so those keep their own placement path.</param>
/// <param name="HostBundleKind">Set when hosting goes through the content-bundle family. Mods,
/// resource packs and worlds have their own endpoints.</param>
/// <param name="CanHost">False only for a modpack, which the server has no page type for.</param>
/// <param name="WantsInstanceStep">False only for a modpack: importing one creates an
/// instance.</param>
/// <param name="WantsLoaders">The hosting form offers loader checkboxes (jars, config folders and
/// KubeJS scripts are loader-specific).</param>
/// <param name="WantsMcVersions">The hosting form offers Minecraft versions.</param>
/// <param name="SingleMcVersion">The endpoint stores one version instead of a CSV. Only worlds,
/// whose DTO has <c>McVersion</c> and not <c>McVersionsCsv</c>.</param>
/// <param name="WantsChannel">The endpoint stores a release channel. Worlds don't.</param>
/// <param name="MaxHostBytes">The server's <c>RequestSizeLimit</c> for this family, so an oversized
/// file is caught before the upload.</param>
/// <param name="Scope">Which scan caches a write of this kind invalidates, so the page behind the
/// card re-reads from disk.</param>
public sealed record ImportContentSpec(
    ImportContentKind Kind,
    string Noun,
    string PluralNoun,
    string FileFilter,
    bool AllowsFolder,
    string InstanceFolder,
    string? LibraryKindName,
    bool LibraryPlacesIntoInstances,
    BundleKind? HostBundleKind,
    bool CanHost,
    bool WantsInstanceStep,
    bool WantsLoaders,
    bool WantsMcVersions,
    bool SingleMcVersion,
    bool WantsChannel,
    long MaxHostBytes,
    ScanScope Scope)
{
    /// <summary>Server upload limit per mod jar, resource-pack zip and bundle zip (same in all three
    /// controllers).</summary>
    private const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>Server upload limit per world zip (see <c>SharedWorldsController</c>).</summary>
    private const long MaxWorldBytes = 256L * 1024 * 1024;

    /// <summary>The <see cref="LibraryKind"/> that stores this kind, or null when the library can't
    /// hold it and the per-kind copy path does the write.</summary>
    /// <remarks>Parsed case-insensitively from the name; see <see cref="ImportContentSpec"/>.</remarks>
    public LibraryKind? ResolveLibraryKind() =>
        LibraryKindName is { Length: > 0 } name && Enum.TryParse<LibraryKind>(name, ignoreCase: true, out var kind)
            ? kind
            : null;

    /// <summary>True when the launcher can keep one copy of this outside every instance, so "keep it in
    /// my library only" works for this kind.</summary>
    /// <remarks>Not the same as <see cref="LibraryPlacesIntoInstances"/>: some kinds are stored but not
    /// handed out.</remarks>
    public bool LibraryCanHold => ResolveLibraryKind() is not null;

    /// <summary>The library both keeps this kind and is what puts it into an instance.</summary>
    public bool LibraryApplies => LibraryPlacesIntoInstances && LibraryCanHold;

    /// <summary>The noun as it reads mid-sentence, e.g. "resource pack".</summary>
    public string LowerNoun => Noun.ToLowerInvariant();

    /// <summary>The plural as it reads mid-sentence, e.g. "resource packs".</summary>
    public string LowerPluralNoun => PluralNoun.ToLowerInvariant();

    /// <summary>Every row, in the order the enum declares them.</summary>
    public static IReadOnlyList<ImportContentSpec> All { get; } =
    [
        new(ImportContentKind.Mod,
            Noun: "Mod", PluralNoun: "Mods",
            FileFilter: "Mod jars (*.jar)|*.jar|All files|*",
            AllowsFolder: false,
            InstanceFolder: "mods",
            LibraryKindName: "Mod",
            LibraryPlacesIntoInstances: true,
            HostBundleKind: null,
            CanHost: true, WantsInstanceStep: true,
            WantsLoaders: true, WantsMcVersions: true, SingleMcVersion: false, WantsChannel: true,
            MaxHostBytes: MaxFileBytes,
            Scope: ScanScope.Mods),

        new(ImportContentKind.ResourcePack,
            Noun: "Resource pack", PluralNoun: "Resource packs",
            FileFilter: "Resource packs (*.zip)|*.zip|All files (*.*)|*.*",
            AllowsFolder: true,
            InstanceFolder: "resourcepacks",
            LibraryKindName: nameof(LibraryKind.ResourcePack),
            LibraryPlacesIntoInstances: true,
            HostBundleKind: null,
            CanHost: true, WantsInstanceStep: true,
            WantsLoaders: false, WantsMcVersions: true, SingleMcVersion: false, WantsChannel: true,
            MaxHostBytes: MaxFileBytes,
            Scope: ScanScope.ResourcePacks),

        new(ImportContentKind.ShaderPack,
            Noun: ContentBundleService.KindLabel(BundleKind.ShaderPack),
            PluralNoun: ContentBundleService.KindLabel(BundleKind.ShaderPack) + "s",
            FileFilter: "Shader packs (*.zip)|*.zip|All files (*.*)|*.*",
            AllowsFolder: true,
            InstanceFolder: BundleTargets.DefaultFor(BundleKind.ShaderPack),
            LibraryKindName: nameof(LibraryKind.ShaderPack),
            LibraryPlacesIntoInstances: true,
            HostBundleKind: BundleKind.ShaderPack,
            CanHost: true, WantsInstanceStep: true,
            WantsLoaders: false, WantsMcVersions: true, SingleMcVersion: false, WantsChannel: true,
            MaxHostBytes: MaxFileBytes,
            Scope: ScanScope.Shaders),

        new(ImportContentKind.World,
            Noun: "World", PluralNoun: "Worlds",
            FileFilter: "Minecraft world (*.zip)|*.zip|All files (*.*)|*.*",
            AllowsFolder: true,
            InstanceFolder: "saves",
            // Kept in the library as a template, never handed out: two instances sharing one save
            // would mean two game processes writing the same region files. Each instance gets its
            // own copy through WorldService.
            LibraryKindName: nameof(LibraryKind.World),
            LibraryPlacesIntoInstances: false,
            HostBundleKind: null,
            CanHost: true, WantsInstanceStep: true,
            WantsLoaders: false, WantsMcVersions: true, SingleMcVersion: true, WantsChannel: false,
            MaxHostBytes: MaxWorldBytes,
            Scope: ScanScope.Worlds),

        new(ImportContentKind.ConfigBundle,
            Noun: ContentBundleService.KindLabel(BundleKind.ConfigBundle),
            PluralNoun: ContentBundleService.KindLabel(BundleKind.ConfigBundle) + "s",
            FileFilter: "Zip archive (*.zip)|*.zip|Every file (*.*)|*.*",
            AllowsFolder: true,
            InstanceFolder: BundleTargets.DefaultFor(BundleKind.ConfigBundle),
            // Stored, but never handed out by the library: a bundle is a zip here and a tree of
            // files in the instance, so the unpack stays with the bundle installer.
            LibraryKindName: "ConfigBundle",
            LibraryPlacesIntoInstances: false,
            HostBundleKind: BundleKind.ConfigBundle,
            CanHost: true, WantsInstanceStep: true,
            WantsLoaders: true, WantsMcVersions: true, SingleMcVersion: false, WantsChannel: true,
            MaxHostBytes: MaxFileBytes,
            Scope: ScanScope.Config),

        new(ImportContentKind.KubeJsBundle,
            Noun: ContentBundleService.KindLabel(BundleKind.KubeJsBundle),
            PluralNoun: ContentBundleService.KindLabel(BundleKind.KubeJsBundle) + "s",
            FileFilter: "Zip archive (*.zip)|*.zip|Every file (*.*)|*.*",
            AllowsFolder: true,
            InstanceFolder: BundleTargets.DefaultFor(BundleKind.KubeJsBundle),
            LibraryKindName: "KubeJsBundle",
            LibraryPlacesIntoInstances: false,
            HostBundleKind: BundleKind.KubeJsBundle,
            CanHost: true, WantsInstanceStep: true,
            WantsLoaders: true, WantsMcVersions: true, SingleMcVersion: false, WantsChannel: true,
            MaxHostBytes: MaxFileBytes,
            Scope: ScanScope.Config),

        new(ImportContentKind.DataPack,
            Noun: ContentBundleService.KindLabel(BundleKind.DataPack),
            PluralNoun: ContentBundleService.KindLabel(BundleKind.DataPack) + "s",
            FileFilter: "Data packs (*.zip)|*.zip|All files (*.*)|*.*",
            AllowsFolder: true,
            InstanceFolder: BundleTargets.DefaultFor(BundleKind.DataPack),
            LibraryKindName: null,
            LibraryPlacesIntoInstances: false,
            HostBundleKind: BundleKind.DataPack,
            CanHost: true, WantsInstanceStep: true,
            WantsLoaders: false, WantsMcVersions: true, SingleMcVersion: false, WantsChannel: true,
            MaxHostBytes: MaxFileBytes,
            Scope: ScanScope.Files),

        new(ImportContentKind.Modpack,
            Noun: "Modpack", PluralNoun: "Modpacks",
            FileFilter: "Modpack files|*.mrpack;*.zip|Modrinth pack (*.mrpack)|*.mrpack|CurseForge pack (*.zip)|*.zip",
            AllowsFolder: false,
            InstanceFolder: "",
            LibraryKindName: null,
            LibraryPlacesIntoInstances: false,
            HostBundleKind: null,
            // A modpack becomes an instance; instances are shared by invitation, not by hosting a zip.
            CanHost: false, WantsInstanceStep: false,
            WantsLoaders: false, WantsMcVersions: false, SingleMcVersion: false, WantsChannel: false,
            MaxHostBytes: MaxFileBytes,
            Scope: ScanScope.All),
    ];

    /// <summary>The row for one kind.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A new enum member with no row. Thrown rather than
    /// defaulted, since a wrong row would put files in the wrong folder.</exception>
    public static ImportContentSpec For(ImportContentKind kind) =>
        All.FirstOrDefault(s => s.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind,
               "No import descriptor for that content kind - add a row to ImportContentSpec.All.");
}

/// <summary>What a page asks the import flow for.</summary>
/// <param name="Kind">Which content page is importing.</param>
/// <param name="Packs">The instances to offer. Passed in so the card shows the same list as the page
/// behind it, without waiting on a second fetch.</param>
/// <param name="PreferredPackId">The instance to pre-tick: the page's scope selection, or the row the
/// user right-clicked.</param>
/// <param name="PrePickedPath">A file or folder already chosen (drag and drop, downloads), which
/// skips the first step.</param>
/// <param name="SourceIsFixed">The pre-picked path can't be swapped, so the card doesn't offer Back
/// to step 1. Used for downloads, where the provenance below describes the fetched bytes; a dropped
/// file can still be swapped.</param>
/// <param name="DisplayName">Name to use in the library and in every instance, e.g. a store
/// listing's title. Null keeps the name derived from the file.</param>
/// <param name="OriginSource">Which store the bytes came from.</param>
/// <param name="OriginProjectId">The listing's id within that store.</param>
/// <param name="OriginVersionId">The exact version that was downloaded.</param>
/// <param name="OriginVersionNumber">That version's human-readable number.</param>
/// <remarks>The <c>Origin</c> fields are stored on the library entry, and
/// <see cref="ContentLibraryService.ApplyAsync"/> copies them to every instance it later applies the
/// item to, so update checks work there too. They are null for a file picked off disk, which just
/// means no update check.</remarks>
public sealed record ImportRequest(
    ImportContentKind Kind,
    IReadOnlyList<PackSummary> Packs,
    Guid? PreferredPackId = null,
    string? PrePickedPath = null,
    bool SourceIsFixed = false,
    string? DisplayName = null,
    ModSource? OriginSource = null,
    string? OriginProjectId = null,
    string? OriginVersionId = null,
    string? OriginVersionNumber = null);

/// <summary>What the import flow did, for the page's status line and its reload decision.</summary>
/// <param name="FilesPlaced">How many files were written into instances. Zero is valid:
/// library-only, or every instance already had it.</param>
/// <param name="Targets">The instances that were written to, i.e. what to invalidate and re-read. For
/// a modpack, the instance that was created.</param>
/// <param name="AddedToLibrary">The launcher now keeps its own copy outside every instance.</param>
/// <param name="HostedId">The mod, resource pack, world or bundle created on the server, or null
/// when nothing was published.</param>
/// <param name="Summary">One status-bar line, built from <see cref="LibraryApplyResult.Summary"/> or
/// the bundle install summary.</param>
public sealed record ImportOutcome(
    int FilesPlaced,
    IReadOnlyList<Guid> Targets,
    bool AddedToLibrary,
    Guid? HostedId,
    string Summary);

/// <summary>Bytes a browser page has just downloaded, staged so <see cref="ImportContentCard"/> can
/// take them as the pre-picked file.</summary>
/// <remarks>
/// <para>Each download gets a private folder, so the file keeps the caller's name (the library, the
/// instances and a save's folder are all named after it) and two downloads of the same pack can't
/// collide.</para>
/// <para>The name is reserved against the library with <see cref="ShaderPackService.NextFreePath"/>,
/// the same helper <see cref="ContentLibraryService.Add"/> uses, so the library won't rename it to
/// <c>name-2.zip</c> and the provenance record matches the file. Two racing downloads can still
/// collide; the loser only loses its update check.</para>
/// <para>The card only copies out of here, so disposing (deleting the folder) is the cleanup for
/// every exit, including cancel.</para>
/// </remarks>
public sealed class StagedDownload : IDisposable
{
    private readonly string _folder;
    private bool _gone;

    private StagedDownload(string folder, string filePath)
    {
        _folder = folder;
        FilePath = filePath;
    }

    /// <summary>Where to write the bytes, and what to hand the card as its pre-picked path.</summary>
    public string FilePath { get; }

    /// <summary>The name the content keeps in the library and in every instance; per-instance settings
    /// keys are built from it.</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Stages a download of <paramref name="fileName"/> for a library kind.</summary>
    /// <param name="kind">Picks the library folder the name is reserved against.</param>
    /// <param name="fileName">The name the content should keep. Sanitised here, since it may be a
    /// store listing's title.</param>
    public static StagedDownload For(LibraryKind kind, string fileName)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"cloudlauncher-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var clean = Sanitise(fileName);
        var reserved = Path.GetFileName(ShaderPackService.NextFreePath(App.State.Library.FolderFor(kind), clean));
        return new StagedDownload(folder, PathSafety.ResolveFileName(folder, reserved)
            ?? throw new IOException($"'{reserved}' cannot be used as a file name."));
    }

    /// <summary>Deletes the staged bytes. Safe to call twice and never throws; a leftover temp folder
    /// isn't worth an error.</summary>
    public void Dispose()
    {
        if (_gone) return;
        _gone = true;
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (Exception ex) { AppLog.LogError(nameof(StagedDownload), ex); }
    }

    private static string Sanitise(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        // Windows drops trailing dots and spaces, which would leave "." and ".." naming a folder.
        var cleaned = new string(fileName.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0) cleaned = "download.zip";
        // Cap the stem so the full path stays under 260 characters inside an instance folder. The
        // extension is kept.
        var ext = Path.GetExtension(cleaned);
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (stem.Length > 96) stem = stem[..96].Trim();
        var name = (stem.Length == 0 ? "download" + ext : stem + ext).TrimEnd('.', ' ');
        if (PathSafety.IsSafeFileName(name)) return name;
        // A device name such as CON or NUL cannot be a file name at all; the prefix makes it a plain
        // one. Anything still refused, such as an absurdly long extension, gets the default name.
        var prefixed = "download-" + name;
        return PathSafety.IsSafeFileName(prefixed) ? prefixed : "download.zip";
    }
}
