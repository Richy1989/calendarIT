using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// "Which calendar does something land on when nobody said?" — the user's first calendar,
/// created on demand.
///
/// Four services needed this (creating an event, importing an .ics, delivering an invite to a
/// local guest, accepting one that arrived by email) and each carried its own copy, which had
/// already drifted: some persisted the new calendar immediately and some left it to the caller,
/// so whether a fresh account ended up with a "Personal" calendar depended on which path
/// happened to run first. One implementation, one answer: the calendar is saved before it is
/// returned, so the caller always gets a row that exists.
/// </summary>
public static class DefaultCalendar
{
    public const string Name = "Personal";

    public static async Task<Calendar> GetOrCreateAsync(
        AppDbContext db, TimeProvider timeProvider, Guid userId, CancellationToken cancellationToken = default)
    {
        // "First by CreatedAt" is the definition of default — it survives renames and deletes.
        var calendar = await db.Calendars
            .Where(c => c.OwnerUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (calendar is not null)
        {
            return calendar;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        calendar = new Calendar
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Name = Name,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);
        return calendar;
    }
}
