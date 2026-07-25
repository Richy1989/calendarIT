using System.Security.Claims;
using System.Buffers.Text;
using System.Text;
using CalendarIT.Application.Auth;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IPasswordResetMailer resetMailer,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthService> logger) : IAuthService
{
    public AuthConfig GetConfig() => new(RegistrationEnabled: !authOptions.Value.DisableRegistration);

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        if (authOptions.Value.DisableRegistration)
        {
            // A self-hosted instance is reachable by anyone who knows the URL — that's the point
            // of phone sync — so the operator needs a way to stop it being an open sign-up.
            logger.LogInformation("Rejected a registration attempt while registration is disabled");
            return AuthResult.Failure("Registration is disabled on this server.");
        }

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

        // Every new account starts with the default category set (events take their
        // display color from categories).
        CategoryDefaults.Seed(db, user.Id, timeProvider.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Registered new user {UserId}", user.Id);
        var tokens = await IssueTokensAsync(user, cancellationToken);
        return AuthResult.Success(tokens);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Uniform message so the response can't distinguish the two cases.
            return AuthResult.Failure("Invalid email or password.");
        }

        // Identity's lockout only engages through SignInManager, which this API doesn't use —
        // so the counter is driven here. Without it, password guessing against this endpoint
        // (and against CalDAV Basic, which shares the store) is unlimited.
        if (await userManager.IsLockedOutAsync(user))
        {
            logger.LogWarning("Login attempt for locked-out user {UserId}", user.Id);
            return AuthResult.Failure("Invalid email or password.");
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            await userManager.AccessFailedAsync(user);
            return AuthResult.Failure("Invalid email or password.");
        }

        await userManager.ResetAccessFailedCountAsync(user);
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

    public async Task<PasswordResult> ChangePasswordAsync(
        Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return PasswordResult.Failure("Account not found.");
        }

        var changed = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!changed.Succeeded)
        {
            return PasswordResult.Failure(changed.Errors.Select(e => e.Description).ToArray());
        }

        // Changing a password is how you get rid of someone else, so every session goes — the
        // caller's included. The SPA signs back in; a CalDAV client keeps working until its
        // cached credential expires (minutes) and then prompts.
        await RevokeAllForUserAsync(user.Id, timeProvider.GetUtcNow(), cancellationToken);
        logger.LogInformation("Password changed for user {UserId}; all sessions revoked", user.Id);
        return PasswordResult.Success();
    }

    public async Task RequestPasswordResetAsync(
        ForgotPasswordRequest request, string? linkBase, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkBase))
        {
            // No configured origin means the only address available is the one the caller asked
            // for, and mailing a working reset token to a host an anonymous request named is
            // account takeover by design. Refuse, and tell the operator how to fix it.
            logger.LogError(
                "Password reset requested but PUBLIC_BASE_URL is not set, so there is no trustworthy " +
                "address to send people to. No link was sent. Set PUBLIC_BASE_URL to the address users " +
                "type, e.g. https://calendar.example.com");
            return;
        }

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user?.Email is null)
        {
            // Unknown address: do nothing, say nothing. The endpoint answers identically either
            // way, so it can't be used to find out who has an account here.
            logger.LogInformation("Password reset requested for an address with no account");
            return;
        }

        // Base64Url because the raw token contains characters that don't survive a query string.
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var encoded = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
        var link = $"{linkBase.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(user.Email)}&token={encoded}";

        await resetMailer.SendAsync(user.Id, user.Email, link, cancellationToken);
    }

    public async Task<PasswordResult> ResetPasswordAsync(
        ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Same wording as a bad token: which of the two failed is not the caller's business.
            return PasswordResult.Failure("This reset link is no longer valid. Request a new one.");
        }

        string token;
        try
        {
            token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(request.Token));
        }
        catch (FormatException)
        {
            return PasswordResult.Failure("This reset link is no longer valid. Request a new one.");
        }

        var reset = await userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!reset.Succeeded)
        {
            // Password-policy complaints are worth showing; anything about the token is not.
            var errors = reset.Errors
                .Where(e => !e.Code.Contains("Token", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Description)
                .ToArray();
            return PasswordResult.Failure(errors.Length > 0
                ? errors
                : ["This reset link is no longer valid. Request a new one."]);
        }

        // A reset is the recovery path from a compromised account, so nothing survives it.
        await RevokeAllForUserAsync(user.Id, timeProvider.GetUtcNow(), cancellationToken);
        // Whoever locked themselves out guessing shouldn't stay locked out after proving control
        // of the mailbox — and that means lifting the lockout itself, not just zeroing the
        // counter: an unexpired LockoutEnd keeps refusing logins on its own.
        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        logger.LogInformation("Password reset completed for user {UserId}; all sessions revoked", user.Id);
        return PasswordResult.Success();
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
