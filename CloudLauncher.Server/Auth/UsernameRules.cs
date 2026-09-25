namespace CloudLauncher.Server.Auth;

/// <summary>What a username may look like. Registration and renaming both check against this.</summary>
public static class UsernameRules
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    /// <summary>Identity's default set, set explicitly so its own check and ours cannot drift.</summary>
    public const string AllowedCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";

    /// <summary>The sentence to show when <paramref name="name"/> is not a valid username, or null
    /// when it is. Pass the name already trimmed.</summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < MinLength || name.Length > MaxLength)
            return $"Username must be {MinLength}-{MaxLength} characters";
        foreach (var c in name)
            if (!AllowedCharacters.Contains(c))
                return "Usernames can only use letters, numbers and - . _ @ +";
        return null;
    }
}
