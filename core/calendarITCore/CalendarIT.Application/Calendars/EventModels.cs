using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Calendars;

/// <summary>
/// An event as returned to the client. Times are UTC (ISO 8601 with offset). For a
/// recurring series, the range query returns one DTO per expanded occurrence — all share
/// the master <see cref="Id"/>, carry <see cref="Recurring"/> = true, and the series'
/// <see cref="Recurrence"/> (RRULE). An occurrence that was edited on its own comes back as
/// its own row (its own <see cref="Id"/>), with <see cref="SeriesMasterId"/> naming the series.
/// Either way <see cref="RecurrenceId"/> is the occurrence's original start: the key for editing,
/// deleting or resetting just that occurrence.
/// </summary>
public sealed record EventDto(
    Guid Id,
    Guid CalendarId,
    string Title,
    string? Description,
    string? Location,
    Guid? CategoryId,
    string? Color,
    DateTimeOffset Start,
    DateTimeOffset? End,
    bool AllDay,
    bool Recurring,
    string? Recurrence,
    IReadOnlyList<ReminderDto> Reminders,
    IReadOnlyList<AttendeeDto> Attendees,
    /// <summary>When this event is an invitation the user received (not one they created), the
    /// user's own RSVP status ("NeedsAction"/"Accepted"/…). Null for the user's own events.</summary>
    string? InvitationStatus = null,
    /// <summary>The organizer's email for a received invitation; null for the user's own events.</summary>
    string? OrganizerEmail = null,
    /// <summary>Set when this is an edited occurrence: the series it belongs to.</summary>
    Guid? SeriesMasterId = null,
    /// <summary>For any occurrence of a series (edited or not), the start it has in the series'
    /// rule. Null for one-off events and for a master read on its own.</summary>
    DateTimeOffset? RecurrenceId = null,
    /// <summary>The category the event shows as: its own <see cref="CategoryId"/>, else its
    /// calendar's default category. <see cref="Color"/> already reflects it.</summary>
    Guid? EffectiveCategoryId = null);

/// <summary>A reminder: fire <paramref name="MinutesBefore"/> minutes before start, via <paramref name="Channel"/>.</summary>
public sealed record ReminderDto(int MinutesBefore, string Channel);

/// <summary>A guest on an event. Status mirrors iCalendar PARTSTAT (NeedsAction until they reply).</summary>
public sealed record AttendeeDto(string Email, string? Name, string Status);

/// <summary>
/// A lightweight search hit (title/location match). For a recurring series, <see cref="Start"/>
/// is the next upcoming occurrence, or the most recent past one if none remain.
/// </summary>
public sealed record EventSearchResult(
    Guid Id,
    string Title,
    string? Location,
    string? Color,
    DateTimeOffset Start,
    bool AllDay,
    bool Recurring);

/// <summary>Create/update payload for an event. Used for both POST and PUT.</summary>
public sealed class SaveEventRequest : IValidatableObject
{
    /// <summary>Guests per event. Each one is an email sent through the owner's mailbox and, for a
    /// local user, a row written to their calendar — so the list can't be open-ended.</summary>
    public const int MaxAttendees = 100;

    /// <summary>Reminders per event; each is a delivery per occurrence.</summary>
    public const int MaxReminders = 20;

    [Required, MaxLength(500)]
    public string Title { get; init; } = string.Empty;

    [MaxLength(8000)]
    public string? Description { get; init; }

    [MaxLength(500)]
    public string? Location { get; init; }

    /// <summary>Category supplying the event's color. Null = uncategorized (default color).</summary>
    public Guid? CategoryId { get; init; }

    [Required]
    public DateTimeOffset Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public bool AllDay { get; init; }

    /// <summary>iCalendar RRULE (e.g. "FREQ=WEEKLY"). Null/empty = a single event.</summary>
    [MaxLength(1000)]
    public string? Recurrence { get; init; }

    /// <summary>IANA time zone (e.g. "Europe/Berlin") the event was authored in.</summary>
    [MaxLength(64)]
    public string? TimeZone { get; init; }

    /// <summary>Reminders for this event; replaces the existing set on update.</summary>
    [MaxLength(MaxReminders)]
    public IReadOnlyList<ReminderInput>? Reminders { get; init; }

    /// <summary>
    /// Target calendar. Create: null = the user's default (first) calendar.
    /// Update: null = leave the event where it is; a value moves it there.
    /// </summary>
    public Guid? CalendarId { get; init; }

    /// <summary>Guests to invite; replaces the existing set on update. Null keeps it unchanged.</summary>
    [MaxLength(MaxAttendees)]
    public IReadOnlyList<AttendeeInput>? Attendees { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // All-day ends are the inclusive last day, so equal dates are a one-day event; timed ends
        // may equal the start (a zero-length marker) but never precede it.
        if (End is { } end && end < Start)
        {
            yield return new ValidationResult("The end can't be before the start.", [nameof(End)]);
        }
    }
}

/// <summary>RSVP to a received invitation: the user's new participation status.</summary>
public sealed class RsvpRequest
{
    /// <summary>"Accepted", "Declined", or "Tentative".</summary>
    [Required, MaxLength(16)]
    public string Status { get; init; } = string.Empty;
}

/// <summary>One guest in a save request.</summary>
public sealed class AttendeeInput
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; init; } = string.Empty;

    [MaxLength(200)]
    public string? Name { get; init; }
}

/// <summary>One reminder in a save request.</summary>
public sealed class ReminderInput
{
    [Range(0, 40320)] // up to 4 weeks before
    public int MinutesBefore { get; init; }

    /// <summary>"Email" or "WebPush".</summary>
    [Required, MaxLength(16)]
    public string Channel { get; init; } = "Email";
}
