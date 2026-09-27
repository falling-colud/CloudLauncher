using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

// Instances on this PC only (see LocalPackStore): the instance calls answer them without the server,
// so a launcher that is not signed in can do everything that needs no account.
public sealed partial class ApiClient
{
    private LocalPackStore? _local;

    /// <summary>Hands the client the store of instances on this PC. Set once by <see cref="AppState"/>
    /// after the folder service exists, which itself needs this client.</summary>
    public void AttachLocalPacks(LocalPackStore store) => _local = store;

    /// <summary>What a call that needs a CloudLauncher account says while nobody is signed in.</summary>
    public const string SignInRequiredMessage = "Sign in to CloudLauncher to use this.";

    /// <summary>True while this PC holds a CloudLauncher session. Everything that needs no account
    /// works without one.</summary>
    public bool IsSignedIn => _settings.HasStoredSession;

    /// <summary>True when <paramref name="packId"/> is an instance on this PC, not on an account.</summary>
    public bool IsLocalPack(Guid packId) => _local?.Contains(packId) == true;

    /// <summary>A 401 that never went to the server: the call needs an account and there is none.</summary>
    private static ApiException SignInRequired() => new(SignInRequiredMessage, HttpStatusCode.Unauthorized);

    /// <summary>The instances on this PC, or none before the store is attached.</summary>
    private List<PackSummary> LocalPackList()
    {
        if (_local is null) return [];
        try { return _local.List(); }
        catch (Exception ex)
        {
            AppLog.LogError("local-packs", ex);
            return [];
        }
    }

    /// <summary>The account's instances followed by this PC's, each id once.</summary>
    private static List<PackSummary> WithLocal(List<PackSummary> account, List<PackSummary> local)
    {
        if (local.Count == 0) return account;
        var localIds = local.Select(p => p.Id).ToHashSet();
        return account.Where(p => !localIds.Contains(p.Id)).Concat(local).ToList();
    }

    /// <summary>The detail of a local instance, or null when the id is not one.</summary>
    private PackDetail? LocalPackDetail(Guid id) =>
        _local is { } store && store.Contains(id) ? store.Get(id, LocalRulesFor(id)) : null;

    /// <summary>Refuses a server-only call about an instance that is on this PC only.</summary>
    private void RefuseIfLocal(Guid packId)
    {
        if (IsLocalPack(packId)) throw new ApiException(LocalPackStore.LocalOnlyMessage, HttpStatusCode.Conflict);
    }

    /// <summary>The instance list to paint before any answer: the account's last list (while signed
    /// in) plus this PC's instances. Null when there is nothing to show.</summary>
    public List<PackSummary>? PeekPacks()
    {
        var local = LocalPackList();
        var remembered = IsSignedIn ? PackListCache.Load() : null;
        if (remembered is null) return local.Count > 0 ? local : null;
        return WithLocal(remembered, local);
    }

    /// <summary>
    /// Moves an instance from this PC onto the signed-in account, keeping its id, so everything the
    /// launcher keeps per instance (settings, mod notes, play time) stays attached to it.
    /// </summary>
    /// <remarks>
    /// <para>The files do not move: the folder is the same, and hosting it for others is the
    /// separate step the Share tab offers once the instance is on the account.</para>
    /// <para>The file rules go up in the same step, before the instance stops being local: an account
    /// instance takes its rules from the server when it is opened, so an instance that arrived without
    /// them would have its own <c>.rules.json</c> replaced by nothing.</para>
    /// <para>A server from before this existed ignores the id and makes an instance of its own; that
    /// copy is deleted again and the call fails, leaving the instance local.</para>
    /// </remarks>
    public async Task<PackSummary> AddLocalPackToAccountAsync(Guid packId, CancellationToken ct = default)
    {
        if (!IsSignedIn) throw SignInRequired();
        var local = LocalPackDetail(packId)
                    ?? throw new ApiException("That instance is no longer on this PC.", HttpStatusCode.NotFound);

        await EnsureTokenAsync(ct);
        var created = await ReadAsync<PackSummary>(await _http.PostAsJsonAsync("packs",
            new CreatePackRequest(local.Name, local.Description, local.IsEmpty, local.MinecraftVersion,
                local.Loader, local.LoaderVersion, local.Summary, Id: packId), JsonOpts, ct), ct);

        try
        {
            if (created.Id != packId)
                throw new ApiException(
                    "The CloudLauncher server is too old to take an instance from this PC. Try again once it has been updated.",
                    HttpStatusCode.NotImplemented);

            if (local.Rules.Count > 0)
            {
                var rules = new UpdatePackRequest(Name: null, Description: null, Visibility: null, IsShared: null,
                    IsEmpty: null, MinecraftVersion: null, Loader: null, LoaderVersion: null, Rules: local.Rules.ToList());
                await EnsureSuccess(await _http.PatchAsJsonAsync($"packs/{packId}", rules, JsonOpts, ct));
            }
        }
        catch
        {
            // Nothing half-done is left on the account: the instance is either there with its rules
            // or not there at all.
            try { await EnsureSuccess(await _http.DeleteAsync($"packs/{created.Id}", CancellationToken.None)); }
            catch (Exception undo) { AppLog.Log("local-packs", $"Could not take back the server copy {created.Id}: {undo.Message}"); }
            throw;
        }

        _local!.Release(packId);
        ForgetRecentPacks();
        AppLog.Log("local-packs", $"Added local instance '{local.Name}' ({packId}) to the account.");
        return created;
    }
}
