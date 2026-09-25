using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Signer;

/// <summary>Creates the release keys, signs update packages and checks a signed release. See
/// tools/release/README.md for when to use which.</summary>
/// <remarks>Signing and checking go through the launcher's own <see cref="UpdateVerifier"/> (compiled
/// in from the client), so this tool and the launcher accept the same packages. Only the JSON a
/// publish needs goes to stdout; everything else goes to stderr.</remarks>
internal static class Program
{
    private const string Usage = """
        usage:
          cl-signer keygen
              Creates k1 (release key, kept with DPAPI for this Windows account) and k2 (backup key,
              a PEM file to move to offline storage). Never overwrites existing keys.
          cl-signer keygen --backup <id>
              Creates one more backup key (PEM) with the given id, for replacing a lost key.
          cl-signer sign --zip <path> --version <x.y.z> [--key <id>] [--pem <file>]
              Signs a package with k1 by default. --pem signs with a key kept as a PEM file (k2).
          cl-signer verify --zip <path> --json <file> [--version <x.y.z>]
              Checks a signed release (the sign output or a latest.json) against the package.
        """;

    // Ties the release key's DPAPI blob to this tool, so no other DPAPI caller on the account can open it.
    private static readonly byte[] KeyEntropy = "CloudLauncher.ReleaseKey.v1"u8.ToArray();

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Keeps Base64's '+' unescaped (the default encoder writes \u002B), so the output can be pasted as is.
    private static readonly JsonSerializerOptions PlainJson =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string KeyDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudlauncher-release");

    private static string BackupDir => Path.Combine(KeyDir, "backup");

    private static string DpapiKeyPath(string id) => Path.Combine(KeyDir, $"update-key-{id}.dpapi");

    private static string BackupKeyPath(string id) => Path.Combine(BackupDir, $"update-key-{id}.pem");

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new UsageException("No command given.");
            var rest = args[1..];
            return args[0] switch
            {
                "keygen" => Keygen(ParseOptions(rest, "backup")),
                "sign" => Sign(ParseOptions(rest, "zip", "version", "key", "pem")),
                "verify" => Verify(ParseOptions(rest, "zip", "json", "version")),
                _ => throw new UsageException($"Unknown command '{args[0]}'.")
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    // ── keygen ──────────────────────────────────────────────────────────────

    private static int Keygen(Dictionary<string, string> options)
    {
        if (options.TryGetValue("backup", out var backupId)) return KeygenBackup(CheckId(backupId));

        var k1Path = DpapiKeyPath("k1");
        var k2Path = BackupKeyPath("k2");
        if (File.Exists(k1Path) || File.Exists(k2Path))
        {
            Console.Error.WriteLine($"Keys already exist in {KeyDir}. Nothing was changed.");
            return 1;
        }

        Directory.CreateDirectory(BackupDir);
        using var k1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var k2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var pkcs8 = k1.ExportPkcs8PrivateKey();
        try { WriteNew(k1Path, ProtectedData.Protect(pkcs8, KeyEntropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
        WriteNew(k2Path, Encoding.ASCII.GetBytes(k2.ExportPkcs8PrivateKeyPem()));
        WriteBackupReadme();

        Console.Error.WriteLine($"k1 (release key): {k1Path}");
        Console.Error.WriteLine($"k2 (backup key):  {k2Path}");
        Console.Error.WriteLine("Move the backup key to offline storage and delete it from this PC; see backup\\README.txt.");
        Console.Error.WriteLine("Public keys for UpdateVerifier.TrustedKeys:");
        PrintPublicKey("k1", k1);
        PrintPublicKey("k2", k2);
        return 0;
    }

    private static int KeygenBackup(string id)
    {
        var path = BackupKeyPath(id);
        if (File.Exists(path) || File.Exists(DpapiKeyPath(id)))
        {
            Console.Error.WriteLine($"A key with id {id} already exists in {KeyDir}. Nothing was changed.");
            return 1;
        }

        Directory.CreateDirectory(BackupDir);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteNew(path, Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()));
        WriteBackupReadme();

        Console.Error.WriteLine($"{id} (backup key): {path}");
        Console.Error.WriteLine("Move it to offline storage and delete it from this PC; see backup\\README.txt.");
        Console.Error.WriteLine("Public key for UpdateVerifier.TrustedKeys:");
        PrintPublicKey(id, key);
        return 0;
    }

    private static void PrintPublicKey(string id, ECDsa key)
    {
        var line = $"{id} {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}";
        Console.WriteLine(line);
        // Also saved next to the keys, so the public halves aren't lost with the console output.
        File.AppendAllText(Path.Combine(KeyDir, "public-keys.txt"), line + Environment.NewLine);
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
    }

    private static void WriteBackupReadme() => File.WriteAllText(Path.Combine(BackupDir, "README.txt"), """
        CloudLauncher update signing: backup keys
        =========================================

        The .pem files in this folder are backup signing keys. Every CloudLauncher install accepts an
        update signed with one of them, so treat each file like a master password.

        Do this now:
          1. Copy each .pem file to offline storage: a USB stick kept somewhere safe, a password
             manager entry, or both.
          2. Check the copy opens, then delete the .pem file from this PC and empty the Recycle Bin.

        Never put a backup key in the repository, a chat, an email, or a folder that syncs to the cloud.

        A backup key is only needed if the normal release key (k1, the .dpapi file one folder up) is
        lost, for example after Windows is reinstalled. tools/release/README.md in the CloudLauncher
        repository says what to do then.
        """, Utf8NoBom);

    // ── sign ────────────────────────────────────────────────────────────────

    private static int Sign(Dictionary<string, string> options)
    {
        var zip = Require(options, "zip");
        var version = Require(options, "version");
        var id = CheckId(options.GetValueOrDefault("key", "k1"));

        if (!UpdateVerifier.TrustedKeys.TryGetValue(id, out var publicKey) || publicKey.Length == 0)
        {
            Console.Error.WriteLine($"Launchers do not trust a key called {id}: add its public key to UpdateVerifier.TrustedKeys first.");
            return 1;
        }

        using var key = LoadPrivateKey(id, options.GetValueOrDefault("pem"));
        if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != publicKey)
        {
            Console.Error.WriteLine($"This private key is not the {id} that launchers trust. Nothing was signed.");
            return 1;
        }

        var (sha256, size) = HashFile(zip);
        var manifest = Utf8NoBom.GetBytes(UpdateVerifier.BuildManifest(version, sha256, size));
        if (UpdateVerifier.ParseManifest(manifest, out _) is null)
        {
            Console.Error.WriteLine($"'{version}' is not a version a launcher accepts (use numbers and dots, like 1.2.3).");
            return 1;
        }

        var signature = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (!UpdateVerifier.SignatureMatches(publicKey, manifest, signature))
        {
            Console.Error.WriteLine("The new signature does not verify against the trusted public key. Nothing was signed.");
            return 1;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            manifest = Convert.ToBase64String(manifest),
            signature = Convert.ToBase64String(signature),
            keyId = id,
            sha256,
            size
        }, PlainJson));
        return 0;
    }

    private static ECDsa LoadPrivateKey(string id, string? pemPath)
    {
        var key = ECDsa.Create();
        try
        {
            if (pemPath is not null)
            {
                key.ImportFromPem(File.ReadAllText(pemPath));
                return key;
            }

            var path = DpapiKeyPath(id);
            if (!File.Exists(path))
                throw new FileNotFoundException($"There is no {path}. A key kept as a PEM file is signed with --pem <file>.");
            var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(path), KeyEntropy, DataProtectionScope.CurrentUser);
            try { key.ImportPkcs8PrivateKey(pkcs8, out _); }
            finally { CryptographicOperations.ZeroMemory(pkcs8); }
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    // ── verify ──────────────────────────────────────────────────────────────

    private static int Verify(Dictionary<string, string> options)
    {
        var zip = Require(options, "zip");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Require(options, "json")));
        var root = doc.RootElement;

        var manifest = Text(root, "manifest");
        var signature = Text(root, "signature");
        var keyId = Text(root, "keyId");
        if (manifest is null || signature is null || keyId is null)
            return Refuse("the JSON has no manifest, signature or keyId");

        // The release's own version when the JSON has one (a latest.json does), else the signed one.
        string? version = options.GetValueOrDefault("version") ?? Text(root, "version");
        if (version is null)
        {
            try { version = UpdateVerifier.ParseManifest(Convert.FromBase64String(manifest), out _)?.Version; }
            catch (FormatException) { /* reported by Verify below */ }
        }

        var release = new LauncherReleaseInfo(version ?? "", "", 0, "", null, default,
            Manifest: manifest, Signature: signature, KeyId: keyId);
        // Any version counts as newer here; only a launcher knows what it is running.
        var result = UpdateVerifier.Verify(release, new Version(0, 0), UpdateVerifier.TrustedKeys);
        if (!result.IsValid || result.Manifest is not { } signed) return Refuse(result.Failure ?? "not valid");

        var (sha256, size) = HashFile(zip);
        if (!UpdateVerifier.MatchesPackage(signed, sha256, size))
            return Refuse($"{zip} (sha256 {sha256}, {size} bytes) is not the package the manifest was signed for");
        if (Text(root, "sha256") is { } listedSha && !listedSha.Equals(signed.Sha256, StringComparison.OrdinalIgnoreCase))
            return Refuse("the JSON's sha256 field does not match the signed manifest");
        if (Number(root, "size") is { } listedSize && listedSize != signed.Size)
            return Refuse("the JSON's size field does not match the signed manifest");

        Console.WriteLine($"OK: version {signed.Version}, signed with {keyId}, sha256 {signed.Sha256}, {signed.Size} bytes");
        return 0;
    }

    private static int Refuse(string reason)
    {
        Console.Error.WriteLine("NOT VALID: " + reason);
        return 2;
    }

    private static string? Text(JsonElement root, string name) =>
        Property(root, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static long? Number(JsonElement root, string name) =>
        Property(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var n) ? n : null;

    private static JsonElement? Property(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (string Sha256, long Size) HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return (Convert.ToHexStringLower(hash), stream.Length);
    }

    private static Dictionary<string, string> ParseOptions(string[] args, params string[] known)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
            if (name is null || !known.Contains(name)) throw new UsageException($"Unexpected argument '{args[i]}'.");
            if (i + 1 >= args.Length) throw new UsageException($"--{name} needs a value.");
            options[name] = args[++i];
        }
        return options;
    }

    private static string Require(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw new UsageException($"--{name} is required.");

    private static string CheckId(string id) =>
        id.Length is > 0 and <= 16 && id.All(char.IsAsciiLetterOrDigit)
            ? id
            : throw new UsageException($"'{id}' is not a key id (letters and digits, like k1).");

    private sealed class UsageException(string message) : Exception(message);
}
