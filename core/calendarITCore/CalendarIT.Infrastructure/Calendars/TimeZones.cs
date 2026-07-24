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
/// </summary>
public static class TimeZones
{
    /// <summary>The zone for <paramref name="id"/>, or false when the runtime can't resolve it.</summary>
    public static bool TryFind(string? id, out TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone!);
    }

    /// <summary>True when the runtime can resolve <paramref name="id"/> to a real zone.</summary>
    public static bool IsKnown(string? id) => TryFind(id, out _);

    /// <summary>
    /// The id to persist: kept when it resolves, otherwise null (the event becomes floating).
    /// Storing only resolvable ids is what keeps every later read — expansion, export, CalDAV —
    /// from having to cope with a zone that doesn't exist.
    /// </summary>
    public static string? Normalize(string? id) => IsKnown(id) ? id!.Trim() : null;
}
