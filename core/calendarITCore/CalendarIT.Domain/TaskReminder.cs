namespace CalendarIT.Domain;

/// <summary>
/// A reminder on a task: <see cref="MinutesBefore"/> minutes before it is due, via
/// <see cref="Channel"/>. A task due on a date (no time) is reminded of from 09:00 that day in
/// its calendar's zone. Only open tasks remind.
/// </summary>
public class TaskReminder
{
    public Guid Id { get; set; }

    public Guid TaskId { get; set; }

    public int MinutesBefore { get; set; }

    public ReminderChannel Channel { get; set; }

    public TaskItem? Task { get; set; }
}
