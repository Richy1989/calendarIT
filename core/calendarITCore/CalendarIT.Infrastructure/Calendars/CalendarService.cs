using CalendarIT.Application;
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
            // Edited occurrences are rows of their own but not appointments of their own.
            .Select(c => new CalendarDto(c.Id, c.Name, c.Events.Count(e => e.SeriesMasterId == null), c.DefaultCategoryId))
            .ToListAsync(cancellationToken);
        if (calendars.Count > 0)
        {
            return calendars;
        }

        // Fresh account: create the default calendar so the UI always has one to work with
        // (the same lazy bootstrap the event/import/CalDAV paths use).
        var created = await CreateEntityAsync(userId, "Personal", null, cancellationToken);
        return [new CalendarDto(created.Id, created.Name, 0)];
    }

    public async Task<CalendarDto> CreateAsync(Guid userId, SaveCalendarRequest request, CancellationToken cancellationToken = default)
    {
        var categoryId = await OwnedCategoryAsync(userId, request.DefaultCategoryId, cancellationToken);
        var entity = await CreateEntityAsync(userId, request.Name.Trim(), categoryId, cancellationToken);
        return new CalendarDto(entity.Id, entity.Name, 0, entity.DefaultCategoryId);
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
        return await ToDtoAsync(entity, cancellationToken);
    }

    public async Task<CalendarDto?> SetDefaultCategoryAsync(
        Guid userId, Guid calendarId, Guid? categoryId, CancellationToken cancellationToken = default)
    {
        var entity = await db.Calendars
            .SingleOrDefaultAsync(c => c.Id == calendarId && c.OwnerUserId == userId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        entity.DefaultCategoryId = await OwnedCategoryAsync(userId, categoryId, cancellationToken);
        entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        // A CalDAV client decides from the CTag whether to re-read the calendar, and its events'
        // exported category just changed — so the change has to show there too.
        await db.Events
            .Where(e => e.CalendarId == entity.Id && e.CategoryId == null && e.SeriesMasterId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.UpdatedAt, entity.UpdatedAt), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(entity, cancellationToken);
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

    /// <summary>The category id when it's one of the user's; null passes through as "none".</summary>
    private async Task<Guid?> OwnedCategoryAsync(Guid userId, Guid? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is not { } id)
        {
            return null;
        }
        return await db.Categories.AnyAsync(c => c.Id == id && c.OwnerUserId == userId, cancellationToken)
            ? id
            : throw new InvalidInputException("That category doesn't exist.");
    }

    private async Task<CalendarDto> ToDtoAsync(Calendar entity, CancellationToken cancellationToken)
    {
        var count = await db.Events.CountAsync(e => e.CalendarId == entity.Id && e.SeriesMasterId == null, cancellationToken);
        return new CalendarDto(entity.Id, entity.Name, count, entity.DefaultCategoryId);
    }

    private async Task<Calendar> CreateEntityAsync(Guid userId, string name, Guid? defaultCategoryId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = new Calendar
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name,
            DefaultCategoryId = defaultCategoryId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Calendars.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
