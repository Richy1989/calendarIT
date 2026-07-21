using System.Security.Claims;
using CalendarIT.Infrastructure.Identity;

namespace CalendarIT.Infrastructure.Auth;

/// <summary>Issues access tokens (JWT) and opaque refresh tokens.</summary>
public interface ITokenService
{
    /// <summary>Builds a signed JWT access token for the user and returns it with its expiry.</summary>
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(ApplicationUser user, IEnumerable<Claim> extraClaims);

    /// <summary>Generates a cryptographically random refresh token (the raw value) and its expiry.</summary>
    (string RawToken, DateTimeOffset ExpiresAt) CreateRefreshToken();

    /// <summary>Hashes a raw refresh token (SHA-256, hex) for storage/lookup.</summary>
    string HashRefreshToken(string rawToken);
}
