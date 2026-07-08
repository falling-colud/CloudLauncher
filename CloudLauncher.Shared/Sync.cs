namespace CloudLauncher.Shared;

public sealed record ManifestEntry(string RelativePath, string Hash, long Size);

public sealed record PackManifest(
    Guid PackId,
    long Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ManifestEntry> Entries);

public sealed record BeginUploadRequest(
    long BaseVersion,
    IReadOnlyList<ManifestEntry> Entries);

public sealed record BeginUploadResponse(
    Guid UploadId,
    IReadOnlyList<string> MissingHashes);

public sealed record CommitUploadResponse(long NewVersion);
