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
    public ModFingerprintCache ModFingerprints { get; }
    public ModMetadataService ModMetadata { get; }
    public PackModInventory ModInventory { get; }
    public TestLaunchScope TestScope { get; }
    public UpdateService Update { get; }

    /// <summary>
    /// Raised when the refresh token has expired. Subscribe from MainWindow to redirect to login.
    /// </summary>
    public event Action? SessionExpired;

    public AppState()
    {
        Settings = AppSettings.Load();
        Api = new ApiClient(Settings);
        Api.SessionExpired += () => SessionExpired?.Invoke();
        Packs = new PackFolderService(Settings, Api);
        MinecraftAccounts = new MinecraftAccountService(Settings);
        Versions = new VersionService();
        Instances = new MinecraftInstanceService();
        Launcher = new LaunchService(Settings, MinecraftAccounts, Packs, Instances);
        Rules = new PackRuleService(Api);
        Modrinth = new ModrinthService(Api);
        CurseForge = new CurseForgeService(Api);
        ModFingerprints = new ModFingerprintCache();
        ModMetadata = new ModMetadataService(Packs);
        ModInventory = new PackModInventory(Packs, ModFingerprints, Modrinth, CurseForge, ModMetadata);
        TestScope = new TestLaunchScope(Packs);
        ModpackImport = new ModpackImportService(Modrinth, CurseForge, Api, Packs, ModFingerprints);
        PackAssets = new PackAssetService(Packs, Modrinth);
        Packs.SetPackAssets(PackAssets);
        ModpackDownload = new ModpackDownloadService(Modrinth, CurseForge, ModpackImport, Api, Packs, PackAssets, Settings);
        ModpackImport.SetPackAssets(PackAssets);
        Worlds = new WorldService(Settings, Packs);
        ResourcePacks = new ResourcePackService(Settings, Packs);
        Update = new UpdateService(Api);
    }
}
