using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>EF Core-backed <see cref="ICalendarService"/>. All queries are scoped by owner.</summary>
public sealed class CalendarService(AppDbContext db, TimeProvider timeProvider) : ICalendarService
{
    public async Task<IReadOnlyList<CalendarDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var calendars = await db.Calendars.AsNoTracking()
            .Where(c => c.OwnerUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new CalendarDto(c.Id, c.Name, c.Events.Count))
            .ToListAsync(cancellationToken);
        if (calendars.Count > 0)
        {
            return calendars;
        }

        // Fresh account: create the default calendar so the UI always has one to work with
        // (the same lazy bootstrap the event/import/CalDAV paths use).
        var created = await CreateEntityAsync(userId, "Personal", cancellationToken);
        return [new CalendarDto(created.Id, created.Name, 0)];
    }

    public async Task<CalendarDto> CreateAsync(Guid userId, SaveCalendarRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await CreateEntityAsync(userId, request.Name.Trim(), cancellationToken);
        return new CalendarDto(entity.Id, entity.Name, 0);
    }

    public async Task<CalendarDto?> RenameAsync(Guid userId, Guid calendarId, SaveCalendarRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await db.Calendars
            .SingleOrDefaultAsync(c => c.Id == calendarId && c.OwnerUserId == userId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        entity.Name = request.Name.Trim();
        entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        var count = await db.Events.CountAsync(e => e.CalendarId == entity.Id, cancellationToken);
        return new CalendarDto(entity.Id, entity.Name, count);
    }

    public async Task<DeleteCalendarResult> DeleteAsync(Guid userId, Guid calendarId, CancellationToken cancellationToken = default)
    {
        var entity = await db.Calendars
            .SingleOrDefaultAsync(c => c.Id == calendarId && c.OwnerUserId == userId, cancellationToken);
        if (entity is null)
        {
            return DeleteCalendarResult.NotFound;
        }

        // Every account keeps at least one calendar — events need somewhere to live.
        var total = await db.Calendars.CountAsync(c => c.OwnerUserId == userId, cancellationToken);
        if (total <= 1)
        {
            return DeleteCalendarResult.LastCalendar;
        }

        db.Calendars.Remove(entity); // cascade deletes the calendar's events
        await db.SaveChangesAsync(cancellationToken);
        return DeleteCalendarResult.Deleted;
    }

    private async Task<Calendar> CreateEntityAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = new Calendar
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Calendars.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
