using System.Collections.Concurrent;
using NodaTime;
using NodaTime.TimeZones;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// One place to resolve an IANA time-zone id, and the only one allowed to decide what an
/// unknown id means.
///
/// Zone ids reach us from outside — an imported .ics, a CalDAV PUT, an invitation someone
/// emailed us, the browser's <c>Intl</c> guess — so an id the runtime has never heard of is
/// ordinary input, not an exceptional condition. Letting <see cref="TimeZoneInfo"/> throw on
/// one is what turned a single malformed message into a calendar that would not load and an
/// inbox scan that never got past it, so nothing here throws: an unresolvable id degrades to
/// "floating", i.e. the stored times are read as UTC.
///
/// <para>Ids are spelled many ways for the same zone: Outlook writes Windows names ("W. Europe
/// Standard Time"), old Mozilla clients a prefixed path ("/mozilla.org/20050126_1/Europe/Berlin"),
/// some writers the wrong case ("europe/berlin"). Ical.Net resolves ids through NodaTime's tz
/// database and silently reads one it doesn't know as UTC, so every id handed to it, and every
/// id stored, is first made <see cref="Canonical"/>: the tz database's own spelling.</para>
/// </summary>
public static class TimeZones
{
    /// <summary>Longer than any real id, with room for a vendor prefix; anything longer is junk.</summary>
    private const int MaxIdLength = 128;

    /// <summary>Distinct ids remembered; past this, ids are still resolved, just not cached
    /// (outside input can make up names without end).</summary>
    private const int MaxCached = 2048;

    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.Ordinal);

    /// <summary>The tz database's ids by any case, and Windows names to their primary tz zone.</summary>
    private static readonly Lazy<(Dictionary<string, string> Tzdb, Dictionary<string, string> Windows)> Ids = new(() =>
    {
        var tzdb = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in DateTimeZoneProviders.Tzdb.Ids)
        {
            tzdb.TryAdd(id, id);
        }
        var windows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (win, tz) in TzdbDateTimeZoneSource.Default.WindowsMapping.PrimaryMapping)
        {
            windows.TryAdd(win, tz);
        }
        return (tzdb, windows);
    });

    /// <summary>
    /// The tz database's spelling of the zone <paramref name="id"/> names — matched regardless of
    /// case, a Windows name mapped to its zone, a vendor path prefix dropped — or null when it
    /// names none (or the runtime can't use the zone it names). Never throws.
    /// </summary>
    public static string? Canonical(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        var trimmed = id.Trim();
        if (trimmed.Length > MaxIdLength)
        {
            return null;
        }
        if (Cache.TryGetValue(trimmed, out var cached))
        {
            return cached;
        }
        var result = Resolve(trimmed);
        if (Cache.Count < MaxCached)
        {
            Cache.TryAdd(trimmed, result);
        }
        return result;
    }

    private static string? Resolve(string id)
    {
        var (tzdb, windows) = Ids.Value;
        // The id itself, then what follows each '/' ("/mozilla.org/20050126_1/Europe/Berlin" →
        // … → "Europe/Berlin"), longest first so "America/Indiana/Indianapolis" stays whole.
        for (var rest = id; rest.Length > 0;)
        {
            var match = tzdb.GetValueOrDefault(rest) ?? windows.GetValueOrDefault(rest);
            if (match is not null && TimeZoneInfo.TryFindSystemTimeZoneById(match, out _))
            {
                return match;
            }
            var slash = rest.IndexOf('/');
            rest = slash < 0 ? "" : rest[(slash + 1)..];
        }
        return null;
    }

    /// <summary>The zone for <paramref name="id"/>, or false when the runtime can't resolve it.</summary>
    public static bool TryFind(string? id, out TimeZoneInfo zone)
    {
        if (Canonical(id) is { } canonical && TimeZoneInfo.TryFindSystemTimeZoneById(canonical, out var found))
        {
            zone = found;
            return true;
        }
        zone = TimeZoneInfo.Utc;
        return false;
    }

    /// <summary>True when <paramref name="id"/> names a zone we can resolve.</summary>
    public static bool IsKnown(string? id) => Canonical(id) is not null;

    /// <summary>
    /// The id to persist: its <see cref="Canonical"/> spelling, or null when it names no zone
    /// (the event becomes floating). Storing only resolvable ids is what keeps every later read —
    /// expansion, export, CalDAV — from having to cope with a zone that doesn't exist.
    /// </summary>
    public static string? Normalize(string? id) => Canonical(id);

    /// <summary>
    /// The instant a wall-clock time first shows in <paramref name="zone"/>. A time the clocks
    /// skip (spring forward) becomes the moment they land; one that comes round twice (fall
    /// back) is its first pass. Neither throws, unlike <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>.
    /// </summary>
    public static DateTime ToUtc(DateTime wallClock, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        for (var i = 0; i < 4 && zone.IsInvalidTime(local); i++)
        {
            local = local.AddMinutes(30);
        }
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }
}
