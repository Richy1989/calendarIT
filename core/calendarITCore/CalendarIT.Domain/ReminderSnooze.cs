namespace CalendarIT.Domain;

/// <summary>
/// A browser reminder the user snoozed: shown again at <see cref="DueAtUtc"/>. One per reminder
/// occurrence — snoozing it again moves the time. Deleted with its reminder.
/// </summary>
public class ReminderSnooze
{
    public Guid Id { get; set; }

    public Guid ReminderId { get; set; }

    /// <summary>The occurrence it reminds of — the same key the reminder was logged under.</summary>
    public DateTime OccurrenceStartUtc { get; set; }

    public DateTime DueAtUtc { get; set; }

    /// <summary>When the reminder job pushed it again; null until then.</summary>
    public DateTime? SentAtUtc { get; set; }

    public Reminder? Reminder { get; set; }
}
