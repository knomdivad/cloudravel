using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace CloudRavel.Infrastructure.Auth;

/// <summary>
/// Verifies local username/password credentials and issues a JWT for the
/// "Local" JwtBearer scheme (see Program.cs). This is the non-Entra login path.
/// </summary>
public sealed class LocalAuthService : ILocalAuthService
{
    private readonly IUserRepository _userRepo;
    private readonly IConfiguration _config;
    private readonly ILogger<LocalAuthService> _logger;

    public LocalAuthService(IUserRepository userRepo, IConfiguration config, ILogger<LocalAuthService> logger)
    {
        _userRepo = userRepo;
        _config = config;
        _logger = logger;
    }

    public async Task<LocalAuthResult?> LoginAsync(string username, string password)
    {
        var user = await _userRepo.GetByUsernameAsync(username);
        if (user == null || !user.IsActive || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            _logger.LogWarning("Local login failed for username '{Username}'", username);
            return null;
        }

        var signingKey = _config["LocalAuth:JwtSigningKey"];
        if (string.IsNullOrEmpty(signingKey))
            throw new InvalidOperationException("LocalAuth:JwtSigningKey is not configured.");

        // Shorter-lived access tokens reduce the window for stolen JWTs (no refresh flow yet).
        var hours = int.TryParse(_config["LocalAuth:TokenLifetimeHours"], out var h) && h is > 0 and <= 24
            ? h : 4;
        var expiresAt = DateTime.UtcNow.AddHours(hours);
        var key = new SymmetricSecurityKey(LocalAuthConstants.DeriveSigningKey(signingKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("name", user.DisplayName),
            new Claim("role", user.GlobalRole),
        };

        var token = new JwtSecurityToken(
            issuer: LocalAuthConstants.Issuer,
            audience: LocalAuthConstants.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        // Never fail login solely because last_login_at could not be written
        // (e.g. transient SQL / session-option issues on the users table).
        try
        {
            await _userRepo.UpdateLastLoginAsync(user.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Login succeeded for {UserId} but UpdateLastLogin failed", user.UserId);
        }

        _logger.LogInformation("Local login succeeded for user {UserId} ({Username})", user.UserId, username);
        return new LocalAuthResult { Token = tokenString, ExpiresAt = expiresAt, User = user };
    }
}
