using System.Security.Cryptography;
using System.Text;

namespace AzureInventoryMonitor.Core.Auth;

/// <summary>
/// Issuer/audience shared between the token issuer (LocalAuthService) and the
/// "Local" JwtBearer scheme's validation parameters (Program.cs) — they must match.
/// </summary>
public static class LocalAuthConstants
{
    public const string Issuer = "aim-local-auth";
    public const string Audience = "aim-api";

    /// <summary>
    /// HS256 requires a key of at least 256 bits — the configured signing key
    /// (LocalAuth:JwtSigningKey) might be shorter, so derive a proper-length
    /// key via SHA-256 rather than using the configured string's bytes
    /// directly. Used identically when issuing (LocalAuthService) and
    /// validating (Program.cs) tokens — they must derive the same way.
    /// </summary>
    public static byte[] DeriveSigningKey(string configuredKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
}
