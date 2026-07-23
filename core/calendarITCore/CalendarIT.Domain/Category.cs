namespace CalendarIT.Domain;

/// <summary>
/// A user-defined event category: a named color. Events reference a category and take
/// their display color from it, so recoloring a category in Settings recolors all of its
/// events at once. Serialized to iCalendar as CATEGORIES (the name, RFC 5545) plus COLOR
/// (the nearest CSS3 color name, RFC 7986).
/// </summary>
public class Category
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Hex color (e.g. "#7B68EE").</summary>
    public string Color { get; set; } = "#7B68EE";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<CalendarEvent> Events { get; set; } = new List<CalendarEvent>();
}
