using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// The occurrence starts of an event whose (recurrence-expanded) start falls in a half-open
/// window (occFrom, occTo]. Shared by the reminder dispatch job and the due-reminder query so the
/// window arithmetic lives in exactly one place.
///
/// <para>An all-day event is stored as its date at midnight UTC, but the day it names begins at
/// midnight where the event was written — two hours earlier in Vienna, five later in New York.
/// So an all-day start is tested on that instant, while what is returned stays the stored start:
/// it is the key reminders are logged under.</para>
/// </summary>
public static class ReminderOccurrences
{
    /// <summary>How far an all-day event's real start can lie from its stored one — no zone is a
    /// full day from UTC. Callers that pre-filter on the stored start widen by this.</summary>
    public static readonly TimeSpan AllDaySlack = TimeSpan.FromDays(1);

    public static IEnumerable<DateTime> InWindow(CalendarEvent ev, DateTime occFrom, DateTime occTo)
    {
        // Without a zone we can resolve (an all-day event synced from a phone carries none),
        // the day is taken to begin at midnight UTC.
        var zone = ev.IsAllDay && TimeZones.TryFind(ev.TimeZoneId, out var found) ? found : null;
        var slack = zone is null ? TimeSpan.Zero : AllDaySlack;

        if (ev.RRule is null)
        {
            if (Begins(ev.StartUtc, zone, occFrom, occTo))
            {
                yield return Truncate(ev.StartUtc);
            }
            yield break;
        }

        var end = ev.EndUtc ?? ev.StartUtc.AddHours(1);
        var exDates = RecurrenceExpander.ParseExDates(ev.ExDates);
        foreach (var occ in RecurrenceExpander.Expand(
                     ev.StartUtc, end, ev.TimeZoneId, ev.RRule, exDates, occFrom - slack, occTo.AddSeconds(1) + slack, ev.IsAllDay))
        {
            if (Begins(occ.StartUtc, zone, occFrom, occTo))
            {
                yield return Truncate(occ.StartUtc);
            }
        }
    }

    /// <summary>Whether a stored start really begins in (occFrom, occTo] — read in
    /// <paramref name="allDayZone"/> when it is an all-day date.</summary>
    private static bool Begins(DateTime storedStartUtc, TimeZoneInfo? allDayZone, DateTime occFrom, DateTime occTo)
    {
        var begins = allDayZone is null ? storedStartUtc : TimeZones.ToUtc(storedStartUtc, allDayZone);
        return begins > occFrom && begins <= occTo;
    }

    public static DateTime Truncate(DateTime dt) =>
        new(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
