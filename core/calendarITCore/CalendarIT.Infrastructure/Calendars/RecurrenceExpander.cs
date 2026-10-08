using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Expands an RRULE into concrete UTC occurrences within a window, using Ical.Net so
/// recurrence + DST are computed against the event's IANA time zone. EXDATEs are applied
/// here (by UTC instant) rather than through Ical.Net, to keep the surface small.
///
/// <para>Nothing here throws, and nothing here runs unbounded. Every caller — the calendar view,
/// search, the reminder job that reads every user's series once a minute — would otherwise be one
/// bad row away from failing as a whole: a rule that no longer parses, or a valid rule that can
/// never match (BYMONTHDAY=30 in February), which Ical.Net walks to the year 9999 before giving up
/// — forty seconds of CPU for an hourly rule. Such a series simply yields nothing.</para>
/// </summary>
public static class RecurrenceExpander
{
    public readonly record struct Occurrence(DateTime StartUtc, DateTime EndUtc);

    /// <summary>Ceiling on occurrences one expansion yields, whatever the window.</summary>
    public const int MaxOccurrencesPerExpansion = 10_000;

    /// <summary>
    /// How many consecutive non-matching steps Ical.Net may take before giving up. Generous for
    /// any real rule (a daily Feb-29 series skips ~2,900 days at worst), and it stops an
    /// impossible one within milliseconds instead of at the end of the calendar.
    /// </summary>
    private static readonly EvaluationOptions Options = new() { MaxUnmatchedIncrementsLimit = 100_000 };

    /// <inheritdoc cref="ExDates.Parse"/>
    public static IReadOnlySet<DateTime> ParseExDates(string? raw) => ExDates.Parse(raw);

    /// <summary>
    /// Occurrences that overlap [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), ascending.
    /// An all-day series is a run of dates rather than instants, so it expands without a zone and
    /// its exclusions are matched by date.
    /// </summary>
    public static IEnumerable<Occurrence> Expand(
        DateTime masterStartUtc,
        DateTime masterEndUtc,
        string? timeZoneId,
        string rrule,
        IReadOnlySet<DateTime> exDatesUtc,
        DateTime fromUtc,
        DateTime toUtc,
        bool allDay = false)
    {
        var duration = masterEndUtc > masterStartUtc ? masterEndUtc - masterStartUtc : TimeSpan.Zero;

        CalendarEvent evt;
        try
        {
            // An id we can't resolve falls through to the UTC branch rather than throwing: rows
            // written before ids were normalised on save may still carry one, and a single such
            // row must not take down every range query the user makes.
            if (!allDay && TimeZones.TryFind(timeZoneId, out var tz))
            {
                var localStart = TimeZoneInfo.ConvertTimeFromUtc(masterStartUtc, tz); // wall-clock, Kind=Unspecified
                // Ical.Net resolves zones through tzdb, so it must get the id as stored (IANA);
                // tz.Id would be the Windows equivalent when running on Windows.
                evt = BuildEvent(
                    new CalDateTime(localStart, timeZoneId!), new CalDateTime(localStart + duration, timeZoneId!), rrule);
            }
            else
            {
                // No zone, an unknown one, or an all-day series: stored times are read as UTC.
                evt = BuildEvent(
                    new CalDateTime(DateTime.SpecifyKind(masterStartUtc, DateTimeKind.Utc)),
                    new CalDateTime(DateTime.SpecifyKind(masterStartUtc + duration, DateTimeKind.Utc)),
                    rrule);
            }
        }
        catch (Exception)
        {
            return []; // a rule that no longer parses: the series has no occurrences to show
        }

        return Enumerate(evt, duration, exDatesUtc, fromUtc, toUtc, allDay);
    }

    private static CalendarEvent BuildEvent(CalDateTime start, CalDateTime end, string rrule) =>
        new() { Start = start, End = end, RecurrenceRule = new RecurrenceRule(rrule) };

    private static IEnumerable<Occurrence> Enumerate(
        CalendarEvent evt, TimeSpan duration, IReadOnlySet<DateTime> exDatesUtc, DateTime fromUtc, DateTime toUtc, bool allDay)
    {
        var excludedDays = allDay ? exDatesUtc.Select(DateOnly.FromDateTime).ToHashSet() : null;

        // Start looking one duration early, so an occurrence that began before the window but is
        // still running inside it is found too (a multi-day series seen from its second day).
        var lower = new CalDateTime(DateTime.SpecifyKind(fromUtc - duration, DateTimeKind.Utc));

        IEnumerator<Ical.Net.DataTypes.Occurrence> source;
        try
        {
            source = evt.GetOccurrences(lower, Options).GetEnumerator();
        }
        catch (Exception)
        {
            yield break;
        }

        using (source)
        {
            var yielded = 0;
            while (yielded < MaxOccurrencesPerExpansion)
            {
                DateTime startUtc;
                try
                {
                    if (!source.MoveNext())
                    {
                        yield break;
                    }
                    startUtc = source.Current.Period.StartTime.AsUtc;
                }
                catch (Exception)
                {
                    // Ical.Net gave up mid-walk (limit hit, end of the calendar, a zone it can't
                    // resolve): stop here and keep what was found.
                    yield break;
                }

                if (startUtc >= toUtc)
                {
                    yield break; // occurrences are ascending; stop once past the window
                }

                var endUtc = startUtc + duration;
                var overlaps = duration > TimeSpan.Zero ? endUtc > fromUtc : startUtc >= fromUtc;
                var excluded = excludedDays is not null
                    ? excludedDays.Contains(DateOnly.FromDateTime(startUtc))
                    : exDatesUtc.Contains(ExDates.TruncateToSeconds(startUtc));
                if (!overlaps || excluded)
                {
                    continue;
                }

                yielded++;
                yield return new Occurrence(startUtc, endUtc);
            }
        }
    }
}
