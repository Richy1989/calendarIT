namespace CalendarIT.Domain;

/// <summary>How a reminder is delivered. Maps loosely to iCalendar VALARM ACTION.</summary>
public enum ReminderChannel
{
    Email,
    WebPush
}

/// <summary>
/// A reminder attached to an event: fire <see cref="MinutesBefore"/> minutes before each
/// occurrence's start, via <see cref="Channel"/>. Maps to an iCalendar VALARM.
/// </summary>
public class Reminder
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    /// <summary>Lead time in minutes before the occurrence start (e.g. 15).</summary>
    public int MinutesBefore { get; set; }

    public ReminderChannel Channel { get; set; }

    public CalendarEvent? Event { get; set; }
}
