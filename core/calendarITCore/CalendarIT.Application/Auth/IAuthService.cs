namespace CalendarIT.Application.Auth;

/// <summary>
/// Account and session operations: registration, password login, refresh-token
/// rotation, and logout (refresh-token revocation). Access tokens are short-lived
/// JWTs; refresh tokens rotate on every use.
/// </summary>
public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<AuthResult> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);

    Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>Whether new accounts may be created on this instance.</summary>
    AuthConfig GetConfig();

    /// <summary>Changes the signed-in user's password, verifying the current one first. Every
    /// existing session is revoked — a password change should end sessions you didn't start.</summary>
    Task<PasswordResult> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Emails a reset link for <paramref name="request"/>'s address, valid for one use.
    /// Deliberately reports nothing about the address: the result is identical whether it is
    /// registered, unregistered, or unreachable, so this can't be used to enumerate accounts.
    /// </summary>
    /// <param name="linkBase">Absolute origin the link points at, e.g. "https://cal.example.com".</param>
    Task RequestPasswordResetAsync(ForgotPasswordRequest request, string linkBase, CancellationToken cancellationToken = default);

    /// <summary>Completes a reset with the emailed token, revoking every existing session.</summary>
    Task<PasswordResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
}
