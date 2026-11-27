using CalendarIT.Application.Auth;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CalendarIT.Infrastructure.Auth;

public sealed class AccountAdmin(
    AppDbContext db, UserManager<ApplicationUser> users, ILogger<AccountAdmin> logger) : IAccountAdmin
{
    public Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        AdminRole.IsAdminAsync(db, userId, cancellationToken);

    public async Task<IReadOnlyList<PendingAccountDto>?> ListPendingAsync(Guid adminId, CancellationToken cancellationToken = default)
    {
        if (!await IsAdminAsync(adminId, cancellationToken))
        {
            return null;
        }
        var rows = await db.Users.AsNoTracking()
            .Where(u => !u.EmailConfirmed)
            .Select(u => new { u.Id, u.Email, u.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        return rows
            .OrderBy(u => u.CreatedAtUtc ?? DateTime.MinValue)
            .Select(u => new PendingAccountDto(u.Id, u.Email ?? string.Empty,
                u.CreatedAtUtc is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null))
            .ToList();
    }

    public async Task<AccountAdminResult> ActivateAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default)
    {
        var (result, user) = await PendingAsync(adminId, userId, cancellationToken);
        if (user is null)
        {
            return result;
        }
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
        logger.LogInformation("Admin {AdminId} activated user {UserId}", adminId, userId);
        return AccountAdminResult.Done;
    }

    public async Task<AccountAdminResult> DeleteAsync(Guid adminId, Guid userId, CancellationToken cancellationToken = default)
    {
        var (result, user) = await PendingAsync(adminId, userId, cancellationToken);
        if (user is null)
        {
            return result;
        }
        // Never-activated accounts can't sign in, so they hold only what sign-up seeded; their
        // rows go with them (cascades).
        await users.DeleteAsync(user);
        logger.LogInformation("Admin {AdminId} removed user {UserId}, which was never activated", adminId, userId);
        return AccountAdminResult.Done;
    }

    private async Task<(AccountAdminResult, ApplicationUser?)> PendingAsync(Guid adminId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(adminId, cancellationToken))
        {
            return (AccountAdminResult.Forbidden, null);
        }
        var user = await users.FindByIdAsync(userId.ToString());
        return user is { EmailConfirmed: false } ? (AccountAdminResult.Done, user) : (AccountAdminResult.NotFound, null);
    }
}
