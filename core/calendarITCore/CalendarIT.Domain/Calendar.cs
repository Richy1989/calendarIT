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

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<CalendarEvent> Events { get; set; } = new List<CalendarEvent>();
}
