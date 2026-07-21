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

    /// <summary>Parses newline-separated ISO UTC EXDATEs into a set (truncated to seconds).</summary>
    public static IReadOnlySet<DateTime> ParseExDates(string? raw)
    {
        var set = new HashSet<DateTime>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateTime.TryParse(line, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                var utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                set.Add(new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc));
            }
        }
        return set;
    }

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
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var localStart = TimeZoneInfo.ConvertTimeFromUtc(masterStartUtc, tz); // wall-clock, Kind=Unspecified
            var localEnd = localStart + duration;
            start = new CalDateTime(localStart, timeZoneId);
            var evt = BuildEvent(start, new CalDateTime(localEnd, timeZoneId), rrule);
            return Enumerate(evt, duration, exDatesUtc, fromUtc, toUtc);
        }

        // No zone: treat stored times as UTC (floating events handled as UTC here).
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
