using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CloudLauncher.Services;

/// <summary>
/// Small per-profile secrets (the account session, the user's own CurseForge key and RCON passwords),
/// each in its own file beside settings.json, encrypted for the signed-in Windows user.
/// </summary>
/// <remarks>
/// Not in settings.json because <see cref="AppSettings.Save"/> rewrites the whole file from memory,
/// and another launcher process that doesn't know the key would drop it. DPAPI ties each file to
/// this Windows account. IO and crypto errors are swallowed: an unreadable secret counts as absent,
/// and a failed write must never make a settings change throw.
/// </remarks>
public static class SecretStore
{
    private static readonly byte[] Entropy = "CloudLauncher.SecretStore.v1"u8.ToArray();

    /// <summary>Error message from the last failed read or write, for the settings page; null after
    /// a call that worked.</summary>
    public static string? LastError { get; private set; }

    private static string PathFor(string name) =>
        Path.Combine(AppSettings.DataRootPath, "secrets", name + ".bin");

    /// <summary>The stored value, or null when there is none or it cannot be decrypted.</summary>
    public static string? Read(string name)
    {
        try
        {
            var path = PathFor(name);
            if (!File.Exists(path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var text = Encoding.UTF8.GetString(plain);
            LastError = null;
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.LogError("secrets", ex);
            return null;
        }
    }

    /// <summary>Stores <paramref name="value"/>, or deletes the secret when it is null or empty.
    /// Returns false when the write failed.</summary>
    public static bool Write(string name, string? value)
    {
        try
        {
            var path = PathFor(name);
            if (string.IsNullOrEmpty(value))
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            // Write-then-rename like settings.json, so a crash mid-write can't leave a truncated file that
            // fails to decrypt and loses the key.
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, path, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.LogError("secrets", ex);
            return false;
        }
    }
}
