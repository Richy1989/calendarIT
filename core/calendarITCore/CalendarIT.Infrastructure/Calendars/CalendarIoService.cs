using System.Globalization;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Ical.Net.Serialization.DataTypes;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;
using DomainEvent = CalendarIT.Domain.CalendarEvent;
using ICalCalendar = Ical.Net.Calendar;
using ICalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>iCalendar (.ics) import/export using Ical.Net, mapping to our event model.</summary>
public sealed class CalendarIoService(AppDbContext db, TimeProvider timeProvider) : ICalendarIoService
{
    public async Task<string> ExportAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var events = await db.Events.AsNoTracking()
            .Where(e => e.Calendar!.OwnerUserId == userId)
            .OrderBy(e => e.StartUtc)
            .ToListAsync(cancellationToken);

        var cal = new ICalCalendar { ProductId = "-//CalendarIT//EN" };
        foreach (var e in events)
        {
            cal.Events.Add(ToICalEvent(e));
        }
        return new CalendarSerializer().SerializeToString(cal);
    }

    public async Task<ImportResult> ImportAsync(Guid userId, string ics, CancellationToken cancellationToken = default)
    {
        var calendar = await GetOrCreateDefaultCalendarAsync(userId, cancellationToken);
        var parsed = ICalCalendar.Load(ics);
        if (parsed is null)
        {
            return new ImportResult(0, 0);
        }

        var existingUids = await db.Events
            .Where(e => e.Calendar!.OwnerUserId == userId)
            .Select(e => e.Uid)
            .ToListAsync(cancellationToken);
        var known = new HashSet<string>(existingUids, StringComparer.OrdinalIgnoreCase);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        int imported = 0, skipped = 0;

        foreach (var ve in parsed.Events)
        {
            if (ve.Start is null)
            {
                skipped++;
                continue;
            }

            var uid = string.IsNullOrWhiteSpace(ve.Uid) ? $"{Guid.NewGuid():N}@calendarit" : ve.Uid;
            if (!known.Add(uid))
            {
                skipped++;
                continue;
            }

            db.Events.Add(FromICalEvent(ve, calendar.Id, uid, now));
            imported++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ImportResult(imported, skipped);
    }

    private static ICalEvent ToICalEvent(DomainEvent e)
    {
        var ve = new ICalEvent
        {
            Uid = e.Uid,
            Summary = e.Title,
            Description = e.Description,
            Location = e.Location,
        };

        var end = e.EndUtc ?? (e.IsAllDay ? e.StartUtc.AddDays(1) : e.StartUtc.AddHours(1));

        if (e.IsAllDay)
        {
            ve.Start = new CalDateTime(DateOnly.FromDateTime(e.StartUtc));
            ve.End = new CalDateTime(DateOnly.FromDateTime(end));
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

    private static DomainEvent FromICalEvent(ICalEvent ve, Guid calendarId, string uid, DateTime now)
    {
        var isAllDay = !ve.Start!.HasTime;
        var startUtc = ve.Start.AsUtc;
        DateTime? endUtc = ve.End?.AsUtc;

        var rrule = ve.RecurrenceRules.Count > 0
            ? new RecurrencePatternSerializer().SerializeToString(ve.RecurrenceRules[0])
            : null;

        // Reading EXDATE back on import is deferred (Ical.Net v5's ExceptionDates shape
        // needs extra plumbing); export still writes EXDATE, and RRULE round-trips fully.
        string? exString = null;

        var colorValue = ve.Properties["COLOR"]?.Value?.ToString();

        return new DomainEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = uid,
            Title = string.IsNullOrWhiteSpace(ve.Summary) ? "(untitled)" : ve.Summary,
            Description = ve.Description,
            Location = ve.Location,
            Color = CssColorMap.ToHex(colorValue),
            StartUtc = startUtc,
            EndUtc = endUtc,
            IsAllDay = isAllDay,
            TimeZoneId = ve.Start.TzId,
            RRule = rrule,
            ExDates = string.IsNullOrEmpty(exString) ? null : exString,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static IEnumerable<DateTime> ParseExDates(string? raw)
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

    private async Task<Calendar> GetOrCreateDefaultCalendarAsync(Guid userId, CancellationToken cancellationToken)
    {
        var calendar = await db.Calendars
            .Where(c => c.OwnerUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (calendar is not null)
        {
            return calendar;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        calendar = new Calendar { Id = Guid.NewGuid(), OwnerUserId = userId, Name = "Personal", CreatedAt = now, UpdatedAt = now };
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);
        return calendar;
    }
}
