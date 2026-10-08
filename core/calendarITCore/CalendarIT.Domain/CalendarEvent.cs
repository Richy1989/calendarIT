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

    /// <summary>Legacy per-event color as hex. Display color now comes from
    /// <see cref="Category"/>; this remains only as a fallback for uncategorized events
    /// (e.g. synced in while the user had no categories).</summary>
    public string? Color { get; set; }

    /// <summary>The category (named color) this event takes its display color from.
    /// Null = uncategorized (default color).</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>UTC start. Stored as <see cref="DateTime"/> (Kind=Utc) for cross-provider
    /// ordering/comparison — SQLite can't sort <c>DateTimeOffset</c> in SQL.</summary>
    public DateTime StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    public bool IsAllDay { get; set; }

    public string? TimeZoneId { get; set; }

    /// <summary>iCalendar RRULE string (e.g. "FREQ=WEEKLY;BYDAY=MO"). Null = single event.</summary>
    public string? RRule { get; set; }

    /// <summary>
    /// Occurrence starts (UTC) the series' rule produces but that are not shown from the rule,
    /// newline-separated ISO 8601: occurrences the user deleted (EXDATE) <em>and</em> occurrences
    /// replaced by an override row. Keeping both here means every expansion — calendar view,
    /// reminders, search — skips an overridden occurrence without knowing overrides exist; the
    /// iCalendar writer subtracts the override instants again so EXDATE carries only deletions.
    /// </summary>
    public string? ExDates { get; set; }

    /// <summary>
    /// Set on an override row: the series (master) this row replaces one occurrence of. An
    /// override is a plain one-off event otherwise — its own times, title, reminders — sharing the
    /// master's <see cref="Uid"/> and calendar, exactly like a VEVENT with a RECURRENCE-ID.
    /// Null for masters and ordinary events.
    /// </summary>
    public Guid? SeriesMasterId { get; set; }

    /// <summary>On an override row, the UTC start the occurrence had in the series before it was
    /// edited (iCalendar RECURRENCE-ID). Null on everything else.</summary>
    public DateTime? RecurrenceIdUtc { get; set; }

    /// <summary>iCalendar SEQUENCE — bumped whenever an update re-sends invitations, so
    /// guests' calendars know which version of the event is current.</summary>
    public int Sequence { get; set; }

    /// <summary>When this event is an invitation received from someone else (an inbound iMIP
    /// REQUEST landed in the owner's inbox), the owner's own participation status — starts at
    /// <see cref="AttendeeStatus.NeedsAction"/>. Null for events the user created/owns; a
    /// non-null value marks the row as a received invitation and gates inbound updates.</summary>
    public AttendeeStatus? InvitationStatus { get; set; }

    /// <summary>The organizer's email for a received invitation (iMIP ORGANIZER), for showing
    /// "invited by …" and, later, sending an RSVP (REPLY) back. Null for the user's own events.</summary>
    public string? OrganizerEmail { get; set; }

    /// <summary>
    /// Set only on a copy this instance delivered to a local guest, naming the user whose event it
    /// mirrors. Null on everything the owner authored, imported, or synced themselves.
    ///
    /// This is what makes a mirrored copy identifiable as one. Delivery finds copies by shared
    /// <see cref="Uid"/>, and a UID is caller-supplied (an .ics import keeps the file's UID; a
    /// CalDAV PUT takes it from the body), so UID alone let any user address a row on someone
    /// else's calendar. Only a copy stamped with the same organizer may be updated or withdrawn.
    /// </summary>
    public Guid? SourceOrganizerUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Calendar? Calendar { get; set; }

    public Category? Category { get; set; }

    public ICollection<Reminder> Reminders { get; set; } = new List<Reminder>();

    public ICollection<Attendee> Attendees { get; set; } = new List<Attendee>();

    /// <summary>The series this override belongs to (see <see cref="SeriesMasterId"/>).</summary>
    public CalendarEvent? SeriesMaster { get; set; }

    /// <summary>On a master: its edited occurrences. Deleted with the master.</summary>
    public ICollection<CalendarEvent> Overrides { get; set; } = new List<CalendarEvent>();

    /// <summary>True for a row that stands in for one occurrence of a series.</summary>
    public bool IsOverride => SeriesMasterId is not null;
}
