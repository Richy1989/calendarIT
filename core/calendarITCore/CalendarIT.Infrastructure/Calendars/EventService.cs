using System.Globalization;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>EF Core-backed <see cref="IEventService"/>. All queries are scoped by owner.</summary>
public sealed class EventService(AppDbContext db, TimeProvider timeProvider) : IEventService
{
    public async Task<IReadOnlyList<EventDto>> GetEventsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var fromUtc = from?.UtcDateTime;
        var toUtc = to?.UtcDateTime;
        var results = new List<EventDto>();

        // Single (non-recurring) events: filter by range in SQL.
        var singles = db.Events.AsNoTracking().Where(e => e.Calendar!.OwnerUserId == userId && e.RRule == null);
        if (toUtc is not null)
        {
            singles = singles.Where(e => e.StartUtc < toUtc);
        }
        if (fromUtc is not null)
        {
            singles = singles.Where(e => (e.EndUtc ?? e.StartUtc) >= fromUtc);
        }
        results.AddRange((await singles.ToListAsync(cancellationToken)).Select(ToDto));

        // Recurring series: fetch masters, expand within the window (needs a bounded range).
        if (fromUtc is not null && toUtc is not null)
        {
            var masters = await db.Events.AsNoTracking()
                .Where(e => e.Calendar!.OwnerUserId == userId && e.RRule != null)
                .ToListAsync(cancellationToken);

            foreach (var m in masters)
            {
                var end = m.EndUtc ?? m.StartUtc.AddHours(1);
                var exDates = ParseExDates(m.ExDates);
                foreach (var occ in RecurrenceExpander.Expand(m.StartUtc, end, m.TimeZoneId, m.RRule!, exDates, fromUtc.Value, toUtc.Value))
                {
                    results.Add(new EventDto(
                        m.Id, m.Title, m.Description, m.Location, m.Color,
                        new DateTimeOffset(occ.StartUtc, TimeSpan.Zero),
                        new DateTimeOffset(occ.EndUtc, TimeSpan.Zero),
                        m.IsAllDay, Recurring: true, Recurrence: m.RRule));
                }
            }
        }

        return results.OrderBy(r => r.Start).ToList();
    }

    public async Task<EventDto?> GetByIdAsync(Guid userId, Guid eventId, CancellationToken cancellationToken = default)
    {
        var entity = await db.Events.AsNoTracking()
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .SingleOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<EventDto> CreateAsync(Guid userId, SaveEventRequest request, CancellationToken cancellationToken = default)
    {
        var calendar = await GetOrCreateDefaultCalendarAsync(userId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var entity = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = calendar.Id,
            Uid = $"{Guid.NewGuid():N}@calendarit",
            CreatedAt = now,
        };
        Apply(entity, request, now);

        db.Events.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<EventDto?> UpdateAsync(Guid userId, Guid eventId, SaveEventRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await db.Events
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return null;
        }

        Apply(entity, request, timeProvider.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid eventId, DateTimeOffset? occurrence, CancellationToken cancellationToken = default)
    {
        var entity = await db.Events
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return false;
        }

        // Exclude a single occurrence of a series (EXDATE) rather than deleting everything.
        if (occurrence is not null && entity.RRule is not null)
        {
            var exDates = ParseExDates(entity.ExDates);
            exDates.Add(TruncateToSeconds(occurrence.Value.UtcDateTime));
            entity.ExDates = FormatExDates(exDates);
            entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        db.Events.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void Apply(CalendarEvent entity, SaveEventRequest request, DateTime now)
    {
        entity.Title = request.Title.Trim();
        entity.Description = request.Description;
        entity.Location = request.Location;
        entity.Color = request.Color;
        entity.StartUtc = request.Start.UtcDateTime;
        entity.EndUtc = request.End?.UtcDateTime;
        entity.IsAllDay = request.AllDay;
        entity.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone;
        entity.RRule = string.IsNullOrWhiteSpace(request.Recurrence) ? null : request.Recurrence.Trim();
        entity.ExDates = null; // editing the series resets any per-occurrence exclusions
        entity.UpdatedAt = now;
    }

    private static EventDto ToDto(CalendarEvent e) =>
        new(
            e.Id, e.Title, e.Description, e.Location, e.Color,
            new DateTimeOffset(DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc)),
            e.EndUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(e.EndUtc.Value, DateTimeKind.Utc)),
            e.IsAllDay, Recurring: e.RRule is not null, Recurrence: e.RRule);

    private static HashSet<DateTime> ParseExDates(string? raw)
    {
        var set = new HashSet<DateTime>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateTime.TryParse(line, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                set.Add(TruncateToSeconds(DateTime.SpecifyKind(dt, DateTimeKind.Utc)));
            }
        }
        return set;
    }

    private static string FormatExDates(IEnumerable<DateTime> exDates) =>
        string.Join('\n', exDates.Select(d => d.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));

    private static DateTime TruncateToSeconds(DateTime dt) =>
        new(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

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
        calendar = new Calendar
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Name = "Personal",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);
        return calendar;
    }
}
