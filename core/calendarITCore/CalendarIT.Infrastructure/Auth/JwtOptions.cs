namespace CalendarIT.Infrastructure.Auth;

/// <summary>
/// JWT + refresh-token settings, populated from environment variables
/// (<c>JWT_SIGNING_KEY</c>, <c>JWT_ISSUER</c>, <c>JWT_AUDIENCE</c>, ...). The signing key
/// must be at least 32 bytes for HMAC-SHA256.
/// </summary>
public sealed class JwtOptions
{
    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "calendarit";

    public string Audience { get; set; } = "calendarit";

    /// <summary>Access-token lifetime in minutes (short-lived).</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Refresh-token lifetime in days.</summary>
    public int RefreshTokenDays { get; set; } = 14;
}
