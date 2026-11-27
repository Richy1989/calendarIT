using CalendarIT.Application.Auth;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalendarIT.Infrastructure.Auth;

public sealed class AccountAdmin(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    ILogger<AccountAdmin> logger,
    IAuthService? auth = null,
    CredentialEpochs? epochs = null,
    IOptions<AuthOptions>? authOptions = null,
    TimeProvider? clock = null) : IAccountAdmin
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        AdminRole.IsAdminAsync(db, userId, cancellationToken);

    public async Task<IReadOnlyList<UserAccountDto>?> ListUsersAsync(Guid adminId, CancellationToken cancellationToken = default)
    {
        if (!await IsAdminAsync(adminId, cancellationToken))
        {
            return null;
        }
        var adminIds = (await AdminRole.MemberIdsAsync(db, cancellationToken)).ToHashSet();
        var rows = await db.Users.AsNoTracking()
            .Select(u => new { u.Id, u.Email, u.CreatedAtUtc, u.LastActiveAtUtc, u.EmailConfirmed, u.DisabledAtUtc })
            .ToListAsync(cancellationToken);
        return rows
            .OrderBy(u => u.EmailConfirmed) // waiting first
            .ThenBy(u => u.EmailConfirmed ? DateTime.MinValue : u.CreatedAtUtc ?? DateTime.MinValue)
            .ThenBy(u => u.Email, StringComparer.OrdinalIgnoreCase)
            .Select(u => new UserAccountDto(
                u.Id, u.Email ?? string.Empty, Utc(u.CreatedAtUtc), Utc(u.LastActiveAtUtc),
                u.EmailConfirmed, u.DisabledAtUtc is not null, adminIds.Contains(u.Id), u.Id == adminId))
            .ToList();
    }

    public async Task<AccountAdminOutcome> ActivateAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default)
    {
        var (denied, user) = await TargetAsync(adminId, userId, cancellationToken);
        if (user is null)
        {
            return denied!;
        }
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            logger.LogInformation("Admin {AdminId} activated user {UserId}", adminId, userId);
        }
        return AccountAdminOutcome.Done;
    }

    public async Task<AccountAdminOutcome> SetDisabledAsync(
        Guid adminId, Guid userId, bool disabled, CancellationToken cancellationToken = default)
    {
        var (denied, user) = await TargetAsync(adminId, userId, cancellationToken);
        if (user is null)
        {
            return denied!;
        }
        if (disabled && userId == adminId)
        {
            return AccountAdminOutcome.Refused("You can't disable your own account.");
        }
        DateTime? value = disabled ? time.GetUtcNow().UtcDateTime : null;
        // A plain UPDATE: Identity's UpdateAsync would also rotate the concurrency stamp under a
        // request that may be signing in at this moment.
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAtUtc, value), cancellationToken);
        if (disabled)
        {
            await SignOutEverywhereAsync(userId, cancellationToken);
        }
        logger.LogInformation("Admin {AdminId} {Action} user {UserId}", adminId, disabled ? "disabled" : "enabled", userId);
        return AccountAdminOutcome.Done;
    }

    public async Task<AccountAdminOutcome> DeleteAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default)
    {
        var (denied, user) = await TargetAsync(adminId, userId, cancellationToken);
        if (user is null)
        {
            return denied!;
        }
        if (userId == adminId)
        {
            return AccountAdminOutcome.Refused("You can't delete your own account.");
        }
        await SignOutEverywhereAsync(userId, cancellationToken);
        // Everything the account owns goes with it (cascades: calendars and their events and
        // tasks, categories, mail account, devices, push subscriptions, sessions, queued mail).
        var deleted = await users.DeleteAsync(user);
        if (!deleted.Succeeded)
        {
            return AccountAdminOutcome.Refused(string.Join(" ", deleted.Errors.Select(e => e.Description)));
        }
        logger.LogInformation("Admin {AdminId} deleted user {UserId} and all of its data", adminId, userId);
        return AccountAdminOutcome.Done;
    }

    public async Task<AccountAdminOutcome> SetAdminAsync(Guid adminId, Guid userId, bool admin, CancellationToken cancellationToken = default)
    {
        var (denied, user) = await TargetAsync(adminId, userId, cancellationToken);
        if (user is null)
        {
            return denied!;
        }
        if (admin)
        {
            if (!user.EmailConfirmed || user.DisabledAtUtc is not null)
            {
                return AccountAdminOutcome.Refused("Only an active, enabled account can be made an admin.");
            }
            await AdminRole.GrantAsync(db, userId);
        }
        else
        {
            if (userId == adminId)
            {
                return AccountAdminOutcome.Refused("You can't remove your own admin rights. Another admin can.");
            }
            await AdminRole.RevokeAsync(db, userId);
        }
        logger.LogInformation("Admin {AdminId} {Action} user {UserId}", adminId, admin ? "made an admin:" : "removed admin rights from", userId);
        return AccountAdminOutcome.Done;
    }

    public async Task<AccountAdminOutcome> CreateResetLinkAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default)
    {
        var (denied, user) = await TargetAsync(adminId, userId, cancellationToken);
        if (user is null)
        {
            return denied!;
        }
        if (user.Email is null)
        {
            return AccountAdminOutcome.Refused("This account has no email address.");
        }
        // The link itself is never logged: it is a working credential.
        var link = await PasswordResetLinks.CreateAsync(users, user, authOptions?.Value.PublicBaseUrl);
        logger.LogInformation("Admin {AdminId} created a password-reset link for user {UserId}", adminId, userId);
        return new AccountAdminOutcome(AccountAdminResult.Done, Link: link);
    }

    /// <summary>The account to act on, or why not: the caller isn't an admin, or there is no such account.</summary>
    private async Task<(AccountAdminOutcome? Denied, ApplicationUser? User)> TargetAsync(
        Guid adminId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(adminId, cancellationToken))
        {
            return (AccountAdminOutcome.Forbidden, null);
        }
        var user = await users.FindByIdAsync(userId.ToString());
        return user is null ? (AccountAdminOutcome.NotFound, null) : (null, user);
    }

    /// <summary>Every session revoked (immediately, see SessionValidator) and cached CalDAV logins retired.</summary>
    private async Task SignOutEverywhereAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (auth is not null)
        {
            await auth.RevokeSessionsAsync(userId, keepSessionId: null, cancellationToken);
        }
        else
        {
            await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, time.GetUtcNow()), cancellationToken);
        }
        epochs?.Bump(userId);
    }

    private static DateTimeOffset? Utc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
}
