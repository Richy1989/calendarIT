using System.Security.Claims;
using CalendarIT.Application.Auth;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CalendarIT.Infrastructure.Auth;

/// <summary>
/// Implements registration, login, refresh-token rotation, and logout on top of
/// ASP.NET Core Identity (<see cref="UserManager{TUser}"/>) and <see cref="ITokenService"/>.
/// </summary>
public sealed class AuthService(
    UserManager<ApplicationUser> userManager,
    AppDbContext db,
    ITokenService tokenService,
    TimeProvider timeProvider,
    ILogger<AuthService> logger) : IAuthService
{
    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
        {
            // Don't reveal which emails are registered.
            return AuthResult.Failure("Registration failed.");
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email
        };

        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return AuthResult.Failure(created.Errors.Select(e => e.Description).ToArray());
        }

        logger.LogInformation("Registered new user {UserId}", user.Id);
        var tokens = await IssueTokensAsync(user, cancellationToken);
        return AuthResult.Success(tokens);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
        {
            // Uniform message so timing/response can't distinguish the two cases meaningfully.
            return AuthResult.Failure("Invalid email or password.");
        }

        var tokens = await IssueTokensAsync(user, cancellationToken);
        return AuthResult.Success(tokens);
    }

    public async Task<AuthResult> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        var hash = tokenService.HashRefreshToken(request.RefreshToken);
        var now = timeProvider.GetUtcNow();

        var stored = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is null)
        {
            return AuthResult.Failure("Invalid refresh token.");
        }

        if (!stored.IsActive(now))
        {
            // Presenting an already-rotated (revoked) token suggests theft/replay: revoke
            // the whole chain for that user as a precaution.
            if (stored.RevokedAt is not null)
            {
                logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking all tokens", stored.UserId);
                await RevokeAllForUserAsync(stored.UserId, now, cancellationToken);
            }
            return AuthResult.Failure("Invalid refresh token.");
        }

        var user = await userManager.FindByIdAsync(stored.UserId.ToString());
        if (user is null)
        {
            return AuthResult.Failure("Invalid refresh token.");
        }

        var tokens = await IssueTokensAsync(user, cancellationToken, rotatingFrom: stored);
        return AuthResult.Success(tokens);
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default)
    {
        var hash = tokenService.HashRefreshToken(request.RefreshToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<AuthTokens> IssueTokensAsync(
        ApplicationUser user,
        CancellationToken cancellationToken,
        RefreshToken? rotatingFrom = null)
    {
        var roles = await userManager.GetRolesAsync(user);
        var roleClaims = roles.Select(r => new Claim(ClaimTypes.Role, r));

        var (accessToken, accessExpiresAt) = tokenService.CreateAccessToken(user, roleClaims);
        var (rawRefresh, refreshExpiresAt) = tokenService.CreateRefreshToken();
        var refreshHash = tokenService.HashRefreshToken(rawRefresh);
        var now = timeProvider.GetUtcNow();

        if (rotatingFrom is not null)
        {
            rotatingFrom.RevokedAt = now;
            rotatingFrom.ReplacedByTokenHash = refreshHash;
        }

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = refreshExpiresAt
        });

        await db.SaveChangesAsync(cancellationToken);

        return new AuthTokens(accessToken, accessExpiresAt, rawRefresh, refreshExpiresAt);
    }

    private async Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }
}
