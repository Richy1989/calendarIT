namespace CalendarIT.Infrastructure.Identity;

/// <summary>
/// A persisted refresh token. Only the SHA-256 <see cref="TokenHash"/> is stored — never
/// the raw token — so a database leak cannot be replayed. Tokens rotate on every use:
/// the consumed token is revoked and linked to its successor via
/// <see cref="ReplacedByTokenHash"/>, which lets us detect reuse of a stolen token.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 (hex) of the raw refresh token handed to the client.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Hash of the token that superseded this one during rotation, if any.</summary>
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}
