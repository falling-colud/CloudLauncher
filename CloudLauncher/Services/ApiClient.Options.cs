using CloudLauncher.Shared;

namespace CloudLauncher.Services;

public sealed partial class ApiClient
{
    /// <summary>Which sign-in methods the server offers. Null when the server could not be reached or
    /// is too old to say, in which case the sign-in screen offers only a username and password.</summary>
    public async Task<AuthOptionsResponse?> GetAuthOptionsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("auth/options", ct);
            if (!response.IsSuccessStatusCode) return null;
            return await ReadAsync<AuthOptionsResponse>(response, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A probe made before sign-in: whatever went wrong, the page still opens.
            return null;
        }
    }
}
