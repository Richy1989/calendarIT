namespace CalendarIT.Application.Auth;

/// <summary>
/// Account and session operations: registration, password login, refresh-token
/// rotation, and logout (refresh-token revocation). Access tokens are short-lived
/// JWTs; refresh tokens rotate on every use.
/// </summary>
public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request, AuthClient? client = null, CancellationToken cancellationToken = default);

    Task<AuthResult> LoginAsync(LoginRequest request, AuthClient? client = null, CancellationToken cancellationToken = default);

    Task<AuthResult> RefreshAsync(RefreshTokenRequest request, AuthClient? client = null, CancellationToken cancellationToken = default);

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
    /// <param name="linkBase">
    /// Absolute origin the link points at, e.g. "https://cal.example.com" — from the operator's
    /// configuration, never from the request. Null when none is configured, in which case no mail
    /// is sent: a link is a bearer credential, and one pointing at an address a caller chose is
    /// worse than no link at all.
    /// </param>
    Task RequestPasswordResetAsync(ForgotPasswordRequest request, string? linkBase, CancellationToken cancellationToken = default);

    /// <summary>Completes a reset with the emailed token, revoking every existing session.</summary>
    Task<PasswordResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>The user's signed-in sessions, most recently active first.</summary>
    Task<IReadOnlyList<SessionDto>> ListSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default);

    /// <summary>Signs one session out. Its refresh token stops working at once, and so do the
    /// access tokens it already holds. False when the user has no such active session.</summary>
    Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Signs out every session except <paramref name="keepSessionId"/> (null: all of
    /// them — "sign out everywhere"). Returns how many were signed out.</summary>
    Task<int> RevokeSessionsAsync(Guid userId, Guid? keepSessionId, CancellationToken cancellationToken = default);
}
