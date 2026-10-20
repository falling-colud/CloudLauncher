using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CloudLauncher.Server.Discord;

/// <summary>
/// Checks that an interaction really came from Discord: an Ed25519 signature over the timestamp
/// header followed by the raw body, made with the key whose public half is the app's public key.
/// </summary>
/// <remarks>Discord checks this endpoint with deliberately bad signatures when the URL is saved and
/// from time to time after; anything but a 401 for those gets the endpoint switched off.</remarks>
public static class DiscordSignature
{
    public static bool Verify(string? publicKeyHex, string? signatureHex, string? timestamp, ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrWhiteSpace(publicKeyHex) || string.IsNullOrWhiteSpace(signatureHex)
            || string.IsNullOrWhiteSpace(timestamp))
            return false;
        byte[] key, signature;
        try
        {
            key = Convert.FromHexString(publicKeyHex.Trim());
            signature = Convert.FromHexString(signatureHex.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        if (key.Length != Ed25519PublicKeyParameters.KeySize || signature.Length != Ed25519.SignatureSize) return false;

        var stamp = Encoding.UTF8.GetBytes(timestamp);
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(key, 0));
        signer.BlockUpdate(stamp, 0, stamp.Length);
        var bytes = body.ToArray();
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.VerifySignature(signature);
    }

    private static class Ed25519
    {
        public const int SignatureSize = 64;
    }
}
