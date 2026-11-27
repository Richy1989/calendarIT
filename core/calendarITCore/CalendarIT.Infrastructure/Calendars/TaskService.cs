using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// EF Core-backed <see cref="ITaskService"/>. Tasks live in the user's own calendars (never a
/// subscription) and sync to phones as VTODOs; subtasks are one level deep and always share their
/// parent's calendar, so a task and its subtasks travel together.
/// </summary>
public sealed class TaskService(AppDbContext db, TimeProvider timeProvider, QuotaOptions? quotaOptions = null) : ITaskService
{
    private readonly QuotaOptions quotas = quotaOptions ?? QuotaOptions.Default;

    /// <summary>Done tasks listed at most — the oldest drop off the list (they stay stored).</summary>
    private const int MaxDoneListed = 500;

    public async Task<IReadOnlyList<TaskDto>> ListAsync(Guid userId, bool includeDone, CancellationToken cancellationToken = default)
    {
        var own = db.Tasks.AsNoTracking().Include(t => t.Reminders).Where(t => t.Calendar!.OwnerUserId == userId);
        var open = await own
            .Where(t => t.Status == TaskItemStatus.NeedsAction || t.Status == TaskItemStatus.InProcess)
            .ToListAsync(cancellationToken);
        if (includeDone)
        {
            open.AddRange(await own
                .Where(t => t.Status == TaskItemStatus.Completed || t.Status == TaskItemStatus.Cancelled)
                .OrderByDescending(t => t.CompletedAtUtc ?? t.UpdatedAt)
                .Take(MaxDoneListed)
                .ToListAsync(cancellationToken));
        }
        return open.OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).Select(ToDto).ToList();
    }

    public async Task<TaskDto?> GetAsync(Guid userId, Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.AsNoTracking().Include(t => t.Reminders)
            .SingleOrDefaultAsync(t => t.Id == taskId && t.Calendar!.OwnerUserId == userId, cancellationToken);
        return task is null ? null : ToDto(task);
    }

    public async Task<TaskDto> CreateAsync(Guid userId, SaveTaskRequest request, CancellationToken cancellationToken = default)
    {
        await Quotas.EnsureTasksAsync(db, quotas, userId, 1, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var task = new TaskItem
        {
            Id = await ClientIds.ResolveAsync(db.Tasks, id => t => t.Id == id, request.Id, cancellationToken),
            Uid = $"{Guid.NewGuid():N}@calendarit",
            Status = TaskItemStatus.NeedsAction,
            CreatedAt = now,
        };
        await ApplyAsync(userId, task, request, now, isNew: true, cancellationToken);
        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(task);
    }

    public async Task<TaskDto?> UpdateAsync(Guid userId, Guid taskId, SaveTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = await OwnTaskAsync(userId, taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }
        await EnsureWritableAsync(task.CalendarId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await ApplyAsync(userId, task, request, now, isNew: false, cancellationToken);
        task.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(task);
    }

    public async Task<TaskDto?> SetDoneAsync(Guid userId, Guid taskId, bool done, CancellationToken cancellationToken = default)
    {
        var task = await OwnTaskAsync(userId, taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }
        await EnsureWritableAsync(task.CalendarId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        SetDone(task, done, now);
        // Ticking off a task ticks off what is left of its checklist; opening it again leaves the
        // subtasks as they are.
        if (done)
        {
            foreach (var sub in task.Subtasks.Where(s => s.Status is TaskItemStatus.NeedsAction or TaskItemStatus.InProcess))
            {
                SetDone(sub, true, now);
            }
        }
        // A subtask opened again re-opens its finished parent: the job isn't done after all.
        else if (task.Parent is { Status: TaskItemStatus.Completed } parent)
        {
            SetDone(parent, false, now);
        }
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(task);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await OwnTaskAsync(userId, taskId, cancellationToken);
        if (task is null)
        {
            return false;
        }
        await EnsureWritableAsync(task.CalendarId, cancellationToken);
        db.Tasks.RemoveRange(task.Subtasks);
        db.Tasks.Remove(task);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private Task<TaskItem?> OwnTaskAsync(Guid userId, Guid taskId, CancellationToken cancellationToken) =>
        db.Tasks
            .Include(t => t.Subtasks)
            .Include(t => t.Parent)
            .Include(t => t.Reminders)
            .SingleOrDefaultAsync(t => t.Id == taskId && t.Calendar!.OwnerUserId == userId, cancellationToken);

    private static void SetDone(TaskItem task, bool done, DateTime now)
    {
        task.Status = done ? TaskItemStatus.Completed : TaskItemStatus.NeedsAction;
        task.CompletedAtUtc = done ? now : null;
        task.Sequence++;
        task.UpdatedAt = now;
    }

    /// <summary>Copies the request onto the task, after checking calendar, parent and priority.</summary>
    private async Task ApplyAsync(Guid userId, TaskItem task, SaveTaskRequest request, DateTime now, bool isNew, CancellationToken cancellationToken)
    {
        var priority = ParsePriority(request.Priority);

        // The parent decides the calendar: a subtask lives with its task.
        TaskItem? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await db.Tasks.SingleOrDefaultAsync(t => t.Id == parentId && t.Calendar!.OwnerUserId == userId, cancellationToken)
                ?? throw new InvalidInputException("That task doesn't exist.");
            if (parent.Id == task.Id || parent.ParentId is not null)
            {
                throw new InvalidInputException("Subtasks go one level deep: a subtask can't have subtasks of its own.");
            }
            if (!isNew && task.Subtasks.Count > 0)
            {
                throw new InvalidInputException("A task with subtasks can't become a subtask itself.");
            }
        }

        Guid calendarId;
        if (parent is not null)
        {
            calendarId = parent.CalendarId;
        }
        else if (request.CalendarId is { } requested && (isNew || requested != task.CalendarId))
        {
            calendarId = await db.Calendars.AnyAsync(c => c.Id == requested && c.OwnerUserId == userId, cancellationToken)
                ? requested
                : throw new InvalidInputException("That calendar doesn't exist.");
        }
        else
        {
            calendarId = isNew ? (await DefaultCalendar.GetOrCreateAsync(db, timeProvider, userId, cancellationToken)).Id : task.CalendarId;
        }
        await EnsureWritableAsync(calendarId, cancellationToken);

        task.Title = request.Title.Trim();
        task.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes;
        task.Priority = priority;
        task.ParentId = parent?.Id;
        task.RelatedToUid = parent?.Uid;
        task.CalendarId = calendarId;
        // Subtasks follow their task into another calendar.
        foreach (var sub in task.Subtasks)
        {
            sub.CalendarId = calendarId;
        }

        if (request.Due is { } due)
        {
            task.DueAllDay = request.DueAllDay;
            // A date is a date: stored at its midnight UTC, the day the caller named.
            task.DueUtc = request.DueAllDay ? DateTime.SpecifyKind(due.Date, DateTimeKind.Utc) : due.UtcDateTime;
            task.TimeZoneId = request.DueAllDay ? null : TimeZones.Normalize(request.TimeZone) ?? task.TimeZoneId;
        }
        else
        {
            task.DueUtc = null;
            task.DueAllDay = false;
            task.TimeZoneId = null;
        }

        if (request.Reminders is { } reminders)
        {
            foreach (var old in task.Reminders.ToList())
            {
                task.Reminders.Remove(old);
                if (!isNew)
                {
                    db.TaskReminders.Remove(old);
                }
            }
            foreach (var r in reminders.DistinctBy(r => (r.MinutesBefore, r.Channel.ToLowerInvariant())))
            {
                var reminder = new TaskReminder
                {
                    Id = Guid.NewGuid(),
                    TaskId = task.Id,
                    MinutesBefore = r.MinutesBefore,
                    Channel = Enum.TryParse<ReminderChannel>(r.Channel, ignoreCase: true, out var c) && Enum.IsDefined(c)
                        && !int.TryParse(r.Channel, out _)
                        ? c
                        : throw new InvalidInputException("A reminder goes by Email or WebPush."),
                };
                if (!isNew)
                {
                    db.Entry(reminder).State = EntityState.Added; // a preset key: insert, not update
                }
                EventService.AddOnce(task.Reminders, reminder); // tracking it may already have linked it
            }
        }

        if (request.SortOrder is { } order)
        {
            task.SortOrder = order;
        }
        else if (isNew)
        {
            var last = await db.Tasks
                .Where(t => t.CalendarId == calendarId && t.ParentId == task.ParentId)
                .MaxAsync(t => (int?)t.SortOrder, cancellationToken);
            task.SortOrder = (last ?? 0) + 1;
        }
        task.UpdatedAt = now;
    }

    /// <summary>A subscription is a copy of someone else's feed: nothing is written there.</summary>
    private async Task EnsureWritableAsync(Guid calendarId, CancellationToken cancellationToken)
    {
        if (await db.Calendars.AnyAsync(c => c.Id == calendarId && c.SourceUrl != null, cancellationToken))
        {
            throw new InvalidInputException(EventService.SubscribedCalendarMessage);
        }
    }

    public static int ParsePriority(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" => 0,
        "high" => 1,
        "normal" => 5,
        "low" => 9,
        _ => throw new InvalidInputException("Priority is high, normal, low or none."),
    };

    /// <summary>iCalendar PRIORITY 1–4 is high, 5 normal, 6–9 low (RFC 5545 §3.8.1.9).</summary>
    public static string? PriorityName(int priority) => priority switch
    {
        >= 1 and <= 4 => "high",
        5 => "normal",
        >= 6 and <= 9 => "low",
        _ => null,
    };

    public static TaskDto ToDto(TaskItem t) => new(
        t.Id,
        t.CalendarId,
        t.Title,
        t.Notes,
        t.DueUtc is { } due ? new DateTimeOffset(DateTime.SpecifyKind(due, DateTimeKind.Utc)) : null,
        t.DueAllDay,
        t.TimeZoneId,
        t.Status.ToString(),
        t.CompletedAtUtc is { } done ? new DateTimeOffset(DateTime.SpecifyKind(done, DateTimeKind.Utc)) : null,
        PriorityName(t.Priority),
        t.ParentId,
        t.SortOrder,
        new DateTimeOffset(DateTime.SpecifyKind(t.UpdatedAt, DateTimeKind.Utc)),
        t.Reminders.OrderBy(r => r.MinutesBefore).Select(r => new ReminderDto(r.MinutesBefore, r.Channel.ToString())).ToList());
}
