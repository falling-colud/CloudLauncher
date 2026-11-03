using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>What a signed release manifest says about the package it covers.</summary>
public sealed record UpdateManifest(string Version, string Sha256, long Size);

/// <summary>The outcome of checking a release: its signed manifest, or why it was refused.</summary>
public sealed record UpdateVerification(UpdateManifest? Manifest, string? Failure)
{
    public bool IsValid => Manifest is not null && Failure is null;
}

/// <summary>Decides whether a launcher release was signed by the release key before any of it is
/// trusted.</summary>
/// <remarks>The feed (<c>launcher/latest</c>) is only a pointer. The signed manifest (version,
/// package SHA-256 and size; ECDSA P-256 with a compiled-in key) decides what may be installed, and
/// downloads are checked against it rather than the feed's own fields. tools/release/signer
/// compiles this same file, so keep it free of WPF.</remarks>
public static class UpdateVerifier
{
    /// <summary>First line of every manifest. A different layout gets a different number.</summary>
    public const string FormatLine = "cloudlauncher-update/1";

    /// <summary>Keys a release may be signed with, by key id: base64 SubjectPublicKeyInfo of an ECDSA
    /// P-256 key.</summary>
    /// <remarks>k1 signs every release. k2 is the backup and is kept offline: if k1 is ever lost, a
    /// release signed with k2 is still accepted, and that release ships a new key pair here.</remarks>
    public static IReadOnlyDictionary<string, string> TrustedKeys { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["k1"] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9FlCk4E4HBwHrl5uDjB1BopR8cIGqcacJMn68h69E/h2lu+/IzG/fv+hmq/1ZotheiW07QrWJ5GGgEiR9Ffeyw==",
            ["k2"] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEG2BwXfJSj0+b3y6OHrZjcZFsxhaS9YKQRQcKWCi/Ul6TE1QtxtuqR36MH110UqbXJNMPUKwVFX1HYitPLHaBKg==",
        };

    // Far above what the format produces; a longer value is not a manifest or signature from the signer.
    private const int MaxManifestBytes = 512;
    private const int MaxSignatureBytes = 128;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Checks <paramref name="release"/> against the compiled-in keys and this launcher's version.</summary>
    public static UpdateVerification Verify(LauncherReleaseInfo? release) =>
        Verify(release, AppVersion.Current, TrustedKeys);

    /// <summary>Checks <paramref name="release"/> like <see cref="Verify(LauncherReleaseInfo?)"/>, for
    /// putting this launcher's own files back rather than updating it: the release has to be signed for
    /// exactly the version that is running.</summary>
    public static UpdateVerification VerifyReinstall(LauncherReleaseInfo? release) =>
        Verify(release, AppVersion.Current, TrustedKeys, sameVersion: true);

    /// <summary>Checks that <paramref name="release"/> carries a manifest signed by one of
    /// <paramref name="trustedKeys"/>, in the expected format, for the version the release names, and
    /// that the version is newer than <paramref name="running"/> (with <paramref name="sameVersion"/>,
    /// that it is the same one).</summary>
    public static UpdateVerification Verify(
        LauncherReleaseInfo? release, Version running, IReadOnlyDictionary<string, string> trustedKeys,
        bool sameVersion = false)
    {
        if (release is null) return Fail("there is no release");
        if (string.IsNullOrWhiteSpace(release.Manifest) || string.IsNullOrWhiteSpace(release.Signature)
            || string.IsNullOrWhiteSpace(release.KeyId))
            return Fail("the release is not signed");
        if (!trustedKeys.TryGetValue(release.KeyId, out var publicKey) || string.IsNullOrEmpty(publicKey))
            return Fail($"the release is signed with a key this launcher does not trust ({release.KeyId})");

        byte[] manifestBytes, signature;
        try
        {
            manifestBytes = Convert.FromBase64String(release.Manifest);
            signature = Convert.FromBase64String(release.Signature);
        }
        catch (FormatException)
        {
            return Fail("the manifest or signature is not valid base64");
        }
        if (manifestBytes.Length > MaxManifestBytes || signature.Length > MaxSignatureBytes)
            return Fail("the manifest or signature is too long");

        if (!SignatureMatches(publicKey, manifestBytes, signature))
            return Fail("the signature does not match the manifest");

        var manifest = ParseManifest(manifestBytes, out var why);
        if (manifest is null) return Fail(why ?? "the manifest could not be read");
        if (!string.Equals(manifest.Version, release.Version?.Trim(), StringComparison.Ordinal))
            return Fail($"the manifest is for version {manifest.Version}, but the release says {release.Version}");
        if (!Version.TryParse(manifest.Version, out var signed)
            || (sameVersion ? AppVersion.Rank(signed) != AppVersion.Rank(running) : AppVersion.Rank(signed) <= AppVersion.Rank(running)))
            return Fail(sameVersion
                ? $"version {manifest.Version} is not the version of this launcher"
                : $"version {manifest.Version} is not newer than this launcher");

        return new UpdateVerification(manifest, null);
    }

    /// <summary>The exact text a release manifest consists of: LF line ends, UTF-8 without a BOM.</summary>
    public static string BuildManifest(string version, string sha256, long size) =>
        $"{FormatLine}\nversion={version}\nsha256={sha256}\nsize={size.ToString(CultureInfo.InvariantCulture)}\n";

    /// <summary>
    /// Reads a manifest, or returns null with <paramref name="failure"/> set when the bytes are anything
    /// but the exact text <see cref="BuildManifest"/> produces.
    /// </summary>
    public static UpdateManifest? ParseManifest(byte[] bytes, out string? failure)
    {
        failure = "the manifest is not in the expected format";
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }

        var lines = text.Split('\n');
        if (lines.Length != 5 || lines[0] != FormatLine || lines[4].Length != 0) return null;
        if (!TryField(lines[1], "version=", out var version) || !IsVersion(version)) return null;
        if (!TryField(lines[2], "sha256=", out var sha256) || !IsSha256(sha256)) return null;
        if (!TryField(lines[3], "size=", out var sizeText)
            || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0)
            return null;

        // Rebuilding it catches whatever the checks above let through that the signer never writes,
        // such as a size with a leading zero.
        if (!string.Equals(BuildManifest(version, sha256, size), text, StringComparison.Ordinal)) return null;

        failure = null;
        return new UpdateManifest(version, sha256, size);
    }

    /// <summary>True when a downloaded package is the one the manifest was signed for.</summary>
    public static bool MatchesPackage(UpdateManifest manifest, string sha256, long size) =>
        size == manifest.Size && string.Equals(sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="signature"/> (DER) is a valid ECDSA P-256 / SHA-256 signature
    /// of <paramref name="data"/> by <paramref name="publicKey"/> (base64 SubjectPublicKeyInfo).</summary>
    /// <remarks>The key and the signature are read by hand rather than with ImportSubjectPublicKeyInfo and
    /// the DER signature format, which need System.Formats.Asn1: a launcher folder that has lost that
    /// file must still be able to verify the release that repairs it. Both layouts are fixed for
    /// P-256, and only strict DER is accepted, as before.</remarks>
    public static bool SignatureMatches(string publicKey, byte[] data, byte[] signature)
    {
        try
        {
            if (!TryReadP256PublicKey(Convert.FromBase64String(publicKey), out var point)
                || !TryReadDerSignature(signature, out var rs))
                return false;
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = point });
            return key.VerifyData(data, rs, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    private const int P256FieldBytes = 32;

    /// <summary>How every P-256 SubjectPublicKeyInfo starts: the ecPublicKey and prime256v1 object ids,
    /// then a bit string holding an uncompressed point (0x04). X and Y follow, 32 bytes each.</summary>
    private static ReadOnlySpan<byte> P256KeyPrefix =>
    [
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
        0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
    ];

    private static bool TryReadP256PublicKey(byte[] spki, out ECPoint point)
    {
        point = default;
        var prefix = P256KeyPrefix.Length;
        if (spki.Length != prefix + 2 * P256FieldBytes || !spki.AsSpan(0, prefix).SequenceEqual(P256KeyPrefix))
            return false;
        point = new ECPoint { X = spki[prefix..(prefix + P256FieldBytes)], Y = spki[(prefix + P256FieldBytes)..] };
        return true;
    }

    /// <summary>Turns a DER ECDSA signature, SEQUENCE { INTEGER r, INTEGER s }, into the 64 bytes of r
    /// then s. Strict DER only: short lengths (a P-256 signature is at most 72 bytes), minimal positive
    /// integers, nothing after them.</summary>
    private static bool TryReadDerSignature(byte[] der, out byte[] rs)
    {
        rs = new byte[2 * P256FieldBytes];
        if (der.Length < 8 || der[0] != 0x30 || der[1] != der.Length - 2) return false;
        var at = 2;
        return TryReadInteger(der, ref at, rs.AsSpan(0, P256FieldBytes))
               && TryReadInteger(der, ref at, rs.AsSpan(P256FieldBytes))
               && at == der.Length;
    }

    private static bool TryReadInteger(byte[] der, ref int at, Span<byte> into)
    {
        if (at + 2 > der.Length || der[at] != 0x02) return false;
        int length = der[at + 1];
        var start = at + 2;
        if (length == 0 || length > P256FieldBytes + 1 || start + length > der.Length) return false;
        var value = der.AsSpan(start, length);
        if ((value[0] & 0x80) != 0) return false;                                        // negative
        if (value.Length > 1 && value[0] == 0 && (value[1] & 0x80) == 0) return false;   // not minimal
        if (value[0] == 0) value = value[1..];
        if (value.Length > P256FieldBytes) return false;
        value.CopyTo(into[(P256FieldBytes - value.Length)..]);
        at = start + length;
        return true;
    }

    private static UpdateVerification Fail(string reason) => new(null, reason);

    private static bool TryField(string line, string prefix, out string value)
    {
        value = line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : "";
        return value.Length > 0;
    }

    private static bool IsVersion(string value) =>
        value.Length <= 32 && value.All(c => c is (>= '0' and <= '9') or '.') && Version.TryParse(value, out _);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
