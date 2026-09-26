using CloudLauncher.Services;

namespace CloudLauncher;

public sealed class AppState
{
    public AppSettings Settings { get; }
    public ApiClient Api { get; }
    public PackFolderService Packs { get; }
    public MinecraftAccountService MinecraftAccounts { get; }
    public VersionService Versions { get; }
    public MinecraftInstanceService Instances { get; }
    public LaunchService Launcher { get; }
    public PackRuleService Rules { get; }
    public ModrinthService Modrinth { get; }
    public CurseForgeService CurseForge { get; }
    public ModpackImportService ModpackImport { get; }
    public ModpackDownloadService ModpackDownload { get; }
    public PackAssetService PackAssets { get; }
    public WorldService Worlds { get; }
    public ResourcePackService ResourcePacks { get; }
    public ShaderPackService Shaders { get; }

    /// <summary>One copy of a resource or shader pack, hard-linked into as many instances as the user
    /// wants. Built after Packs and Shaders because it sits on top of both.</summary>
    public ContentLibraryService Library { get; }

    /// <summary>Writes options.txt's pack stack and the shader loader's config for the defaults engine.
    /// Kept separate because the launch path also runs it on its own, after the overlay.</summary>
    public ContentActivationService ContentActivation { get; }

    /// <summary>Applies the user's default worlds, shaders, resource packs, configs and scripts to every
    /// compatible instance. Built last because it drives Library, Worlds, ResourcePacks and Shaders.</summary>
    public ContentDefaultsService ContentDefaults { get; }

    /// <summary>Mod semantics on top of the content library: the user's default mod set, the rules
    /// that decide which instances get it, and the same-mod collision guard a jar needs.</summary>
    public ModLibraryService ModLibrary { get; }
    public ModFingerprintCache ModFingerprints { get; }
    public ModCounterpartCache ModCounterparts { get; }
    public ModAddedCache ModAdded { get; }
    public ModVersionCatalog ModVersions { get; }
    public ModMetadataService ModMetadata { get; }
    public PackModInventory ModInventory { get; }
    public ModPlanService ModPlans { get; }
    public TestLaunchScope TestScope { get; }
    public UpdateService Update { get; }

    /// <summary>
    /// Raised when the refresh token has expired. Subscribe from MainWindow to redirect to login.
    /// </summary>
    public event Action? SessionExpired;

    /// <summary>True when the launcher's own traffic is not reaching the server.</summary>
    public bool IsOffline => Api.IsOffline;

    /// <summary>Short phrase naming why, e.g. "the connection was refused". Null while online.</summary>
    public string? OfflineReason => Api.OfflineReason;

    /// <summary>When the launcher first noticed it was out. Null while online.</summary>
    public DateTimeOffset? OfflineSince => Api.OfflineSince;

    /// <summary>
    /// Raised on the UI thread whenever <see cref="IsOffline"/> flips, in either direction.
    /// </summary>
    /// <remarks>
    /// Safe to subscribe from any thread. Handlers should re-read <see cref="IsOffline"/>, since raises
    /// can arrive out of order.
    /// </remarks>
    public event Action? ConnectivityChanged;

    /// <summary>
    /// Raised on the UI thread whenever a store call is waiting to be retried because CurseForge,
    /// Modrinth or the launcher server asked for a pause. Pages show it as "CurseForge is busy, trying
    /// again..." while they load (see <see cref="Views.PageState"/>).
    /// </summary>
    public event Action<StoreWait>? StoreWaiting;

    public AppState()
    {
        Settings = AppSettings.Load();
        Api = new ApiClient(Settings);
        Api.SessionExpired += () => SessionExpired?.Invoke();
        Api.ConnectivityChanged += _ => OnUi(() => ConnectivityChanged?.Invoke());
        Api.StoreWaiting += wait => OnUi(() => StoreWaiting?.Invoke(wait));
        Packs = new PackFolderService(Settings, Api);
        MinecraftAccounts = new MinecraftAccountService(Settings);
        Versions = new VersionService();
        Instances = new MinecraftInstanceService();
        Launcher = new LaunchService(Settings, MinecraftAccounts, Packs, Instances);
        Rules = new PackRuleService(Api);
        Modrinth = new ModrinthService(Api);
        CurseForge = new CurseForgeService(Api);
        ModFingerprints = new ModFingerprintCache();
        ModCounterparts = new ModCounterpartCache();
        ModAdded = new ModAddedCache();
        ModVersions = new ModVersionCatalog(Modrinth, CurseForge);
        ModMetadata = new ModMetadataService(Packs, Settings);
        Packs.SetModMetadata(ModMetadata);
        ModInventory = new PackModInventory(Packs, ModFingerprints, Modrinth, CurseForge, ModMetadata, ModAdded);
        ModPlans = new ModPlanService(Packs);
        Packs.SetModPlans(ModPlans);
        TestScope = new TestLaunchScope(Packs);
        ModpackImport = new ModpackImportService(Modrinth, CurseForge, Api, Packs, ModFingerprints);
        ModpackImport.SetModMetadata(ModMetadata);
        ModpackImport.SetSettings(Settings);
        PackAssets = new PackAssetService(Packs, Modrinth);
        Packs.SetPackAssets(PackAssets);
        ModpackDownload = new ModpackDownloadService(Modrinth, CurseForge, ModpackImport, Api, Packs, PackAssets, Settings);
        ModpackImport.SetPackAssets(PackAssets);
        Worlds = new WorldService(Settings, Packs);
        ResourcePacks = new ResourcePackService(Settings, Packs);
        Shaders = new ShaderPackService(Settings, Packs);
        Library = new ContentLibraryService(Settings, Packs, Shaders);
        Packs.SetLibrary(Library);
        ContentActivation = new ContentActivationService(ResourcePacks, Shaders, Instances);
        ContentDefaults = new ContentDefaultsService(
            Settings, Packs, Library, Worlds, ResourcePacks, Shaders, ContentActivation, Instances);
        // Launcher is created before the content services, so it gets the defaults engine by setter below.
        ModLibrary = new ModLibraryService(Library, ContentDefaults, Packs);

        Launcher.SetContentDefaults(ContentDefaults);
        Update = new UpdateService(Api);
    }

    /// <summary>
    /// Runs <paramref name="raise"/> on the UI thread, the way <see cref="ProgressHub"/> does.
    /// </summary>
    /// <remarks>
    /// Connectivity changes are noticed on socket threads. Unlike ProgressHub, this still runs the action
    /// when there is no <c>Application</c> (a console harness has no dispatcher).
    /// </remarks>
    private static void OnUi(Action raise)
    {
        var app = System.Windows.Application.Current;
        if (app is null) { raise(); return; }
        if (app.Dispatcher.CheckAccess()) raise();
        else app.Dispatcher.BeginInvoke(raise);
    }
}
