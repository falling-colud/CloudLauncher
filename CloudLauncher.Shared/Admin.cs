namespace CloudLauncher.Shared;

public sealed record UserQuotaInfo(
    Guid UserId,
    string Username,
    long UsedBytes,
    long? QuotaBytes);

/// <param name="QuotaBytes">null = unlimited.</param>
public sealed record SetQuotaRequest(long? QuotaBytes);

/// <summary>What the signed-in user is storing on the server, and what they are allowed to store.</summary>
/// <param name="UsedBytes">Bytes of blob storage kept alive by their owned packs, mods, worlds and
/// resource packs, counting each distinct blob once.</param>
/// <param name="QuotaBytes">Their quota, or null when unlimited.</param>
/// <remarks>Not the same as <see cref="UserQuotaInfo"/>, whose UsedBytes charges for every manifest
/// entry even when two entries share the same bytes. This is what deleting everything would free.</remarks>
public sealed record UserStorageUsage(long UsedBytes, long? QuotaBytes);

public sealed record SetAdminRequest(bool IsAdmin);
