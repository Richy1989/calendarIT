using System.Globalization;
using CalendarIT.Domain;
using Ical.Net.DataTypes;
using Ical.Net.Serialization.DataTypes;
using DomainEvent = CalendarIT.Domain.CalendarEvent;
using ICalAlarm = Ical.Net.CalendarComponents.Alarm;
using ICalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Maps between domain events and iCalendar VEVENTs. Shared by .ics import/export
/// (<see cref="CalendarIoService"/>) and the CalDAV endpoints so both sides agree on one
/// convention: stored all-day ends are the inclusive last day, while iCalendar's DTEND
/// is exclusive.
/// </summary>
public static class ICalEventMapper
{
    /// <summary>Non-standard property that carries our delivery channel through a VALARM
    /// round-trip, so a reminder synced out and back keeps its Email/WebPush choice.</summary>
    private const string ChannelProperty = "X-CALENDARIT-CHANNEL";

    public static ICalEvent ToICalEvent(DomainEvent e)
    {
        var ve = new ICalEvent
        {
            Uid = e.Uid,
            Summary = e.Title,
            Description = e.Description,
            Location = e.Location,
        };

        var end = e.EndUtc ?? e.StartUtc.AddHours(1);

        if (e.IsAllDay)
        {
            // Stored all-day ends are the inclusive last day; iCalendar's DTEND is exclusive,
            // so a one-day event (end == start) exports as DTEND = DTSTART + 1 day.
            ve.Start = new CalDateTime(DateOnly.FromDateTime(e.StartUtc));
            ve.End = new CalDateTime(DateOnly.FromDateTime((e.EndUtc ?? e.StartUtc).AddDays(1)));
        }
        else if (TimeZones.TryFind(e.TimeZoneId, out var tz))
        {
            // The id goes out as stored (IANA) — tz.Id would be the Windows name on Windows.
            ve.Start = new CalDateTime(TimeZoneInfo.ConvertTimeFromUtc(e.StartUtc, tz), e.TimeZoneId!);
            ve.End = new CalDateTime(TimeZoneInfo.ConvertTimeFromUtc(end, tz), e.TimeZoneId!);
        }
        else
        {
            ve.Start = new CalDateTime(DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc));
            ve.End = new CalDateTime(DateTime.SpecifyKind(end, DateTimeKind.Utc));
        }

        if (!string.IsNullOrWhiteSpace(e.RRule))
        {
            ve.RecurrenceRule = new RecurrenceRule(e.RRule);
        }
        foreach (var ex in ParseExDates(e.ExDates))
        {
            ve.ExceptionDates.Add(new CalDateTime(DateTime.SpecifyKind(ex, DateTimeKind.Utc)));
        }

        // The category rides along as CATEGORIES (RFC 5545); its color as COLOR (RFC 7986).
        // Callers must have the Category navigation loaded for categorized events.
        var colorName = CssColorMap.ToNearestName(e.Category?.Color ?? e.Color);
        if (colorName is not null)
        {
            ve.AddProperty("COLOR", colorName);
        }
        if (e.Category is not null)
        {
            ve.AddProperty("CATEGORIES", e.Category.Name);
        }

        // Reminders ride along as VALARMs so a synced client (a phone, say) shows them too.
        // ACTION:DISPLAY is what clients reliably surface; our own channel is preserved in an
        // X-property for a faithful round-trip. The server still sends its own copy regardless —
        // syncing a device is additive, so the same reminder may alert on both. Callers must
        // have the Reminders collection loaded.
        foreach (var r in e.Reminders)
        {
            var alarm = new ICalAlarm
            {
                Action = "DISPLAY",
                Description = string.IsNullOrWhiteSpace(e.Title) ? "Reminder" : e.Title,
                Trigger = new Trigger(-Duration.FromMinutes(r.MinutesBefore)),
            };
            alarm.AddProperty(ChannelProperty, r.Channel.ToString());
            ve.Alarms.Add(alarm);
        }
        return ve;
    }

    /// <summary>
    /// Reads a VEVENT's VALARMs back into reminders — for CalDAV PUTs and .ics import. Only alarms
    /// we can represent survive: a relative trigger at or before the start. Absolute triggers,
    /// after-start triggers, and <c>RELATED=END</c> are dropped, since our model is strictly
    /// "N minutes before start". The channel comes from our X-property when a faithful client
    /// preserved it, otherwise defaults to Email.
    /// </summary>
    public static List<Reminder> ReadReminders(ICalEvent ve)
    {
        var reminders = new List<Reminder>();
        foreach (var alarm in ve.Alarms)
        {
            if (TriggerMinutesBefore(alarm.Trigger) is not { } minutes)
            {
                continue;
            }
            reminders.Add(new Reminder
            {
                Id = Guid.NewGuid(),
                MinutesBefore = minutes,
                Channel = ParseChannel(alarm.Properties[ChannelProperty]?.Value?.ToString()),
            });
        }
        return reminders;
    }

    /// <summary>Minutes-before-start for a VALARM trigger, or null when it can't be represented.</summary>
    private static int? TriggerMinutesBefore(Trigger? trigger)
    {
        if (trigger?.Duration is not { } d)
        {
            return null; // absent, or an absolute DATE-TIME trigger
        }
        if (!string.IsNullOrEmpty(trigger.Related) &&
            trigger.Related.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            return null; // relative to the event end, which we don't model
        }
        var signed = DurationToMinutes(d);
        return signed > 0 ? null : -signed; // after-start is unrepresentable; before/at start → 0..N
    }

    /// <summary>Total minutes of an iCalendar duration, sign preserved. Robust to whichever way
    /// Ical.Net signs the components (value or the separate <c>Sign</c>): abs magnitude × sign.</summary>
    private static int DurationToMinutes(Duration d)
    {
        var magnitude =
            Math.Abs(d.Weeks ?? 0) * 7 * 24 * 60 +
            Math.Abs(d.Days ?? 0) * 24 * 60 +
            Math.Abs(d.Hours ?? 0) * 60 +
            Math.Abs(d.Minutes ?? 0) +
            (int)Math.Round(Math.Abs(d.Seconds ?? 0) / 60.0);
        return d.Sign < 0 ? -magnitude : magnitude;
    }

    private static ReminderChannel ParseChannel(string? raw) =>
        Enum.TryParse<ReminderChannel>(raw, ignoreCase: true, out var c) ? c : ReminderChannel.Email;

    public static DomainEvent FromICalEvent(
        ICalEvent ve, Guid calendarId, string uid, DateTime now, IReadOnlyList<Category>? categories = null)
    {
        var e = new DomainEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = uid,
            CreatedAt = now,
        };
        Apply(ve, e, now, categories);
        return e;
    }

    /// <summary>
    /// Copies a VEVENT's fields onto an entity (used for CalDAV PUT-updates as well as
    /// fresh imports). Existing <c>ExDates</c> are kept: EXDATE parsing is deferred
    /// (Ical.Net v5's ExceptionDates shape needs extra plumbing), and dropping them here
    /// would resurrect occurrences the user deleted in the web UI.
    /// </summary>
    /// <param name="categories">The owner's categories, for resolving the event's category
    /// from CATEGORIES (by name) or COLOR (nearest color). Null skips category resolution.</param>
    public static void Apply(ICalEvent ve, DomainEvent e, DateTime now, IReadOnlyList<Category>? categories = null)
    {
        var isAllDay = !ve.Start!.HasTime;
        var startUtc = AsUtcLenient(ve.Start);
        DateTime? endUtc = ve.End is null ? null : AsUtcLenient(ve.End);

        // iCalendar's DTEND is exclusive, but we store all-day ends as the inclusive last day
        // (the convention events created in the UI use). Without this, a one-day imported
        // event (DTEND = DTSTART + 1 day) would span two days in the calendar.
        if (isAllDay && endUtc is not null)
        {
            var inclusive = endUtc.Value.AddDays(-1);
            endUtc = inclusive < startUtc ? startUtc : inclusive;
        }

        var rrule = ve.RecurrenceRule is not null
            ? new RecurrenceRuleSerializer().SerializeToString(ve.RecurrenceRule)
            : null;

        // Category resolution, most-specific first: a CATEGORIES name matching one of the
        // user's categories wins; else an incoming COLOR snaps to the category with the
        // nearest color (phones typically send only COLOR). Nothing resolvable — no
        // property at all, or a value we can't parse — leaves the assignment unchanged, so
        // an edit synced from a phone never wipes what was chosen in the web UI.
        var colorHex = ReadColorHex(ve);
        var categoryName = ReadCategoryName(ve);

        e.Title = string.IsNullOrWhiteSpace(ve.Summary) ? "(untitled)" : ve.Summary;
        e.Description = ve.Description;
        e.Location = ve.Location;
        if (categories is { Count: > 0 })
        {
            var resolved = (string.IsNullOrWhiteSpace(categoryName)
                    ? null
                    : categories.FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase)))
                ?? NearestByColor(categories, colorHex);
            if (resolved is not null)
            {
                e.CategoryId = resolved.Id;
            }
        }
        else if (colorHex is not null)
        {
            e.Color = colorHex; // no categories to snap to — keep the hex as the legacy fallback
        }
        e.StartUtc = startUtc;
        e.EndUtc = endUtc;
        e.IsAllDay = isAllDay;
        // Only an id this runtime can resolve is stored; an invented TZID becomes floating
        // rather than a row that throws on every later read.
        e.TimeZoneId = TimeZones.Normalize(ve.Start.TzId);
        e.RRule = rrule;
        e.UpdatedAt = now;
    }

    /// <summary>
    /// A VEVENT date as UTC, tolerating a TZID neither Ical.Net's tzdb nor the runtime knows.
    /// RFC 5545 lets a sender put any string in TZID (and some do, e.g. Exchange's own zone
    /// names), but <c>CalDateTime.AsUtc</c> throws on one — which would reject the whole
    /// message. The wall-clock value is then read as UTC, matching how a floating time is
    /// treated everywhere else here.
    /// </summary>
    private static DateTime AsUtcLenient(CalDateTime value)
    {
        try
        {
            return value.AsUtc;
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException)
        {
            return DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        }
    }

    /// <summary>The VEVENT's COLOR as hex, or null when absent/unresolvable.</summary>
    public static string? ReadColorHex(ICalEvent ve)
        => CssColorMap.ToHex(ve.Properties["COLOR"]?.Value?.ToString());

    /// <summary>The first non-blank CATEGORIES entry, or null. Ical.Net may surface the
    /// property value as a string list or a single string depending on the source.</summary>
    public static string? ReadCategoryName(ICalEvent ve)
        => (ve.Properties["CATEGORIES"]?.Value as IEnumerable<object>)?
               .Select(v => v?.ToString()).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim()
           ?? ve.Properties["CATEGORIES"]?.Value?.ToString()?.Trim();

    /// <summary>The category whose color is nearest to <paramref name="hex"/> (squared RGB
    /// distance), or null when the hex or every category color fails to parse.</summary>
    private static Category? NearestByColor(IReadOnlyList<Category> categories, string? hex)
    {
        Category? best = null;
        var bestDist = int.MaxValue;
        foreach (var c in categories)
        {
            if (CssColorMap.Distance(c.Color, hex) is { } d && d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }

    /// <summary>Parses newline-separated ISO UTC EXDATEs (the stored format) into UTC instants.</summary>
    public static IEnumerable<DateTime> ParseExDates(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateTime.TryParse(line, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                yield return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
        }
    }
}
