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

    /// <summary>
    /// The sign-in this token belongs to. Constant across rotations, so the chain of tokens one
    /// device holds over time is one session — the unit the user sees and revokes in Settings, and
    /// the <c>sid</c> claim every access token from that chain carries.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>When the session's first token was issued (the sign-in itself).</summary>
    public DateTimeOffset SessionStartedAt { get; set; }

    /// <summary>The client's User-Agent at the last refresh, for recognising the device.</summary>
    public string? UserAgent { get; set; }

    /// <summary>The client's address at the last refresh, for recognising the device.</summary>
    public string? IpAddress { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}
