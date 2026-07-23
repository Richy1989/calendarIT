using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// The starter category set every new account gets at registration. Colors are exact
/// CSS3-named colors so they round-trip losslessly through the iCalendar COLOR property.
/// </summary>
public static class CategoryDefaults
{
    public static readonly IReadOnlyList<(string Name, string Color)> Defaults =
    [
        ("Work", "#6495ED"),      // cornflowerblue
        ("Personal", "#3CB371"),  // mediumseagreen
        ("Family", "#DAA520"),    // goldenrod
        ("Important", "#FF6347"), // tomato
    ];

    /// <summary>Stages the default categories for a user; the caller saves.</summary>
    public static void Seed(AppDbContext db, Guid userId, DateTime now)
    {
        foreach (var (name, color) in Defaults)
        {
            db.Categories.Add(new Category
            {
                Id = Guid.NewGuid(),
                OwnerUserId = userId,
                Name = name,
                Color = color,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }
}
