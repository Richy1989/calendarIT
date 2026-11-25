using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using ICalCalendar = Ical.Net.Calendar;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Tasks as iCalendar VTODOs (RFC 5545 §3.6.2), both ways. What arrives from a phone is outside
/// input like any VEVENT: text clipped to the column widths, dates read through the same zone
/// handling as events, a repeat rule kept only when it is one we'd store.
/// </summary>
public static class TaskICalMapper
{
    public static Todo ToTodo(TaskItem t)
    {
        var vt = new Todo
        {
            Uid = t.Uid,
            Summary = t.Title,
            Description = t.Notes,
            Status = t.Status switch
            {
                TaskItemStatus.InProcess => "IN-PROCESS",
                TaskItemStatus.Completed => "COMPLETED",
                TaskItemStatus.Cancelled => "CANCELLED",
                _ => "NEEDS-ACTION",
            },
            Priority = t.Priority,
            Sequence = t.Sequence,
            DtStamp = new CalDateTime(DateTime.SpecifyKind(t.UpdatedAt, DateTimeKind.Utc)),
            Created = new CalDateTime(DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)),
            LastModified = new CalDateTime(DateTime.SpecifyKind(t.UpdatedAt, DateTimeKind.Utc)),
        };
        if (t.DueUtc is { } due)
        {
            vt.Due = DueValue(due, t.DueAllDay, t.TimeZoneId);
        }
        if (t.Status == TaskItemStatus.Completed)
        {
            vt.Completed = new CalDateTime(DateTime.SpecifyKind(t.CompletedAtUtc ?? t.UpdatedAt, DateTimeKind.Utc));
            vt.PercentComplete = 100;
        }
        if (t.RelatedToUid is { Length: > 0 } parentUid)
        {
            vt.RelatedComponents.Add(parentUid);
        }
        if (t.RRule is { Length: > 0 } rule)
        {
            try
            {
                vt.RecurrenceRule = new RecurrenceRule(rule);
            }
            catch (Exception)
            {
                // a rule that no longer parses is left out rather than breaking the resource
            }
        }
        return vt;
    }

    /// <summary>The resource a CalDAV client gets: one VCALENDAR holding the VTODO.</summary>
    public static string Serialize(TaskItem t)
    {
        var cal = new ICalCalendar { ProductId = "-//CalendarIT//EN" };
        cal.Todos.Add(ToTodo(t));
        return new CalendarSerializer().SerializeToString(cal)!;
    }

    private static CalDateTime DueValue(DateTime utc, bool allDay, string? zone)
    {
        if (allDay)
        {
            return new CalDateTime(DateOnly.FromDateTime(utc));
        }
        if (TimeZones.Canonical(zone) is { } zoneId && TimeZones.TryFind(zoneId, out var tz))
        {
            return new CalDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz), zoneId);
        }
        return new CalDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    /// <summary>Copies an incoming VTODO onto <paramref name="t"/> (all but calendar, UID and parent).</summary>
    public static void Apply(Todo vt, TaskItem t, DateTime now)
    {
        var zones = new ICalEventMapper.IncomingZones(vt);
        t.Title = string.IsNullOrWhiteSpace(vt.Summary) ? "(untitled)" : ICalEventMapper.Clip(vt.Summary.Trim(), 500)!;
        t.Notes = ICalEventMapper.Clip(string.IsNullOrWhiteSpace(vt.Description) ? null : vt.Description, 8000);

        if (vt.Due is { } due)
        {
            t.DueAllDay = !due.HasTime;
            t.DueUtc = t.DueAllDay
                ? DateTime.SpecifyKind(due.Value.Date, DateTimeKind.Utc)
                : zones.ToUtc(due);
            t.TimeZoneId = t.DueAllDay ? null : zones.ZoneId(due);
        }
        else
        {
            t.DueUtc = null;
            t.DueAllDay = false;
            t.TimeZoneId = null;
        }

        t.Status = vt.Status?.Trim().ToUpperInvariant() switch
        {
            "COMPLETED" => TaskItemStatus.Completed,
            "IN-PROCESS" => TaskItemStatus.InProcess,
            "CANCELLED" => TaskItemStatus.Cancelled,
            // Some clients only set COMPLETED (or 100%) and leave STATUS out.
            _ when vt.Completed is not null || vt.PercentComplete >= 100 => TaskItemStatus.Completed,
            _ => TaskItemStatus.NeedsAction,
        };
        t.CompletedAtUtc = t.Status == TaskItemStatus.Completed
            ? (vt.Completed is { } done ? zones.ToUtc(done) : t.CompletedAtUtc ?? now)
            : null;
        t.Priority = Math.Clamp(vt.Priority, 0, 9);
        t.RRule = RecurrenceRules.FromExternal(vt.RecurrenceRule);
        t.Sequence = Math.Max(t.Sequence, vt.Sequence);
        var related = vt.RelatedComponents.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))?.Trim();
        t.RelatedToUid = related is null || related.Length > ICalEventMapper.MaxUidLength || related == t.Uid ? null : related;
        t.UpdatedAt = now;
    }
}

/// <summary>
/// Writes a VTODO a CalDAV client PUT into a calendar, and keeps subtasks linked: RELATED-TO
/// names the parent by UID, and the two may arrive in either order. Trees deeper than one level
/// are flattened onto the top task. Changes are staged, not saved.
/// </summary>
public sealed class TaskWriter(AppDbContext db)
{
    public async Task<TaskItem> WriteAsync(
        TaskItem? existing, Todo vt, Guid calendarId, string uid, DateTime now, CancellationToken cancellationToken)
    {
        var task = existing ?? new TaskItem { Id = Guid.NewGuid(), CalendarId = calendarId, Uid = uid, CreatedAt = now };
        TaskICalMapper.Apply(vt, task, now);
        if (existing is null)
        {
            db.Tasks.Add(task);
        }

        var hasSubtasks = existing is not null && await db.Tasks.AnyAsync(t => t.ParentId == existing.Id, cancellationToken);
        task.ParentId = null;
        if (task.RelatedToUid is { } parentUid && !hasSubtasks)
        {
            var parent = await db.Tasks.FirstOrDefaultAsync(t => t.CalendarId == calendarId && t.Uid == parentUid, cancellationToken);
            // One level deep: a subtask of a subtask hangs off the top task instead — unless
            // that is this task itself (a loop), which leaves it on its own.
            var top = parent is null ? (Guid?)null : parent.ParentId ?? parent.Id;
            if (top is { } topId && topId != task.Id)
            {
                task.ParentId = topId;
            }
        }

        // Subtasks that arrived before this task: link them now.
        var waiting = await db.Tasks
            .Where(t => t.CalendarId == calendarId && t.RelatedToUid == uid && t.ParentId == null && t.Id != task.Id)
            .ToListAsync(cancellationToken);
        foreach (var child in waiting)
        {
            if (!await db.Tasks.AnyAsync(t => t.ParentId == child.Id, cancellationToken))
            {
                child.ParentId = task.ParentId ?? task.Id;
            }
        }
        return task;
    }
}
