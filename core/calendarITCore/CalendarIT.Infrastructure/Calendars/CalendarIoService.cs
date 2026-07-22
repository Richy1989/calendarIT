using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Persistence;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;
using ICalCalendar = Ical.Net.Calendar;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>iCalendar (.ics) import/export using Ical.Net, mapping to our event model.</summary>
public sealed class CalendarIoService(AppDbContext db, TimeProvider timeProvider) : ICalendarIoService
{
    public async Task<string> ExportAsync(
        Guid userId, IReadOnlyCollection<Guid>? calendarIds = null, CancellationToken cancellationToken = default)
    {
        var query = db.Events.AsNoTracking()
            .Where(e => e.Calendar!.OwnerUserId == userId);
        if (calendarIds is { Count: > 0 })
        {
            query = query.Where(e => calendarIds.Contains(e.CalendarId));
        }
        var events = await query
            .OrderBy(e => e.StartUtc)
            .ToListAsync(cancellationToken);

        var cal = new ICalCalendar { ProductId = "-//CalendarIT//EN" };
        foreach (var e in events)
        {
            cal.Events.Add(ICalEventMapper.ToICalEvent(e));
        }
        return new CalendarSerializer().SerializeToString(cal);
    }

    public async Task<ImportResult> ImportAsync(
        Guid userId, string ics, Guid? calendarId = null, string? newCalendarName = null,
        CancellationToken cancellationToken = default)
    {
        var calendar = await ResolveTargetCalendarAsync(userId, calendarId, newCalendarName, cancellationToken);
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

            db.Events.Add(ICalEventMapper.FromICalEvent(ve, calendar.Id, uid, now));
            imported++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ImportResult(imported, skipped);
    }

    /// <summary>Where imported events land: a fresh calendar when a name was given, else the
    /// requested calendar when the user owns it, else the default calendar.</summary>
    private async Task<Calendar> ResolveTargetCalendarAsync(
        Guid userId, Guid? calendarId, string? newCalendarName, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(newCalendarName))
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var created = new Calendar
            {
                Id = Guid.NewGuid(),
                OwnerUserId = userId,
                Name = newCalendarName.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Calendars.Add(created);
            await db.SaveChangesAsync(cancellationToken);
            return created;
        }

        if (calendarId is { } id)
        {
            var owned = await db.Calendars
                .SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == userId, cancellationToken);
            if (owned is not null)
            {
                return owned;
            }
        }

        return await GetOrCreateDefaultCalendarAsync(userId, cancellationToken);
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
