using System.Text.Json;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Net;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>Launcher-wide settings stored in the database so every client sees the
/// same values. Secret keys are never returned; admins only see "is set" flags.</summary>
[ApiController]
public class SettingsController(
    AppDbContext db, UpstreamGuard upstreamGuard, ILogger<SettingsController> log) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Authorize(Roles = "admin")]
    [HttpGet("admin/global-settings")]
    public async Task<ActionResult<GlobalSettingsView>> Get(CancellationToken ct)
    {
        var s = await LoadOrCreateAsync(ct);
        return Ok(new GlobalSettingsView(
            HasCurseForgeApiKey: !string.IsNullOrEmpty(s.CurseForgeApiKey),
            HasModrinthToken:    !string.IsNullOrEmpty(s.ModrinthToken),
            DefaultRules:        DeserializeRules(s.DefaultRulesJson)));
    }

    [Authorize(Roles = "admin")]
    [HttpPut("admin/global-settings")]
    [HttpPost("admin/global-settings")]
    [HttpPatch("admin/global-settings")]
    public async Task<ActionResult<GlobalSettingsView>> Update(
        [FromBody] UpdateGlobalSettingsRequest req, CancellationToken ct)
    {
        var s = await LoadOrCreateAsync(ct);

        if (req.ClearCurseForgeApiKey)               s.CurseForgeApiKey = null;
        else if (req.CurseForgeApiKey is { Length: > 0 } k) s.CurseForgeApiKey = k.Trim();

        if (req.ClearModrinthToken)                  s.ModrinthToken = null;
        else if (req.ModrinthToken is { Length: > 0 } t) s.ModrinthToken = t.Trim();

        if (req.DefaultRules is not null)
            s.DefaultRulesJson = JsonSerializer.Serialize(req.DefaultRules, Json);

        s.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        upstreamGuard.InvalidateKeys(); // the proxy caches the keys for a minute

        return Ok(new GlobalSettingsView(
            !string.IsNullOrEmpty(s.CurseForgeApiKey),
            !string.IsNullOrEmpty(s.ModrinthToken),
            DeserializeRules(s.DefaultRulesJson)));
    }

    /// <summary>Any logged-in launcher can pull the admin's default rules so newly
    /// created packs use the same routing across all clients.</summary>
    [Authorize]
    [HttpGet("settings/default-rules")]
    public async Task<ActionResult<DefaultRulesResponse>> GetDefaultRules(CancellationToken ct)
    {
        var s = await db.GlobalSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return Ok(new DefaultRulesResponse(DeserializeRules(s?.DefaultRulesJson)));
    }

    /// <summary>Whether new accounts can be created, by registering or by a first Google sign-in.</summary>
    [Authorize(Roles = "admin")]
    [HttpGet("admin/registration")]
    public async Task<ActionResult<RegistrationSettings>> GetRegistration(CancellationToken ct) =>
        Ok(new RegistrationSettings(Open: !await AccountSignups.AreClosedAsync(db, ct)));

    /// <summary>Opens or closes sign-ups. Existing accounts are not affected either way.</summary>
    [Authorize(Roles = "admin")]
    [HttpPut("admin/registration")]
    public async Task<ActionResult<RegistrationSettings>> SetRegistration(
        [FromBody] RegistrationSettings req, CancellationToken ct)
    {
        var s = await LoadOrCreateAsync(ct);
        s.RegistrationClosed = !req.Open;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Admin {AdminId} turned sign-ups {State}.", this.UserId(), req.Open ? "on" : "off");
        return Ok(new RegistrationSettings(Open: !s.RegistrationClosed));
    }

    private async Task<GlobalSetting> LoadOrCreateAsync(CancellationToken ct)
    {
        var existing = await db.GlobalSettings.FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;
        var fresh = new GlobalSetting { Id = 1 };
        db.GlobalSettings.Add(fresh);
        await db.SaveChangesAsync(ct);
        return fresh;
    }

    private static IReadOnlyList<PackFileRule> DeserializeRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PackFileRule>();
        try { return JsonSerializer.Deserialize<List<PackFileRule>>(json, Json) ?? new(); }
        catch { return Array.Empty<PackFileRule>(); }
    }
}

/// <summary>The sign-up switch as the admin routes read and write it.</summary>
public sealed record RegistrationSettings(bool Open);
