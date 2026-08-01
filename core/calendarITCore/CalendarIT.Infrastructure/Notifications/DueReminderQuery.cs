using CalendarIT.Application.Notifications;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Mirrors <see cref="ReminderDispatchJob"/>'s window arithmetic, but read-only and per-user, and
/// only for <see cref="ReminderChannel.WebPush"/> reminders (Email is delivered by the job, not here).
/// </summary>
public sealed class DueReminderQuery(AppDbContext db, TimeProvider clock) : IDueReminderQuery
{
    private static readonly TimeSpan MaxLookback = TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<DueReminderDto>> GetDueAsync(
        Guid userId, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        var now = ReminderOccurrences.Truncate(clock.GetUtcNow().UtcDateTime);
        var floor = now - MaxLookback;
        var windowStart = sinceUtc < floor ? floor : ReminderOccurrences.Truncate(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc));

        var longestOffset = await db.Reminders
            .Where(r => r.Channel == ReminderChannel.WebPush && r.Event!.Calendar!.OwnerUserId == userId)
            .Select(r => (int?)r.MinutesBefore)
            .MaxAsync(cancellationToken) ?? 0;
        var horizon = now.AddMinutes(longestOffset);

        var reminders = await db.Reminders
            .Include(r => r.Event!).ThenInclude(e => e.Calendar)
            .Where(r => r.Channel == ReminderChannel.WebPush
                && r.Event!.Calendar!.OwnerUserId == userId
                && (r.Event!.RRule != null
                    || (r.Event!.StartUtc > windowStart && r.Event!.StartUtc <= horizon)))
            .ToListAsync(cancellationToken);

        var items = new List<DueReminderDto>();
        foreach (var reminder in reminders)
        {
            var offset = TimeSpan.FromMinutes(reminder.MinutesBefore);
            foreach (var occStart in ReminderOccurrences.InWindow(reminder.Event!, windowStart + offset, now + offset))
            {
                items.Add(new DueReminderDto(
                    reminder.Id,
                    new DateTimeOffset(DateTime.SpecifyKind(occStart, DateTimeKind.Utc)),
                    reminder.Event!.Title,
                    reminder.Event!.Location));
            }
        }
        return items;
    }
}
