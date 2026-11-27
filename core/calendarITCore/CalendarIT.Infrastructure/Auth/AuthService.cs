using System.IdentityModel.Tokens.Jwt;
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
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalendarIT.Infrastructure.Auth;

/// <summary>
/// Implements registration, login, refresh-token rotation, sessions, and logout on top of
/// ASP.NET Core Identity (<see cref="UserManager{TUser}"/>) and <see cref="ITokenService"/>.
/// </summary>
public sealed class AuthService(
    UserManager<ApplicationUser> userManager,
    AppDbContext db,
    ITokenService tokenService,
    TimeProvider timeProvider,
    IPasswordResetMailer resetMailer,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthService> logger,
    IMemoryCache? cache = null,
    CredentialEpochs? epochs = null,
    IEmailConfirmations? confirmations = null,
    IAdminSignupNotifier? adminNotifier = null) : IAuthService
{
    /// <summary>
    /// How long after a token was rotated a second presentation of it is taken for a race, not a
    /// theft. Two tabs that refresh at the same moment both send the same token; the second one
    /// used to trip reuse detection and sign the user out on every device.
    /// </summary>
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(30);

    /// <summary>One reset mail per account per this long — the endpoint is anonymous, and
    /// without a cooldown anyone could fill a user's inbox with them.</summary>
    private static readonly TimeSpan ResetCooldown = TimeSpan.FromMinutes(2);

    /// <summary>Shown only to someone who got the password right.</summary>
    public const string DisabledMessage = "This account has been disabled. Ask the server's admin.";

    public const string NotActivatedMessage =
        "This account isn't activated yet. Open the link in the activation email, or ask the server's admin to activate it.";

    public AuthConfig GetConfig() => new(
        RegistrationEnabled: !authOptions.Value.DisableRegistration,
        EmailConfirmationAvailable: confirmations?.Available ?? false);

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, AuthClient? client = null, CancellationToken cancellationToken = default)
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
            Email = request.Email,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
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

        // The first account on a server has nobody to activate it: it is active at once, and is
        // the admin who activates everyone after it.
        if (!await db.Users.AnyAsync(u => u.Id != user.Id, cancellationToken))
        {
            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);
            await AdminRole.GrantAsync(db, user.Id);
            logger.LogInformation("Registered new user {UserId}, the first account: active, and this server's admin", user.Id);
        }
        else
        {
            logger.LogInformation("Registered new user {UserId}, waiting for activation", user.Id);
        }

        // Everyone else signs in only once their address is activated — anyone can sign up with
        // anyone's address. By the mailed link when the server can send one, else by the admin.
        if (!user.EmailConfirmed)
        {
            var byEmail = confirmations?.Available == true;
            if (byEmail)
            {
                try
                {
                    await confirmations!.SendAsync(user.Id, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The user can ask for it again; the sign-up itself stands.
                    logger.LogWarning(ex, "Couldn't queue the activation mail for new user {UserId}", user.Id);
                }
            }
            var activation = byEmail ? AccountActivation.Email : AccountActivation.Admin;
            if (adminNotifier is not null)
            {
                await adminNotifier.NotifyAsync(activation, cancellationToken);
            }
            return AuthResult.Pending(activation);
        }
        var tokens = await StartSessionAsync(user, client, cancellationToken);
        return AuthResult.Success(tokens);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, AuthClient? client = null, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Same message — and the same work — so neither the response nor its timing tells an
            // unknown address from a wrong password. Skipping the hash made an unknown address
            // answer in ~2 ms and a real one in ~40 ms.
            PasswordTiming.Equalize(userManager.PasswordHasher, request.Password);
            return AuthResult.Failure("Invalid email or password.");
        }

        // Identity's lockout only engages through SignInManager, which this API doesn't use —
        // so the counter is driven here. Without it, password guessing against this endpoint
        // (and against CalDAV Basic, which shares the store) is unlimited.
        if (await userManager.IsLockedOutAsync(user))
        {
            PasswordTiming.Equalize(userManager.PasswordHasher, request.Password);
            logger.LogWarning("Login attempt for locked-out user {UserId}", user.Id);
            return AuthResult.Failure("Invalid email or password.");
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            await userManager.AccessFailedAsync(user);
            return AuthResult.Failure("Invalid email or password.");
        }

        await userManager.ResetAccessFailedCountAsync(user);
        if (user.DisabledAtUtc is not null)
        {
            // Like "not activated": said only to someone who got the password right.
            return AuthResult.Failure(DisabledMessage);
        }
        if (!user.EmailConfirmed)
        {
            // Said only after the password checked out, so it tells nobody else anything.
            return AuthResult.NotActivated(NotActivatedMessage);
        }
        var tokens = await StartSessionAsync(user, client, cancellationToken);
        return AuthResult.Success(tokens);
    }

    public async Task<AuthResult> RefreshAsync(RefreshTokenRequest request, AuthClient? client = null, CancellationToken cancellationToken = default)
    {
        var hash = tokenService.HashRefreshToken(request.RefreshToken);
        var now = timeProvider.GetUtcNow();

        var stored = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is null)
        {
            return AuthResult.Failure("Invalid refresh token.");
        }

        if (!stored.IsActive(now))
        {
            if (stored.RevokedAt is { } revokedAt)
            {
                if (stored.ReplacedByTokenHash is not null && now - revokedAt < RotationGrace)
                {
                    // Rotated a moment ago by a sibling tab of the same browser: refuse this copy,
                    // but don't treat it as theft — the sibling already holds the new token.
                    logger.LogInformation("Refresh token for session {SessionId} was just rotated; ignoring the duplicate", stored.SessionId);
                    return AuthResult.Failure("Invalid refresh token.");
                }

                // Presenting an already-rotated (revoked) token suggests theft/replay: revoke
                // the whole chain for that user as a precaution.
                logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking all tokens", stored.UserId);
                await RevokeAllForUserAsync(stored.UserId, now, cancellationToken);
            }
            return AuthResult.Failure("Invalid refresh token.");
        }

        var user = await userManager.FindByIdAsync(stored.UserId.ToString());
        if (user is null || !user.EmailConfirmed || user.DisabledAtUtc is not null)
        {
            return AuthResult.Failure("Invalid refresh token.");
        }

        // Claim the old token with a conditional update: of two refreshes racing on the same
        // token, exactly one gets a row back. Read-then-write let both succeed, forking the
        // session into two live chains.
        //
        // The claim and its successor commit together, under the user's token lock: a failed
        // insert leaves the old token usable (rather than a dead session, and reuse detection
        // signing out every device on the retry), and a sign-out can't land between the two and
        // find nothing to revoke while the new token is about to appear.
        var (rawRefresh, refreshExpiresAt) = tokenService.CreateRefreshToken();
        var newHash = tokenService.HashRefreshToken(rawRefresh);
        await using var transaction = await BeginTokenWriteAsync(stored.UserId, cancellationToken);
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.ReplacedByTokenHash, newHash), cancellationToken);
        if (claimed == 0)
        {
            return AuthResult.Failure("Invalid refresh token.");
        }

        var tokens = await IssueAsync(
            user, stored.SessionId, stored.SessionStartedAt, client ?? new AuthClient(stored.UserAgent, stored.IpAddress),
            rawRefresh, refreshExpiresAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AuthResult.Success(tokens);
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default)
    {
        var hash = tokenService.HashRefreshToken(request.RefreshToken);
        var stored = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is not null)
        {
            // Holding the refresh token is the authority to end its session.
            await RevokeSessionAsync(stored.UserId, stored.SessionId, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(
        Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        // ExpiresAt is compared in memory: SQLite can't compare DateTimeOffset in SQL.
        var live = (await db.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .ToListAsync(cancellationToken))
            .Where(t => t.ExpiresAt > now);

        return live
            .GroupBy(t => t.SessionId)
            .Select(g => g.MaxBy(t => t.CreatedAt)!)
            .Select(t => new SessionDto(
                t.SessionId, t.UserAgent, t.IpAddress, t.SessionStartedAt, t.CreatedAt, t.ExpiresAt,
                Current: t.SessionId == currentSessionId))
            .OrderByDescending(s => s.Current)
            .ThenByDescending(s => s.LastActiveAt)
            .ToList();
    }

    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        int revoked;
        await using (var transaction = await BeginTokenWriteAsync(userId, cancellationToken))
        {
            revoked = await db.RefreshTokens
                .Where(t => t.UserId == userId && t.SessionId == sessionId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (cache is not null)
        {
            SessionValidator.Evict(cache, [sessionId]);
        }
        return revoked > 0;
    }

    public async Task<int> RevokeSessionsAsync(Guid userId, Guid? keepSessionId, CancellationToken cancellationToken = default)
    {
        List<Guid> sessions;
        await using (var transaction = await BeginTokenWriteAsync(userId, cancellationToken))
        {
            sessions = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == userId && t.RevokedAt == null && t.SessionId != keepSessionId)
                .Select(t => t.SessionId)
                .Distinct()
                .ToListAsync(cancellationToken);
            if (sessions.Count == 0)
            {
                return 0;
            }

            var now = timeProvider.GetUtcNow();
            await db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null && t.SessionId != keepSessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (cache is not null)
        {
            SessionValidator.Evict(cache, sessions);
        }
        logger.LogInformation("Signed out {Count} session(s) of user {UserId}", sessions.Count, userId);
        return sessions.Count;
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
        // caller's included — and so do cached CalDAV logins made with the old password.
        await RevokeAllForUserAsync(user.Id, timeProvider.GetUtcNow(), cancellationToken);
        epochs?.Bump(user.Id);
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

        if (cache is not null)
        {
            var key = $"password-reset-sent:{user.Id:N}";
            if (cache.TryGetValue(key, out _))
            {
                logger.LogInformation("Password reset for user {UserId} skipped: one was sent moments ago", user.Id);
                return;
            }
            cache.Set(key, true, ResetCooldown);
        }

        var link = await PasswordResetLinks.CreateAsync(userManager, user, linkBase);
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
        epochs?.Bump(user.Id);
        // Whoever locked themselves out guessing shouldn't stay locked out after proving control
        // of the mailbox — and that means lifting the lockout itself, not just zeroing the
        // counter: an unexpired LockoutEnd keeps refusing logins on its own.
        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        logger.LogInformation("Password reset completed for user {UserId}; all sessions revoked", user.Id);
        return PasswordResult.Success();
    }

    /// <summary>A fresh sign-in: a new session with its first token pair.</summary>
    private Task<AuthTokens> StartSessionAsync(ApplicationUser user, AuthClient? client, CancellationToken cancellationToken)
    {
        var (rawRefresh, refreshExpiresAt) = tokenService.CreateRefreshToken();
        return IssueAsync(user, Guid.NewGuid(), timeProvider.GetUtcNow(), client, rawRefresh, refreshExpiresAt, cancellationToken);
    }

    private async Task<AuthTokens> IssueAsync(
        ApplicationUser user,
        Guid sessionId,
        DateTimeOffset sessionStartedAt,
        AuthClient? client,
        string rawRefresh,
        DateTimeOffset refreshExpiresAt,
        CancellationToken cancellationToken)
    {
        // Sign-in and every refresh: what the admin's user list shows as "last active".
        await LastActive.TouchAsync(db, user.Id, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        var roles = await userManager.GetRolesAsync(user);
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r))
            .Append(new Claim(JwtRegisteredClaimNames.Sid, sessionId.ToString()));

        var (accessToken, accessExpiresAt) = tokenService.CreateAccessToken(user, claims);

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenService.HashRefreshToken(rawRefresh),
            CreatedAt = timeProvider.GetUtcNow(),
            ExpiresAt = refreshExpiresAt,
            SessionId = sessionId,
            SessionStartedAt = sessionStartedAt,
            UserAgent = Clip(client?.UserAgent, 300),
            IpAddress = Clip(client?.IpAddress, 64),
        });

        await db.SaveChangesAsync(cancellationToken);

        return new AuthTokens(accessToken, accessExpiresAt, rawRefresh, refreshExpiresAt);
    }

    private async Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<Guid> sessions;
        await using (var transaction = await BeginTokenWriteAsync(userId, cancellationToken))
        {
            sessions = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .Select(t => t.SessionId)
                .Distinct()
                .ToListAsync(cancellationToken);
            await db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (cache is not null)
        {
            SessionValidator.Evict(cache, sessions);
        }
    }

    /// <summary>
    /// A transaction in which one user's refresh tokens are changed, holding that user's token lock
    /// until it ends: rotations and revocations of one user run one after the other.
    ///
    /// SQLite needs nothing more — EF opens its transactions with BEGIN IMMEDIATE, which holds the
    /// database's single write lock. PostgreSQL (READ COMMITTED) would let a revocation's UPDATE
    /// work from a snapshot taken before a concurrent rotation committed, so it would never see the
    /// rotated-in token; locking the user's row first makes every later statement start after the
    /// other transaction has finished, and so see its rows.
    /// </summary>
    private async Task<IDbContextTransaction> BeginTokenWriteAsync(Guid userId, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (db.Database.IsNpgsql())
        {
            var users = db.Model.FindEntityType(typeof(ApplicationUser))!;
            var table = users.GetSchema() is { } schema ? $"\"{schema}\".\"{users.GetTableName()}\"" : $"\"{users.GetTableName()}\"";
            var id = users.FindProperty(nameof(ApplicationUser.Id))!.GetColumnName();
            await db.Database.ExecuteSqlRawAsync($"SELECT 1 FROM {table} WHERE \"{id}\" = {{0}} FOR UPDATE", [userId], cancellationToken);
        }
        return transaction;
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>
/// Spends the same time on a sign-in that can't succeed as on one that checks a real password, so
/// response time doesn't reveal whether an account exists (or is locked).
/// </summary>
public static class PasswordTiming
{
    private static readonly ApplicationUser Nobody = new();
    private static string? _dummyHash;

    public static void Equalize(IPasswordHasher<ApplicationUser> hasher, string? password)
    {
        _dummyHash ??= hasher.HashPassword(Nobody, Guid.NewGuid().ToString("N"));
        hasher.VerifyHashedPassword(Nobody, _dummyHash, password ?? string.Empty);
    }
}
