using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>EF Core-backed <see cref="ICategoryService"/>. All queries are scoped by owner.</summary>
public sealed class CategoryService(AppDbContext db, TimeProvider timeProvider) : ICategoryService
{
    public async Task<IReadOnlyList<CategoryDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
        => await db.Categories.AsNoTracking()
            .Where(c => c.OwnerUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Color, c.Events.Count))
            .ToListAsync(cancellationToken);

    public async Task<CategorySaveOutcome> CreateAsync(Guid userId, SaveCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        if (await NameTakenAsync(userId, name, excludeId: null, cancellationToken))
        {
            return new CategorySaveOutcome(CategorySaveStatus.DuplicateName, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = new Category
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Name = name,
            Color = request.Color,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Categories.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new CategorySaveOutcome(CategorySaveStatus.Saved, new CategoryDto(entity.Id, entity.Name, entity.Color, 0));
    }

    public async Task<CategorySaveOutcome> UpdateAsync(Guid userId, Guid categoryId, SaveCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await db.Categories
            .SingleOrDefaultAsync(c => c.Id == categoryId && c.OwnerUserId == userId, cancellationToken);
        if (entity is null)
        {
            return new CategorySaveOutcome(CategorySaveStatus.NotFound, null);
        }

        var name = request.Name.Trim();
        if (await NameTakenAsync(userId, name, excludeId: categoryId, cancellationToken))
        {
            return new CategorySaveOutcome(CategorySaveStatus.DuplicateName, null);
        }

        entity.Name = name;
        entity.Color = request.Color;
        entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        var count = await db.Events.CountAsync(e => e.CategoryId == entity.Id, cancellationToken);
        return new CategorySaveOutcome(CategorySaveStatus.Saved, new CategoryDto(entity.Id, entity.Name, entity.Color, count));
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken = default)
    {
        var entity = await db.Categories
            .SingleOrDefaultAsync(c => c.Id == categoryId && c.OwnerUserId == userId, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        db.Categories.Remove(entity); // events fall back to uncategorized (FK is SetNull)
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Case-insensitive: ToLower() == translates to SQL on both SQLite and Npgsql.
    private async Task<bool> NameTakenAsync(Guid userId, string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var needle = name.ToLowerInvariant();
        var query = db.Categories.Where(c => c.OwnerUserId == userId && c.Name.ToLower() == needle);
        if (excludeId is { } id)
        {
            query = query.Where(c => c.Id != id);
        }
        return await query.AnyAsync(cancellationToken);
    }
}
