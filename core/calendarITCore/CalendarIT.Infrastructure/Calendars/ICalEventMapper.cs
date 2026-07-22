using System.Globalization;
using Ical.Net.DataTypes;
using Ical.Net.Serialization.DataTypes;
using DomainEvent = CalendarIT.Domain.CalendarEvent;
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
        else if (!string.IsNullOrWhiteSpace(e.TimeZoneId))
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(e.TimeZoneId);
            ve.Start = new CalDateTime(TimeZoneInfo.ConvertTimeFromUtc(e.StartUtc, tz), e.TimeZoneId);
            ve.End = new CalDateTime(TimeZoneInfo.ConvertTimeFromUtc(end, tz), e.TimeZoneId);
        }
        else
        {
            ve.Start = new CalDateTime(DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc));
            ve.End = new CalDateTime(DateTime.SpecifyKind(end, DateTimeKind.Utc));
        }

        if (!string.IsNullOrWhiteSpace(e.RRule))
        {
            ve.RecurrenceRules.Add(new RecurrencePattern(e.RRule));
        }
        foreach (var ex in ParseExDates(e.ExDates))
        {
            ve.ExceptionDates.Add(new CalDateTime(DateTime.SpecifyKind(ex, DateTimeKind.Utc)));
        }

        var colorName = CssColorMap.ToNearestName(e.Color);
        if (colorName is not null)
        {
            ve.AddProperty("COLOR", colorName);
        }
        return ve;
    }

    public static DomainEvent FromICalEvent(ICalEvent ve, Guid calendarId, string uid, DateTime now)
    {
        var e = new DomainEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = uid,
            CreatedAt = now,
        };
        Apply(ve, e, now);
        return e;
    }

    /// <summary>
    /// Copies a VEVENT's fields onto an entity (used for CalDAV PUT-updates as well as
    /// fresh imports). Existing <c>ExDates</c> are kept: EXDATE parsing is deferred
    /// (Ical.Net v5's ExceptionDates shape needs extra plumbing), and dropping them here
    /// would resurrect occurrences the user deleted in the web UI.
    /// </summary>
    public static void Apply(ICalEvent ve, DomainEvent e, DateTime now)
    {
        var isAllDay = !ve.Start!.HasTime;
        var startUtc = ve.Start.AsUtc;
        DateTime? endUtc = ve.End?.AsUtc;

        // iCalendar's DTEND is exclusive, but we store all-day ends as the inclusive last day
        // (the convention events created in the UI use). Without this, a one-day imported
        // event (DTEND = DTSTART + 1 day) would span two days in the calendar.
        if (isAllDay && endUtc is not null)
        {
            var inclusive = endUtc.Value.AddDays(-1);
            endUtc = inclusive < startUtc ? startUtc : inclusive;
        }

        var rrule = ve.RecurrenceRules.Count > 0
            ? new RecurrencePatternSerializer().SerializeToString(ve.RecurrenceRules[0])
            : null;

        var colorValue = ve.Properties["COLOR"]?.Value?.ToString();

        e.Title = string.IsNullOrWhiteSpace(ve.Summary) ? "(untitled)" : ve.Summary;
        e.Description = ve.Description;
        e.Location = ve.Location;
        e.Color = CssColorMap.ToHex(colorValue);
        e.StartUtc = startUtc;
        e.EndUtc = endUtc;
        e.IsAllDay = isAllDay;
        e.TimeZoneId = ve.Start.TzId;
        e.RRule = rrule;
        e.UpdatedAt = now;
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
