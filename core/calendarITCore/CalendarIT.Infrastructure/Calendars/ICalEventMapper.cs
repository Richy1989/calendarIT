using CalendarIT.Domain;
using Ical.Net.DataTypes;
using DomainEvent = CalendarIT.Domain.CalendarEvent;
using ICalAlarm = Ical.Net.CalendarComponents.Alarm;
using ICalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Maps between domain events and iCalendar VEVENTs. Shared by .ics import/export
/// (<see cref="CalendarIoService"/>), the CalDAV endpoints, and iMIP so all sides agree on one
/// convention: stored all-day ends are the inclusive last day, while iCalendar's DTEND
/// is exclusive.
///
/// <para>Everything read here arrived from outside (a phone, a file, someone's email), so the
/// reading side never trusts a size or a shape: text is clipped to the column widths — Postgres
/// enforces them, and one long SUMMARY used to fail the whole import — and an RRULE we won't
/// store turns the event into a one-off rather than rejecting it.</para>
/// </summary>
public static class ICalEventMapper
{
    /// <summary>Non-standard property that carries our delivery channel through a VALARM
    /// round-trip, so a reminder synced out and back keeps its Email/WebPush choice.</summary>
    private const string ChannelProperty = "X-CALENDARIT-CHANNEL";

    /// <summary>Column widths (see <c>AppDbContext</c>).</summary>
    public const int MaxUidLength = 255;
    private const int MaxTitleLength = 500;
    private const int MaxLocationLength = 500;
    private const int MaxDescriptionLength = 8000;

    /// <summary>VALARMs read per VEVENT; matches the API's own limit.</summary>
    private const int MaxReminders = 20;

    /// <summary>
    /// The series as VEVENTs: the master, then one VEVENT per override carrying its RECURRENCE-ID.
    /// The master's EXDATE lists only real deletions — the overridden instants stored alongside
    /// them in <see cref="DomainEvent.ExDates"/> are taken out again, since a client that saw them
    /// in EXDATE would drop the override too.
    /// </summary>
    /// <param name="inherited">The calendar's default category: written as the category of events
    /// without one of their own, so other clients see what the web UI shows.</param>
    public static List<ICalEvent> ToICalEvents(DomainEvent master, IEnumerable<DomainEvent>? overrides, Category? inherited = null)
    {
        var list = overrides?.Where(o => o.RecurrenceIdUtc is not null).OrderBy(o => o.RecurrenceIdUtc).ToList() ?? [];
        var overridden = list.Select(o => ExDates.TruncateToSeconds(o.RecurrenceIdUtc!.Value)).ToHashSet();

        var result = new List<ICalEvent> { ToICalEvent(master, overridden, inherited) };
        foreach (var o in list)
        {
            var ve = ToICalEvent(o, null, inherited);
            ve.RecurrenceIdentifier = new RecurrenceIdentifier(AsSeriesDateTime(o.RecurrenceIdUtc!.Value, master), null);
            result.Add(ve);
        }
        return result;
    }

    public static ICalEvent ToICalEvent(DomainEvent e, Category? inherited = null) => ToICalEvent(e, null, inherited);

    private static ICalEvent ToICalEvent(DomainEvent e, IReadOnlySet<DateTime>? overridden, Category? inherited)
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
            try
            {
                ve.RecurrenceRule = new RecurrenceRule(e.RRule);
            }
            catch (Exception)
            {
                // A rule stored before rules were validated on save. Exporting the event without
                // it beats failing the whole export (or CalDAV listing) over one row.
            }
            // EXDATE takes DTSTART's form: DATE for an all-day series, the series' zone otherwise —
            // some clients ignore an exclusion whose form doesn't match the start.
            foreach (var ex in ExDates.Parse(e.ExDates).Where(x => overridden is null || !overridden.Contains(x)).Order())
            {
                ve.ExceptionDates.Add(AsSeriesDateTime(ex, e));
            }
        }

        // The category rides along as CATEGORIES (RFC 5545); its color as COLOR (RFC 7986).
        // Callers must have the Category navigation loaded for categorized events. An event
        // without its own category goes out with its calendar's default, as the web UI shows it.
        var category = e.Category ?? (e.CategoryId is null ? inherited : null);
        var colorName = CssColorMap.ToNearestName(category?.Color ?? e.Color);
        if (colorName is not null)
        {
            ve.AddProperty("COLOR", colorName);
        }
        if (category is not null)
        {
            ve.AddProperty("CATEGORIES", category.Name);
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

    /// <summary>A UTC instant of the series written the way the series' DTSTART is written: a DATE
    /// for an all-day series, local time with the series' TZID, or UTC.</summary>
    private static CalDateTime AsSeriesDateTime(DateTime utc, DomainEvent series)
    {
        if (series.IsAllDay)
        {
            return new CalDateTime(DateOnly.FromDateTime(utc));
        }
        if (TimeZones.TryFind(series.TimeZoneId, out var tz))
        {
            return new CalDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz), series.TimeZoneId!);
        }
        return new CalDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
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
            if (reminders.Count == MaxReminders)
            {
                break;
            }
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
        // After-start is unrepresentable; before/at start → 0..N, capped like the API's own input.
        return signed > 0 || signed < -40_320 ? null : -signed;
    }

    /// <summary>Total minutes of an iCalendar duration, sign preserved. Robust to whichever way
    /// Ical.Net signs the components (value or the separate <c>Sign</c>): abs magnitude × sign.</summary>
    private static int DurationToMinutes(Duration d)
    {
        var magnitude =
            Math.Abs((long)(d.Weeks ?? 0)) * 7 * 24 * 60 +
            Math.Abs((long)(d.Days ?? 0)) * 24 * 60 +
            Math.Abs((long)(d.Hours ?? 0)) * 60 +
            Math.Abs((long)(d.Minutes ?? 0)) +
            (long)Math.Round(Math.Abs((long)(d.Seconds ?? 0)) / 60.0);
        var clamped = (int)Math.Min(magnitude, int.MaxValue);
        return d.Sign < 0 ? -clamped : clamped;
    }

    private static ReminderChannel ParseChannel(string? raw) =>
        Enum.TryParse<ReminderChannel>(raw, ignoreCase: true, out var c) ? c : ReminderChannel.Email;

    public static DomainEvent FromICalEvent(
        ICalEvent ve, Guid calendarId, string uid, DateTime now, IReadOnlyList<Category>? categories = null,
        Guid? inheritedCategoryId = null)
    {
        var e = new DomainEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = uid,
            CreatedAt = now,
        };
        Apply(ve, e, now, categories, inheritedCategoryId);
        return e;
    }

    /// <summary>
    /// Copies a VEVENT's fields onto an entity (used for CalDAV PUT-updates as well as fresh
    /// imports). EXDATEs are taken from the VEVENT only when it carries some: a client that
    /// doesn't manage exclusions sends none, and dropping the stored ones then would resurrect
    /// occurrences the user deleted in the web UI. When the series itself moved, though, the old
    /// exclusions no longer name real occurrences and are dropped. (Override instants are folded
    /// in afterwards by <see cref="SeriesWriter"/>.)
    /// </summary>
    /// <param name="categories">The owner's categories, for resolving the event's category
    /// from CATEGORIES (by name) or COLOR (nearest color). Null skips category resolution.</param>
    /// <param name="inheritedCategoryId">The calendar's default category, if it has one. An incoming
    /// category equal to it leaves the event inheriting (so changing the calendar's category later
    /// still recolors it), and a bare COLOR isn't snapped to a category at all — in a calendar with a
    /// default, the default is the better guess than the nearest color.</param>
    public static void Apply(
        ICalEvent ve, DomainEvent e, DateTime now, IReadOnlyList<Category>? categories = null, Guid? inheritedCategoryId = null)
    {
        var isAllDay = !ve.Start!.HasTime;
        var startUtc = AsUtcLenient(ve.Start);
        DateTime? endUtc = ve.End is null ? null : AsUtcLenient(ve.End);

        // iCalendar's DTEND is exclusive, but we store all-day ends as the inclusive last day
        // (the convention events created in the UI use). Without this, a one-day imported
        // event (DTEND = DTSTART + 1 day) would span two days in the calendar.
        if (isAllDay && endUtc is not null)
        {
            endUtc = endUtc.Value.AddDays(-1);
        }
        if (endUtc < startUtc)
        {
            endUtc = startUtc; // a DTEND before DTSTART is malformed; read it as zero-length
        }

        // An override never recurs itself, whatever it carries.
        var rrule = ve.RecurrenceIdentifier is null ? RecurrenceRules.FromExternal(ve.RecurrenceRule) : null;
        var recurrenceMoved = rrule != e.RRule || startUtc != e.StartUtc || isAllDay != e.IsAllDay;

        // Category resolution, most-specific first: a CATEGORIES name matching one of the
        // user's categories wins; else an incoming COLOR snaps to the category with the
        // nearest color (phones typically send only COLOR). Nothing resolvable — no
        // property at all, or a value we can't parse — leaves the assignment unchanged, so
        // an edit synced from a phone never wipes what was chosen in the web UI.
        var colorHex = ReadColorHex(ve);
        var categoryName = ReadCategoryName(ve);

        e.Title = string.IsNullOrWhiteSpace(ve.Summary) ? "(untitled)" : Clip(ve.Summary.Trim(), MaxTitleLength)!;
        e.Description = Clip(ve.Description, MaxDescriptionLength);
        e.Location = Clip(ve.Location, MaxLocationLength);
        if (categories is { Count: > 0 })
        {
            var resolved = (string.IsNullOrWhiteSpace(categoryName)
                    ? null
                    : categories.FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase)))
                ?? (inheritedCategoryId is null ? NearestByColor(categories, colorHex) : null);
            if (resolved is not null)
            {
                e.CategoryId = resolved.Id == inheritedCategoryId ? null : resolved.Id;
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

        var incomingExDates = ReadExDates(ve);
        if (rrule is null)
        {
            e.ExDates = null;
        }
        else if (incomingExDates.Count > 0)
        {
            e.ExDates = ExDates.Format(incomingExDates);
        }
        else if (recurrenceMoved)
        {
            e.ExDates = null;
        }
        e.UpdatedAt = now;
    }

    /// <summary>
    /// The VEVENT's EXDATEs as UTC instants (whole seconds). An all-day series' DATE exclusions
    /// become midnight UTC — the same instant an all-day start is stored at — and are matched by
    /// date on expansion anyway. Unreadable values are skipped, never fatal.
    /// </summary>
    public static List<DateTime> ReadExDates(ICalEvent ve)
    {
        var result = new List<DateTime>();
        try
        {
            foreach (var d in ve.ExceptionDates.GetAllDates())
            {
                result.Add(ExDates.TruncateToSeconds(AsUtcLenient(d)));
                if (result.Count >= 5_000)
                {
                    break; // a bounded list: each one is re-parsed on every expansion
                }
            }
        }
        catch (Exception)
        {
            // An EXDATE Ical.Net can't evaluate leaves the series without exclusions rather
            // than failing the event.
        }
        return result.Distinct().ToList();
    }

    /// <summary>The RECURRENCE-ID of an override VEVENT as a UTC instant (whole seconds), or null
    /// for a master or a plain event.</summary>
    public static DateTime? ReadRecurrenceIdUtc(ICalEvent ve)
    {
        var rid = ve.RecurrenceIdentifier?.StartTime;
        return rid is null ? null : ExDates.TruncateToSeconds(AsUtcLenient(rid));
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
            return DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc);
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException)
        {
            return DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        }
    }

    private static string? Clip(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

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
    public static IEnumerable<DateTime> ParseExDates(string? raw) => ExDates.Parse(raw);
}
