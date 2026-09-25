using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>
/// The signed-in user's own public packs, for a "public" list the server may leave them out of.
/// </summary>
/// <remarks>
/// Older servers leave the caller's own packs out of <c>packs/browse?source=Public</c>, so Public lists
/// also fetch the <c>Owned</c> source with the same search and add the public ones. Callers drop
/// duplicates by id. Never throws: errors are logged and return what was found so far, and a
/// cancellation returns an empty list.
/// </remarks>
public static class OwnPublicPacks
{
    /// <summary>The server's cap for one browse page.</summary>
    private const int PageSize = 100;

    /// <summary>Safety stop at a thousand owned instances.</summary>
    private const int MaxPages = 10;

    /// <summary>Your packs that are set to Public and match <paramref name="query"/>, newest first.</summary>
    public static async Task<List<PackSummary>> FetchAsync(ApiClient api, string? query, CancellationToken ct)
    {
        var found = new List<PackSummary>();
        try
        {
            var offset = 0;
            for (var page = 0; page < MaxPages; page++)
            {
                var result = await api.BrowsePacksAsync(PackBrowseSource.Owned, null, query, offset, PageSize, ct);
                found.AddRange(result.Items.Where(p => p.Visibility == PackVisibility.Public));
                offset += result.Items.Count;
                if (result.Items.Count == 0 || offset >= result.Total) break;
            }
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            AppLog.Log("packs", "Your own public packs could not be read, so a Public list may leave them out: "
                                + ex.Message);
        }
        return found.OrderByDescending(p => p.UpdatedAt).ToList();
    }
}
