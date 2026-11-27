using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Auth;

/// <summary>
/// Records when an account was last used, for the admin's user list. One conditional UPDATE, and
/// only when the stored time is over an hour old — so a phone refreshing its token every few
/// minutes doesn't write the user row each time. Never touches the concurrency stamp.
/// </summary>
public static class LastActive
{
    private static readonly TimeSpan Resolution = TimeSpan.FromHours(1);

    public static async Task TouchAsync(AppDbContext db, Guid userId, DateTime nowUtc, CancellationToken ct = default)
    {
        var stale = nowUtc - Resolution;
        await db.Users
            .Where(u => u.Id == userId && (u.LastActiveAtUtc == null || u.LastActiveAtUtc < stale))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActiveAtUtc, nowUtc), ct);
    }
}
