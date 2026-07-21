using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Auth;

/// <summary>Registration payload for a new account.</summary>
public sealed class RegisterRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required, MinLength(8), MaxLength(128)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Credentials for password login.</summary>
public sealed class LoginRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required, MaxLength(128)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Exchanges a valid refresh token for a fresh token pair.</summary>
public sealed class RefreshTokenRequest
{
    [Required]
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>Revokes a refresh token (logout).</summary>
public sealed class LogoutRequest
{
    [Required]
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>Issued token pair returned to the SPA.</summary>
public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>Outcome of an auth operation: either a token pair or a list of errors.</summary>
public sealed record AuthResult(bool Succeeded, AuthTokens? Tokens, IReadOnlyList<string> Errors)
{
    public static AuthResult Success(AuthTokens tokens) => new(true, tokens, []);

    public static AuthResult Failure(params string[] errors) => new(false, null, errors);
}
