using System.Text.Json;

namespace AzureInventoryMonitor.Core.Auth;

/// <summary>
/// Reads the `iss` claim from a JWT's payload WITHOUT validating signature or
/// expiry — used only to route a request to the correct JwtBearer scheme
/// (Program.cs's "EntraOrLocal" policy scheme). Actual validation happens in
/// whichever scheme the token gets forwarded to.
/// </summary>
public static class JwtIssuerReader
{
    public static string? TryReadIssuer(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("iss", out var iss) ? iss.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
