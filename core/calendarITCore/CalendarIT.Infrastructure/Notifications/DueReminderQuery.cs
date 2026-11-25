using CalendarIT.Application.Notifications;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Mirrors <see cref="ReminderDispatchJob"/>'s window arithmetic, but read-only and per-user, and
/// only for <see cref="ReminderChannel.WebPush"/> reminders (Email is delivered by the job, not here).
/// </summary>
public sealed class DueReminderQuery(AppDbContext db, TimeProvider clock, ReminderSnoozes? snoozes = null) : IDueReminderQuery
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
        var allDayFrom = windowStart - ReminderOccurrences.AllDaySlack;
        var allDayTo = horizon + ReminderOccurrences.AllDaySlack;

        var reminders = await db.Reminders
            .Include(r => r.Event!).ThenInclude(e => e.Calendar)
            .Where(r => r.Channel == ReminderChannel.WebPush
                && r.Event!.Calendar!.OwnerUserId == userId
                && (r.Event!.RRule != null
                    || (r.Event!.StartUtc > windowStart && r.Event!.StartUtc <= horizon)
                    || (r.Event!.IsAllDay && r.Event!.StartUtc > allDayFrom && r.Event!.StartUtc <= allDayTo)))
            .ToListAsync(cancellationToken);

        var fallbackZones = await ReminderOccurrences.FallbackZonesAsync(db, reminders.Select(r => r.Event!), cancellationToken);
        var items = new List<DueReminderDto>();
        foreach (var reminder in reminders)
        {
            var offset = TimeSpan.FromMinutes(reminder.MinutesBefore);
            foreach (var occStart in ReminderOccurrences.InWindow(reminder.Event!, windowStart + offset, now + offset,
                             fallbackZones.GetValueOrDefault(reminder.Event!.CalendarId)))
            {
                items.Add(new DueReminderDto(
                    reminder.Id,
                    new DateTimeOffset(DateTime.SpecifyKind(occStart, DateTimeKind.Utc)),
                    reminder.Event!.Title,
                    reminder.Event!.Location,
                    reminder.Event!.IsAllDay,
                    snoozes?.CreateToken(userId, reminder.Id, occStart)));
            }
        }

        // Snoozed reminders coming back in the same window (the job pushes them to subscribed
        // browsers; this is for the ones that poll). Told apart by when they were due again.
        var snoozed = await db.ReminderSnoozes.AsNoTracking()
            .Include(s => s.Reminder!).ThenInclude(r => r.Event)
            .Where(s => s.DueAtUtc > windowStart && s.DueAtUtc <= now
                && s.Reminder!.Event!.Calendar!.OwnerUserId == userId)
            .ToListAsync(cancellationToken);
        foreach (var s in snoozed)
        {
            var ev = s.Reminder!.Event!;
            items.Add(new DueReminderDto(
                s.ReminderId,
                new DateTimeOffset(DateTime.SpecifyKind(s.OccurrenceStartUtc, DateTimeKind.Utc)),
                ev.Title,
                ev.Location,
                ev.IsAllDay,
                snoozes?.CreateToken(userId, s.ReminderId, s.OccurrenceStartUtc),
                new DateTimeOffset(DateTime.SpecifyKind(s.DueAtUtc, DateTimeKind.Utc))));
        }
        return items;
    }
}
