using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// The occurrence starts of an event whose (recurrence-expanded) start falls in a half-open
/// window (occFrom, occTo]. Shared by the reminder dispatch job and the due-reminder query so the
/// window arithmetic lives in exactly one place.
/// </summary>
public static class ReminderOccurrences
{
    public static IEnumerable<DateTime> InWindow(CalendarEvent ev, DateTime occFrom, DateTime occTo)
    {
        if (ev.RRule is null)
        {
            if (ev.StartUtc > occFrom && ev.StartUtc <= occTo)
            {
                yield return Truncate(ev.StartUtc);
            }
            yield break;
        }

        var end = ev.EndUtc ?? ev.StartUtc.AddHours(1);
        var exDates = RecurrenceExpander.ParseExDates(ev.ExDates);
        foreach (var occ in RecurrenceExpander.Expand(ev.StartUtc, end, ev.TimeZoneId, ev.RRule, exDates, occFrom, occTo.AddSeconds(1)))
        {
            if (occ.StartUtc > occFrom && occ.StartUtc <= occTo)
            {
                yield return Truncate(occ.StartUtc);
            }
        }
    }

    public static DateTime Truncate(DateTime dt) =>
        new(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
