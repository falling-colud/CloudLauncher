namespace CloudLauncher.Shared;

/// <summary>Admin view of the global settings. Secret values are never returned — only
/// boolean flags telling whether they're set, plus the non-secret default rules.</summary>
public sealed record GlobalSettingsView(
    bool HasCurseForgeApiKey,
    bool HasModrinthToken,
    IReadOnlyList<PackFileRule> DefaultRules);

/// <summary>Admin update payload. Each field is independently optional:
/// pass null to leave unchanged, pass a value to set, or set Clear* = true to wipe.</summary>
public sealed record UpdateGlobalSettingsRequest(
    string? CurseForgeApiKey = null,
    bool ClearCurseForgeApiKey = false,
    string? ModrinthToken = null,
    bool ClearModrinthToken = false,
    List<PackFileRule>? DefaultRules = null);

/// <summary>Rules-only view available to any authenticated launcher so it can seed
/// new packs with the admin-defined defaults.</summary>
public sealed record DefaultRulesResponse(IReadOnlyList<PackFileRule> Rules);
