namespace CloudLauncher.Shared;

public sealed record UserQuotaInfo(
    Guid UserId,
    string Username,
    long UsedBytes,
    long? QuotaBytes);

/// <param name="QuotaBytes">null = unlimited.</param>
public sealed record SetQuotaRequest(long? QuotaBytes);

public sealed record SetAdminRequest(bool IsAdmin);
