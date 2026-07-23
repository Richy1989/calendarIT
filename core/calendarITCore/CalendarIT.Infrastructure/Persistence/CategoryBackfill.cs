using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CalendarIT.Infrastructure.Persistence;

/// <summary>
/// One-time data upgrade for the categories feature: each distinct per-event color
/// becomes a category for that user (named after its nearest CSS3 color name), and every
/// colored event is assigned to it — so nothing changes visually. Idempotent: events that
/// already carry a category are skipped, making later startups a no-op.
/// </summary>
public static class CategoryBackfill
{
    public static async Task BackfillCategoriesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(CategoryBackfill));

        var pending = await db.Events
            .Where(e => e.CategoryId == null && e.Color != null)
            .Select(e => new { Event = e, e.Calendar!.OwnerUserId })
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var owners = pending.Select(p => p.OwnerUserId).Distinct().ToList();
        var categoriesByOwner = (await db.Categories
                .Where(c => owners.Contains(c.OwnerUserId))
                .ToListAsync(cancellationToken))
            .GroupBy(c => c.OwnerUserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var created = 0;
        foreach (var group in pending.GroupBy(p => p.OwnerUserId))
        {
            var categories = categoriesByOwner.TryGetValue(group.Key, out var list) ? list : [];
            foreach (var p in group)
            {
                var hex = p.Event.Color!;
                var category = categories.FirstOrDefault(
                    c => string.Equals(c.Color, hex, StringComparison.OrdinalIgnoreCase));
                if (category is null)
                {
                    category = new Category
                    {
                        Id = Guid.NewGuid(),
                        OwnerUserId = group.Key,
                        Name = UniqueName(CssColorMap.ToNearestName(hex) ?? "color", categories),
                        Color = hex,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    db.Categories.Add(category);
                    categories.Add(category);
                    created++;
                }
                p.Event.CategoryId = category.Id;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Category backfill: assigned {EventCount} events, created {CategoryCount} categories",
            pending.Count, created);
    }

    /// <summary>"mediumslateblue" → "Mediumslateblue", suffixed if two hexes snap to the same name.</summary>
    private static string UniqueName(string cssName, List<Category> existing)
    {
        var baseName = char.ToUpperInvariant(cssName[0]) + cssName[1..];
        var name = baseName;
        for (var i = 2; existing.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{baseName} {i}";
        }
        return name;
    }
}
