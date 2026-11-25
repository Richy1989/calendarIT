using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

    /// <param name="fallbackZoneId">The zone an all-day event without one of its own is read in
    /// (see <see cref="FallbackZonesAsync"/>); with neither, its day begins at midnight UTC.</param>
    public static IEnumerable<DateTime> InWindow(CalendarEvent ev, DateTime occFrom, DateTime occTo, string? fallbackZoneId = null)
    {
        // An all-day event synced from a phone carries no zone: its day is the one its calendar
        // lives in, and only without that too does it begin at midnight UTC.
        var zone = ev.IsAllDay && (TimeZones.TryFind(ev.TimeZoneId, out var found) || TimeZones.TryFind(fallbackZoneId, out found))
            ? found
            : null;
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

    /// <summary>
    /// For each calendar holding a zone-less all-day event among <paramref name="events"/>, the
    /// zone its days are read in: the calendar's own, else that of its most recently changed event
    /// that has one — a phone writes all-day events without a zone, but its timed ones with it.
    /// One query for all of them, whatever the count.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> FallbackZonesAsync(
        AppDbContext db, IEnumerable<CalendarEvent> events, CancellationToken cancellationToken)
    {
        var zones = new Dictionary<Guid, string>();
        var needed = new HashSet<Guid>();
        foreach (var ev in events.Where(e => e.IsAllDay && !TimeZones.TryFind(e.TimeZoneId, out _)))
        {
            if (ev.Calendar?.TimeZoneId is { } own && TimeZones.TryFind(own, out _))
            {
                zones[ev.CalendarId] = own;
            }
            else
            {
                needed.Add(ev.CalendarId);
            }
        }
        needed.ExceptWith(zones.Keys);
        if (needed.Count == 0)
        {
            return zones;
        }

        var candidates = await db.Events.AsNoTracking()
            .Where(e => needed.Contains(e.CalendarId) && e.TimeZoneId != null)
            .GroupBy(e => e.CalendarId)
            .Select(g => g.OrderByDescending(e => e.UpdatedAt).Select(e => new { e.CalendarId, e.TimeZoneId }).First())
            .ToListAsync(cancellationToken);
        foreach (var c in candidates)
        {
            if (TimeZones.TryFind(c.TimeZoneId, out _))
            {
                zones[c.CalendarId] = c.TimeZoneId!;
            }
        }
        return zones;
    }

    public static DateTime Truncate(DateTime dt) =>
        new(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
