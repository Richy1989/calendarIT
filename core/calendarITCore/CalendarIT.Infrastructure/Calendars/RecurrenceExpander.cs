using System.Globalization;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Expands an RRULE into concrete UTC occurrences within a window, using Ical.Net so
/// recurrence + DST are computed against the event's IANA time zone. EXDATEs are applied
/// here (by UTC instant) rather than through Ical.Net, to keep the surface small.
/// </summary>
public static class RecurrenceExpander
{
    public readonly record struct Occurrence(DateTime StartUtc, DateTime EndUtc);

    /// <inheritdoc cref="ExDates.Parse"/>
    public static IReadOnlySet<DateTime> ParseExDates(string? raw) => ExDates.Parse(raw);

    public static IEnumerable<Occurrence> Expand(
        DateTime masterStartUtc,
        DateTime masterEndUtc,
        string? timeZoneId,
        string rrule,
        IReadOnlySet<DateTime> exDatesUtc,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var duration = masterEndUtc - masterStartUtc;

        CalDateTime start;
        // An id we can't resolve falls through to the UTC branch rather than throwing: rows
        // written before ids were normalised on save may still carry one, and a single such
        // row must not take down every range query the user makes.
        if (TimeZones.TryFind(timeZoneId, out var tz))
        {
            var localStart = TimeZoneInfo.ConvertTimeFromUtc(masterStartUtc, tz); // wall-clock, Kind=Unspecified
            var localEnd = localStart + duration;
            // Ical.Net resolves zones through tzdb, so it must get the id as stored (IANA);
            // tz.Id would be the Windows equivalent when running on Windows.
            start = new CalDateTime(localStart, timeZoneId!);
            var evt = BuildEvent(start, new CalDateTime(localEnd, timeZoneId!), rrule);
            return Enumerate(evt, duration, exDatesUtc, fromUtc, toUtc);
        }

        // No zone (or one this runtime can't resolve): treat stored times as UTC.
        start = new CalDateTime(DateTime.SpecifyKind(masterStartUtc, DateTimeKind.Utc));
        var endUtc = new CalDateTime(DateTime.SpecifyKind(masterEndUtc, DateTimeKind.Utc));
        var utcEvt = BuildEvent(start, endUtc, rrule);
        return Enumerate(utcEvt, duration, exDatesUtc, fromUtc, toUtc);
    }

    private static CalendarEvent BuildEvent(CalDateTime start, CalDateTime end, string rrule)
    {
        var evt = new CalendarEvent { Start = start, End = end };
        evt.RecurrenceRules.Add(new RecurrencePattern(rrule));
        return evt;
    }

    private static IEnumerable<Occurrence> Enumerate(
        CalendarEvent evt, TimeSpan duration, IReadOnlySet<DateTime> exDatesUtc, DateTime fromUtc, DateTime toUtc)
    {
        var lower = new CalDateTime(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc));
        foreach (var occurrence in evt.GetOccurrences(lower))
        {
            var startUtc = occurrence.Period.StartTime.AsUtc;
            if (startUtc >= toUtc)
            {
                yield break; // occurrences are ascending; stop once past the window
            }
            var key = new DateTime(startUtc.Ticks - (startUtc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
            if (startUtc < fromUtc || exDatesUtc.Contains(key))
            {
                continue;
            }
            yield return new Occurrence(startUtc, startUtc + duration);
        }
    }
}
