namespace CloudLauncher.Shared;

public sealed record UserQuotaInfo(
    Guid UserId,
    string Username,
    long UsedBytes,
    long? QuotaBytes);

/// <param name="QuotaBytes">null = unlimited.</param>
public sealed record SetQuotaRequest(long? QuotaBytes);

/// <summary>What the signed-in user is storing on the server, and what they are allowed to store.</summary>
/// <param name="UsedBytes">
/// Bytes of blob storage their owned packs, mods, worlds and resource packs keep alive, counting each
/// distinct blob once.
/// </param>
/// <param name="QuotaBytes">Their quota, or null when they have none (unlimited).</param>
/// <remarks>
/// Deliberately not <see cref="UserQuotaInfo"/>, whose <c>UsedBytes</c> is the admin screen's logical
/// per-pack accounting: it charges a user for every manifest entry even when two of them are the same
/// bytes on disk. This figure answers a different question — how much storage would actually come
/// back if the user deleted everything — so the two must not be confused for one another.
/// </remarks>
public sealed record UserStorageUsage(long UsedBytes, long? QuotaBytes);

public sealed record SetAdminRequest(bool IsAdmin);
