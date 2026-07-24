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

/// <summary>Changes the signed-in user's password. The current one is required so a stolen
/// access token alone can't take the account over.</summary>
public sealed class ChangePasswordRequest
{
    [Required, MaxLength(128)]
    public string CurrentPassword { get; init; } = string.Empty;

    [Required, MinLength(8), MaxLength(128)]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>Starts password recovery for an address. Always answered the same way, whether or
/// not the address is registered.</summary>
public sealed class ForgotPasswordRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = string.Empty;
}

/// <summary>Completes recovery with the token from the emailed link.</summary>
public sealed class ResetPasswordRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    public string Token { get; init; } = string.Empty;

    [Required, MinLength(8), MaxLength(128)]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>What the sign-in screen needs to know before anyone has signed in.</summary>
public sealed record AuthConfig(bool RegistrationEnabled);

/// <summary>Outcome of an operation that changes a password rather than issuing tokens.</summary>
public sealed record PasswordResult(bool Succeeded, IReadOnlyList<string> Errors)
{
    public static PasswordResult Success() => new(true, []);

    public static PasswordResult Failure(params string[] errors) => new(false, errors);
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
