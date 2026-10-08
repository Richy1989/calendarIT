namespace CalendarIT.Domain;

/// <summary>
/// A named collection of events owned by a user. Every user gets at least one default
/// calendar; multiple calendars / sharing come in a later phase.
/// </summary>
public class Calendar
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public string Name { get; set; } = "Personal";

    /// <summary>Hex color for the calendar itself (distinct from per-event color).</summary>
    public string? Color { get; set; }

    /// <summary>Default IANA time zone for events created in this calendar.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// The category events in this calendar take when they have none of their own — so a whole
    /// imported holiday calendar is colored at once, and recolors with it. An event's own category
    /// always wins. Null = no default.
    /// </summary>
    public Guid? DefaultCategoryId { get; set; }

    public Category? DefaultCategory { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<CalendarEvent> Events { get; set; } = new List<CalendarEvent>();
}
