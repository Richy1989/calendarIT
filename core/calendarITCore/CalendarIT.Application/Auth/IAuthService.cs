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
}
