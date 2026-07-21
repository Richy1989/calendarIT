namespace CalendarIT.Domain;

/// <summary>
/// Records that a reminder was dispatched for a specific occurrence, so the background
/// job never sends the same reminder twice. Unique on (ReminderId, OccurrenceStartUtc).
/// </summary>
public class NotificationLog
{
    public Guid Id { get; set; }

    public Guid ReminderId { get; set; }

    /// <summary>UTC start of the occurrence this reminder was sent for.</summary>
    public DateTime OccurrenceStartUtc { get; set; }

    public DateTime SentAtUtc { get; set; }
}
