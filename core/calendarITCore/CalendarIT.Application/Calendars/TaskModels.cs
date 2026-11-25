using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Calendars;

/// <summary>A task as the client sees it.</summary>
/// <param name="Due">When it is due: an instant, or — with <paramref name="DueAllDay"/> — a date
/// (midnight UTC, read as that date wherever it is viewed). Null = no date.</param>
/// <param name="Status">NeedsAction, InProcess, Completed or Cancelled.</param>
/// <param name="Priority">"high", "normal", "low", or null for none.</param>
/// <param name="ParentId">The task this is a subtask of.</param>
public sealed record TaskDto(
    Guid Id,
    Guid CalendarId,
    string Title,
    string? Notes,
    DateTimeOffset? Due,
    bool DueAllDay,
    string? TimeZone,
    string Status,
    DateTimeOffset? CompletedAt,
    string? Priority,
    Guid? ParentId,
    int SortOrder,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ReminderDto>? Reminders = null);

/// <summary>Create/edit payload for a task.</summary>
public sealed class SaveTaskRequest
{
    /// <summary>Create only: the id the new task gets, chosen by an offline client (the app) so it
    /// can refer to it before the server has seen it. Null = the server picks one. Ignored on edit.</summary>
    public Guid? Id { get; init; }

    /// <summary>Null on create = the default calendar; on edit = stays where it is.</summary>
    public Guid? CalendarId { get; init; }

    [Required, MaxLength(500)]
    public string Title { get; init; } = string.Empty;

    [MaxLength(8000)]
    public string? Notes { get; init; }

    public DateTimeOffset? Due { get; init; }

    public bool DueAllDay { get; init; }

    /// <summary>The zone a timed due date is in; null keeps the task's own (or none).</summary>
    [MaxLength(64)]
    public string? TimeZone { get; init; }

    /// <summary>"high", "normal", "low", or null for none.</summary>
    [MaxLength(16)]
    public string? Priority { get; init; }

    /// <summary>Makes it a subtask of that task (one level deep); null makes it a top-level task.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Its place among its siblings; null keeps it (or, new, puts it last).</summary>
    public int? SortOrder { get; init; }

    /// <summary>Reminders before it is due; null keeps the task's (none, when new).</summary>
    [MaxLength(SaveEventRequest.MaxReminders)]
    public IReadOnlyList<ReminderInput>? Reminders { get; init; }
}

/// <summary>Ticks a task off (or opens it again).</summary>
public sealed class SetTaskDoneRequest
{
    public bool Done { get; init; }
}

/// <summary>The user's tasks.</summary>
public interface ITaskService
{
    /// <summary>Open tasks — and, with <paramref name="includeDone"/>, done and cancelled ones too.</summary>
    Task<IReadOnlyList<TaskDto>> ListAsync(Guid userId, bool includeDone, CancellationToken cancellationToken = default);

    Task<TaskDto?> GetAsync(Guid userId, Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="InvalidInputException"/> for a parent, calendar or priority that won't do.</summary>
    Task<TaskDto> CreateAsync(Guid userId, SaveTaskRequest request, CancellationToken cancellationToken = default);

    Task<TaskDto?> UpdateAsync(Guid userId, Guid taskId, SaveTaskRequest request, CancellationToken cancellationToken = default);

    /// <summary>Done (with its subtasks) or open again. Null when it isn't the user's.</summary>
    Task<TaskDto?> SetDoneAsync(Guid userId, Guid taskId, bool done, CancellationToken cancellationToken = default);

    /// <summary>Deletes the task and its subtasks.</summary>
    Task<bool> DeleteAsync(Guid userId, Guid taskId, CancellationToken cancellationToken = default);
}
