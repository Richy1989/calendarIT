namespace CalendarIT.Domain;

/// <summary>
/// A single calendar event. Times are stored in UTC (<see cref="StartUtc"/> /
/// <see cref="EndUtc"/>); <see cref="TimeZoneId"/> records the originating zone so
/// recurrence and DST can be computed correctly in later phases. <see cref="Uid"/> is a
/// stable iCalendar identifier used for import/export and CalDAV sync.
/// </summary>
public class CalendarEvent
{
    public Guid Id { get; set; }

    public Guid CalendarId { get; set; }

    /// <summary>Stable iCalendar UID (survives edits, used by iCal/CalDAV).</summary>
    public string Uid { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Location { get; set; }

    /// <summary>Per-event color as hex (serialized to the iCalendar COLOR property, RFC 7986).</summary>
    public string? Color { get; set; }

    /// <summary>UTC start. Stored as <see cref="DateTime"/> (Kind=Utc) for cross-provider
    /// ordering/comparison — SQLite can't sort <c>DateTimeOffset</c> in SQL.</summary>
    public DateTime StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    public bool IsAllDay { get; set; }

    public string? TimeZoneId { get; set; }

    /// <summary>iCalendar RRULE string (e.g. "FREQ=WEEKLY;BYDAY=MO"). Null = single event.</summary>
    public string? RRule { get; set; }

    /// <summary>Excluded occurrence starts (UTC), newline-separated ISO 8601. Maps to EXDATE.</summary>
    public string? ExDates { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Calendar? Calendar { get; set; }

    public ICollection<Reminder> Reminders { get; set; } = new List<Reminder>();
}
