using System.Security.Cryptography;
using System.Text;

namespace CloudRavel.Core.BotMonitor;

/// <summary>Token hashing, HMAC verification, and AES-GCM secret protection for BotMonitor.</summary>
public static class BotMonitorCrypto
{
    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string GenerateHmacSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public static string HashToken(string plaintextToken, string? pepper)
    {
        var input = string.IsNullOrEmpty(pepper)
            ? plaintextToken
            : $"{pepper}:{plaintextToken}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool FixedTimeEqualsHex(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public static string ComputeSignatureHex(string hmacSecretBase64, long timestampUnix, ReadOnlySpan<byte> body)
    {
        var secretBytes = Convert.FromBase64String(hmacSecretBase64);
        var prefix = Encoding.UTF8.GetBytes($"{timestampUnix}.");
        using var hmac = new HMACSHA256(secretBytes);
        hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
        hmac.TransformFinalBlock(body.ToArray(), 0, body.Length);
        return Convert.ToHexString(hmac.Hash!).ToLowerInvariant();
    }

    public static bool VerifySignature(string hmacSecretBase64, long timestampUnix, ReadOnlySpan<byte> body, string signatureHex)
    {
        if (string.IsNullOrWhiteSpace(signatureHex)) return false;
        var expected = ComputeSignatureHex(hmacSecretBase64, timestampUnix, body);
        return FixedTimeEqualsHex(expected, signatureHex.Trim().ToLowerInvariant());
    }

    public static string ProtectSecret(string plaintext, byte[] key32)
    {
        if (key32.Length != 32) throw new ArgumentException("Key must be 32 bytes.", nameof(key32));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key32, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        return $"{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(cipher)}.{Convert.ToBase64String(tag)}";
    }

    public static string UnprotectSecret(string protectedForm, byte[] key32)
    {
        if (key32.Length != 32) throw new ArgumentException("Key must be 32 bytes.", nameof(key32));
        var parts = protectedForm.Split('.');
        if (parts.Length != 3) throw new FormatException("Invalid protected secret format.");
        var nonce = Convert.FromBase64String(parts[0]);
        var cipher = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key32, tagSizeInBytes: 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// Resolves the AES key for encrypting bot HMAC secrets at rest.
    /// Non-Development hosts must set BotMonitor:DataProtectionKeyBase64 (32 bytes, base64).
    /// Development may derive from LOCAL_AUTH_JWT_SIGNING_KEY when the dedicated key is unset.
    /// </summary>
    public static byte[] ResolveProtectionKey(
        string? configuredBase64,
        bool allowDevelopmentFallback,
        string? localAuthSigningKey)
    {
        if (!string.IsNullOrWhiteSpace(configuredBase64))
        {
            var key = Convert.FromBase64String(configuredBase64.Trim());
            if (key.Length == 32) return key;
            throw new InvalidOperationException("BotMonitor:DataProtectionKeyBase64 must decode to exactly 32 bytes.");
        }

        if (allowDevelopmentFallback && !string.IsNullOrWhiteSpace(localAuthSigningKey))
            return SHA256.HashData(Encoding.UTF8.GetBytes(localAuthSigningKey));

        throw new InvalidOperationException(
            "BotMonitor:DataProtectionKeyBase64 is required when Platform:Environment is not Development.");
    }

    [Obsolete("Use ResolveProtectionKey")]
    public static byte[] DeriveProtectionKey(string? configuredBase64, string? fallbackSigningKey) =>
        ResolveProtectionKey(configuredBase64, allowDevelopmentFallback: true, fallbackSigningKey);
}
