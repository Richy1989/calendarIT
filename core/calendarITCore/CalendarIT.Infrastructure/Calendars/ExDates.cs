using System.Globalization;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// The stored EXDATE format: newline-separated ISO-8601 UTC instants, one per excluded
/// occurrence of a series.
///
/// This lived in three places — the expander, the iCal mapper, and the event service — with
/// three different return types and, more dangerously, two different opinions on rounding: two
/// truncated to whole seconds and one did not. Exclusions are matched by exact instant, so a
/// sub-second difference means an occurrence the user deleted quietly comes back. Parsing and
/// formatting therefore belong together, in one place, at one precision.
/// </summary>
public static class ExDates
{
    /// <summary>Parses the stored form into UTC instants truncated to whole seconds.
    /// Unparseable lines are skipped rather than failing the whole read.</summary>
    public static HashSet<DateTime> Parse(string? raw)
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
                set.Add(TruncateToSeconds(DateTime.SpecifyKind(dt, DateTimeKind.Utc)));
            }
        }
        return set;
    }

    /// <summary>Serializes UTC instants back to the stored form.</summary>
    public static string Format(IEnumerable<DateTime> exDates) =>
        string.Join('\n', exDates.Select(d => d.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));

    /// <summary>The precision exclusions are compared at — see the note above.</summary>
    public static DateTime TruncateToSeconds(DateTime dt) =>
        new(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
