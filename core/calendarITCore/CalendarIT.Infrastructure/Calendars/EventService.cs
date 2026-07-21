using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>EF Core-backed <see cref="IEventService"/>. All queries are scoped by owner.</summary>
public sealed class EventService(AppDbContext db, TimeProvider timeProvider) : IEventService
{
    public async Task<IReadOnlyList<EventDto>> GetEventsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var query = db.Events
            .AsNoTracking()
            .Where(e => e.Calendar!.OwnerUserId == userId);

        // Overlap filter: event starts before the window ends and ends at/after it begins.
        if (to is not null)
        {
            var toUtc = to.Value.UtcDateTime;
            query = query.Where(e => e.StartUtc < toUtc);
        }
        if (from is not null)
        {
            var fromUtc = from.Value.UtcDateTime;
            query = query.Where(e => (e.EndUtc ?? e.StartUtc) >= fromUtc);
        }

        var rows = await query.OrderBy(e => e.StartUtc).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
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

    public async Task<bool> DeleteAsync(Guid userId, Guid eventId, CancellationToken cancellationToken = default)
    {
        var deleted = await db.Events
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
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
        entity.UpdatedAt = now;
    }

    private static EventDto ToDto(CalendarEvent e) =>
        new(
            e.Id, e.Title, e.Description, e.Location, e.Color,
            new DateTimeOffset(DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc)),
            e.EndUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(e.EndUtc.Value, DateTimeKind.Utc)),
            e.IsAllDay);

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
