namespace CloudLauncher.Shared;

/// <summary>How one path differs between the server's manifest and the one a client tried to push.</summary>
public enum ManifestConflictChange
{
    /// <summary>The server has this file and the upload does not.</summary>
    OnlyOnServer = 0,
    /// <summary>The upload has this file and the server does not.</summary>
    OnlyInUpload = 1,
    /// <summary>Both have it, with different contents.</summary>
    HashDiffers = 2
}

public sealed record ManifestConflictPath(string RelativePath, ManifestConflictChange Change);

/// <summary>
/// The body of a 409 from the sync endpoints: somebody else committed to this pack since the
/// client last pulled.
/// </summary>
/// <remarks>
/// <para><c>Error</c> and <c>CurrentVersion</c> keep the names and meanings older clients read.</para>
/// <para><see cref="Paths"/> is a best-effort diff of the server's current manifest against the
/// refused one, capped so a conflict on a huge pack is still a small response.
/// <see cref="TotalDifferences"/> is the real count, and <see cref="PathsTruncated"/> says whether
/// the list is complete.</para>
/// </remarks>
public sealed record ManifestConflict(
    string Error,
    Guid PackId,
    long CurrentVersion,
    long BaseVersion,
    DateTimeOffset ServerUpdatedAt,
    Guid? LastUploadedById,
    string? LastUploadedByUsername,
    DateTimeOffset? LastUploadedAt,
    IReadOnlyList<ManifestConflictPath> Paths,
    int TotalDifferences,
    bool PathsTruncated);
