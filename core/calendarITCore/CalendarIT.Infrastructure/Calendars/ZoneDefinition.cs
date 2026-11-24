using System.Collections.Concurrent;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using NodaTime;
using NodaTime.TimeZones;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// A VTIMEZONE a sender wrote out itself, under a name no tz database knows — Outlook's
/// "(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna", Exchange's "Customized Time
/// Zone". RFC 5545 makes such a definition authoritative for the TZID it carries, so it is read
/// here: its offsets around the event, the real zone that keeps the same clocks
/// (<see cref="Match"/>), and failing that its own wall-clock → UTC conversion.
///
/// <para>Outside input, so bounded like everything else that reads it: a few components, a few
/// transitions each, rules only from a couple of years before the event, and nothing throws.</para>
/// </summary>
public sealed class ZoneDefinition
{
    private const int MaxComponents = 20;
    private const int MaxOnsetsPerComponent = 50;
    private const int MaxCached = 512;
    private static readonly TimeSpan MaxOffset = TimeSpan.FromHours(18);
    private static readonly DateTime Antiquity = new(2, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly EvaluationOptions Options = new() { MaxUnmatchedIncrementsLimit = 10_000 };

    /// <summary>Real zones, so a definition is matched at most once per name, year and shape.</summary>
    private static readonly ConcurrentDictionary<string, string?> MatchCache = new(StringComparer.Ordinal);

    /// <summary>The tz database's own zones (no aliases), and those that stand for a Windows zone.</summary>
    private static readonly Lazy<(string[] Canonical, HashSet<string> Primary)> Zones = new(() => (
        TzdbDateTimeZoneSource.Default.CanonicalIdMap.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        TzdbDateTimeZoneSource.Default.WindowsMapping.PrimaryMapping.Values.ToHashSet(StringComparer.Ordinal)));

    private readonly (DateTime Utc, TimeSpan Offset)[] _transitions;
    private readonly TimeSpan _initial;
    private readonly int _year;

    private ZoneDefinition((DateTime Utc, TimeSpan Offset)[] transitions, TimeSpan initial, int year)
    {
        _transitions = transitions;
        _initial = initial;
        _year = year;
    }

    /// <summary>
    /// Reads <paramref name="vtz"/> around <paramref name="year"/> (the year before to the year
    /// after). Null when it defines no usable offset.
    /// </summary>
    public static ZoneDefinition? Read(VTimeZone vtz, int year)
    {
        try
        {
            year = Math.Clamp(year, 2, 9997);
            var windowStart = new DateTime(year - 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var windowEnd = new DateTime(year + 2, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var transitions = new List<(DateTime Utc, TimeSpan Offset, TimeSpan From)>();

            foreach (var component in vtz.TimeZoneInfos.Take(MaxComponents))
            {
                if (component.OffsetTo?.Offset is not { } to || to.Duration() > MaxOffset)
                {
                    continue;
                }
                var from = component.OffsetFrom?.Offset is { } f && f.Duration() <= MaxOffset ? f : to;
                List<DateTime> onsets;
                try
                {
                    onsets = Onsets(component, windowStart, windowEnd).ToList();
                }
                catch (Exception)
                {
                    continue; // one component we can't evaluate; the others still count
                }
                foreach (var onset in onsets)
                {
                    // An onset in antiquity ("since 0001") only says the offset holds from the
                    // start; clamped, so subtracting the offset can't underflow DateTime.
                    var clamped = onset < Antiquity ? Antiquity : onset;
                    transitions.Add((DateTime.SpecifyKind(clamped - from, DateTimeKind.Utc), to, from));
                }
            }
            if (transitions.Count == 0)
            {
                return null;
            }
            transitions.Sort((a, b) => a.Utc.CompareTo(b.Utc));
            return new ZoneDefinition(
                transitions.Select(t => (t.Utc, t.Offset)).ToArray(), transitions[0].From, year);
        }
        catch (Exception)
        {
            return null; // a definition we can't read is no definition
        }
    }

    /// <summary>When the component takes effect, as wall-clock times: its DTSTART, its yearly
    /// rule's dates near the window, its RDATEs. Wall clocks are carried as UTC values.</summary>
    private static IEnumerable<DateTime> Onsets(VTimeZoneInfo component, DateTime windowStart, DateTime windowEnd)
    {
        if (component.Start is not { } dtStart)
        {
            yield break;
        }
        var start = DateTime.SpecifyKind(dtStart.Value, DateTimeKind.Utc);
        if (start < windowEnd)
        {
            yield return start;
        }

        // Real definitions repeat yearly (DST changes). Others are ignored rather than walked.
        if (component.RecurrenceRule is { Frequency: FrequencyType.Yearly } rule && rule.Count is null or <= 1000)
        {
            // Walking a 1601 DTSTART up to today is wasted work, and the moment to bound it: start
            // the rule a couple of years before the window, same month and day (a yearly rule's
            // dates don't depend on DTSTART's year). Not with COUNT, which counts from DTSTART.
            var ruleStart = start;
            if (rule.Count is null && start.Year < windowStart.Year - 2)
            {
                var y = windowStart.Year - 2;
                ruleStart = new DateTime(y, start.Month, Math.Min(start.Day, DateTime.DaysInMonth(y, start.Month)),
                    start.Hour, start.Minute, start.Second, DateTimeKind.Utc);
            }
            var evt = new CalendarEvent
            {
                Start = new CalDateTime(ruleStart),
                RecurrenceRule = rule,
            };
            foreach (var occ in evt.GetOccurrences(new CalDateTime(windowStart.AddYears(-1)), Options)
                         .Select(o => DateTime.SpecifyKind(o.Period.StartTime.Value, DateTimeKind.Utc))
                         .TakeWhile(o => o < windowEnd)
                         .Take(MaxOnsetsPerComponent))
            {
                yield return occ;
            }
        }

        foreach (var rdate in component.RecurrenceDates.GetAllDates().Take(MaxOnsetsPerComponent))
        {
            var value = DateTime.SpecifyKind(rdate.Value, DateTimeKind.Utc);
            if (value < windowEnd)
            {
                yield return value;
            }
        }
    }

    /// <summary>The definition's UTC offset at <paramref name="utc"/>.</summary>
    public TimeSpan OffsetAt(DateTime utc)
    {
        var offset = _initial;
        foreach (var (at, to) in _transitions)
        {
            if (at > utc)
            {
                break;
            }
            offset = to;
        }
        return offset;
    }

    /// <summary>A wall-clock time in this zone as UTC.</summary>
    public DateTime ToUtc(DateTime wallClock)
    {
        var wall = DateTime.SpecifyKind(wallClock, DateTimeKind.Utc);
        var guess = wall - OffsetAt(wall);
        return wall - OffsetAt(guess);
    }

    /// <summary>
    /// The real zone (tz database id) that keeps exactly this definition's clocks for the year it
    /// was read around and the next, or null when none does. Of several that do — most of Europe
    /// for Outlook's Berlin definition — the one whose city <paramref name="tzid"/> names first wins,
    /// then one that stands for a Windows zone (Europe/Berlin rather than Africa/Ceuta).
    /// </summary>
    public string? Match(string tzid)
    {
        var key = $"{tzid}\n{_year}\n{_initial}\n{string.Join(';', _transitions.Select(t => $"{t.Utc.Ticks}:{t.Offset.Ticks}"))}";
        if (MatchCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var result = FindMatch(tzid);
        if (MatchCache.Count < MaxCached)
        {
            MatchCache.TryAdd(key, result);
        }
        return result;
    }

    private string? FindMatch(string tzid)
    {
        var from = new DateTime(_year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddYears(2);

        // The instants compared: every twelve hours, and either side of every change the
        // definition makes — a zone that changes its clocks at another moment differs there.
        var samples = new List<DateTime>();
        for (var t = from; t < to; t = t.AddHours(12))
        {
            samples.Add(t);
        }
        foreach (var (at, _) in _transitions)
        {
            if (at > from && at < to)
            {
                samples.Add(at.AddMinutes(-1));
                samples.Add(at.AddMinutes(1));
            }
        }
        var expected = samples.Select(OffsetAt).ToArray();
        var winter = OffsetAt(new DateTime(_year, 1, 15, 12, 0, 0, DateTimeKind.Utc));
        var summer = OffsetAt(new DateTime(_year, 7, 15, 12, 0, 0, DateTimeKind.Utc));

        var (canonical, primary) = Zones.Value;
        string? best = null;
        (int City, int Primary) bestRank = (int.MaxValue, int.MaxValue);
        foreach (var id in canonical)
        {
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(id);
            if (zone is null
                || OffsetOf(zone, new DateTime(_year, 1, 15, 12, 0, 0, DateTimeKind.Utc)) != winter
                || OffsetOf(zone, new DateTime(_year, 7, 15, 12, 0, 0, DateTimeKind.Utc)) != summer)
            {
                continue; // the cheap test first: most zones fail it
            }
            var same = true;
            for (var i = 0; i < samples.Count && same; i++)
            {
                same = OffsetOf(zone, samples[i]) == expected[i];
            }
            if (!same || TimeZones.Canonical(id) is null)
            {
                continue;
            }
            var city = id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ');
            var position = tzid.IndexOf(city, StringComparison.OrdinalIgnoreCase);
            var rank = (position < 0 ? int.MaxValue : position, primary.Contains(id) ? 0 : 1);
            if (rank.CompareTo(bestRank) < 0)
            {
                (best, bestRank) = (id, rank);
            }
        }
        return best;
    }

    private static TimeSpan OffsetOf(DateTimeZone zone, DateTime utc) =>
        zone.GetUtcOffset(Instant.FromDateTimeUtc(utc)).ToTimeSpan();
}
