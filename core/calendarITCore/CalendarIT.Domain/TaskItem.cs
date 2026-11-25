namespace CalendarIT.Domain;

/// <summary>Where a task stands. Maps to iCalendar VTODO STATUS.</summary>
public enum TaskItemStatus
{
    NeedsAction,
    InProcess,
    Completed,
    Cancelled,
}

/// <summary>
/// A to-do in one of the user's calendars — synced as a VTODO next to the calendar's events.
/// A due date is a date (stored at midnight UTC, like an all-day event) or an instant; a task may
/// have none. Subtasks are one level deep: a task with a <see cref="ParentId"/> has no subtasks
/// of its own.
/// </summary>
public class TaskItem
{
    public Guid Id { get; set; }

    public Guid CalendarId { get; set; }

    /// <summary>iCalendar UID — unique per calendar across tasks and events (one CalDAV resource).</summary>
    public string Uid { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Notes { get; set; }

    /// <summary>When it is due; for <see cref="DueAllDay"/> the date's midnight UTC. Null = no date.</summary>
    public DateTime? DueUtc { get; set; }

    public bool DueAllDay { get; set; }

    /// <summary>The zone a timed due date was set in (shown and synced in it).</summary>
    public string? TimeZoneId { get; set; }

    public TaskItemStatus Status { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>iCalendar PRIORITY: 0 none, 1 high, 5 normal, 9 low (other values from phones are kept).</summary>
    public int Priority { get; set; }

    /// <summary>The task this is a subtask of, or null.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>
    /// The UID its RELATED-TO names, as written — kept even while that task isn't here (a phone
    /// may send a subtask before its parent), so the two are linked once it arrives.
    /// </summary>
    public string? RelatedToUid { get; set; }

    /// <summary>Order among its siblings in the web list (iCalendar X-APPLE-SORT-ORDER-like).</summary>
    public int SortOrder { get; set; }

    /// <summary>A repeat rule a phone sent. Kept and sent back so it isn't lost, but not acted on:
    /// tasks don't repeat here (yet).</summary>
    public string? RRule { get; set; }

    /// <summary>Bumped on every change: the guests/phones' SEQUENCE.</summary>
    public int Sequence { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Calendar? Calendar { get; set; }

    public TaskItem? Parent { get; set; }

    public ICollection<TaskItem> Subtasks { get; set; } = new List<TaskItem>();

    public ICollection<TaskReminder> Reminders { get; set; } = new List<TaskReminder>();
}
