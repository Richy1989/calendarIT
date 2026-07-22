namespace CalendarIT.Domain;

/// <summary>Participation state of an attendee (iCalendar PARTSTAT).</summary>
public enum AttendeeStatus
{
    NeedsAction,
    Accepted,
    Declined,
    Tentative,
}

/// <summary>
/// A guest invited to an event. Invitations go out over email (iMIP); the status stays
/// <see cref="AttendeeStatus.NeedsAction"/> until inbox scanning (later phase) reads the
/// guest's reply back in.
/// </summary>
public class Attendee
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string? Name { get; set; }

    public AttendeeStatus Status { get; set; }

    public CalendarEvent? Event { get; set; }
}
