using CalendarIT.Application.Notifications;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using MimeKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Reminders on tasks, run by <see cref="ReminderDispatchJob"/> next to the event reminders and
/// read by the polling fallback (<see cref="DueReminderQuery"/>). A reminder counts back from the
/// moment the task is due — for a task due on a date, 09:00 that day in its calendar's zone (a
/// to-do is no midnight appointment). Each (reminder, due) fires once: moving the due date makes
/// it remind again for the new one. Finished tasks don't remind.
/// </summary>
public static class TaskReminders
{
    /// <summary>When a date-only task is "due" for its reminders, in its calendar's zone.</summary>
    public static readonly TimeSpan DateOnlyDueTime = TimeSpan.FromHours(9);

    /// <summary>How far a date-only task's real due moment can lie from its stored midnight UTC.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromDays(1);

    public sealed record Due(TaskReminder Reminder, TaskItem Task, DateTime DueAtUtc);

    /// <summary>The task reminders whose trigger lies in (fromUtc, toUtc], optionally for one
    /// owner and one channel. Tasks and their calendars are loaded.</summary>
    public static async Task<List<Due>> InWindowAsync(
        AppDbContext db, DateTime fromUtc, DateTime toUtc, Guid? ownerUserId, ReminderChannel? channel, CancellationToken cancellationToken)
    {
        var longest = await db.TaskReminders.Select(r => (int?)r.MinutesBefore).MaxAsync(cancellationToken) ?? 0;
        var lower = fromUtc - Slack;
        var upper = toUtc.AddMinutes(longest) + Slack;
        var query = db.TaskReminders
            .Include(r => r.Task!).ThenInclude(t => t.Calendar)
            .Where(r => r.Task!.DueUtc != null && r.Task.DueUtc > lower && r.Task.DueUtc <= upper
                && (r.Task.Status == TaskItemStatus.NeedsAction || r.Task.Status == TaskItemStatus.InProcess));
        if (ownerUserId is { } owner)
        {
            query = query.Where(r => r.Task!.Calendar!.OwnerUserId == owner);
        }
        if (channel is { } ch)
        {
            query = query.Where(r => r.Channel == ch);
        }
        var candidates = await query.ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        var zones = await ZonesAsync(db, candidates.Where(c => c.Task!.DueAllDay).Select(c => c.Task!), cancellationToken);
        var due = new List<Due>();
        foreach (var r in candidates)
        {
            var at = DueAt(r.Task!, zones.GetValueOrDefault(r.Task!.CalendarId));
            var trigger = at.AddMinutes(-r.MinutesBefore);
            if (trigger > fromUtc && trigger <= toUtc)
            {
                due.Add(new Due(r, r.Task!, at));
            }
        }
        return due;
    }

    /// <summary>The moment a task is due: its time, or 09:00 on its date in <paramref name="zoneId"/>.</summary>
    public static DateTime DueAt(TaskItem task, string? zoneId)
    {
        var stored = DateTime.SpecifyKind(task.DueUtc!.Value, DateTimeKind.Utc);
        if (!task.DueAllDay)
        {
            return stored;
        }
        var local = DateTime.SpecifyKind(stored.Date + DateOnlyDueTime, DateTimeKind.Unspecified);
        return TimeZones.TryFind(zoneId, out var zone) ? TimeZones.ToUtc(local, zone) : DateTime.SpecifyKind(local, DateTimeKind.Utc);
    }

    /// <summary>Each calendar's zone for date-only tasks: its own, else that of its latest zoned
    /// event or task (a phone writes those with one), else none (UTC).</summary>
    private static async Task<Dictionary<Guid, string>> ZonesAsync(AppDbContext db, IEnumerable<TaskItem> tasks, CancellationToken cancellationToken)
    {
        var zones = new Dictionary<Guid, string>();
        var needed = new HashSet<Guid>();
        foreach (var t in tasks)
        {
            if (t.Calendar?.TimeZoneId is { } own && TimeZones.TryFind(own, out _))
            {
                zones[t.CalendarId] = own;
            }
            else
            {
                needed.Add(t.CalendarId);
            }
        }
        needed.ExceptWith(zones.Keys);
        if (needed.Count == 0)
        {
            return zones;
        }
        var fromEvents = await db.Events.AsNoTracking()
            .Where(e => needed.Contains(e.CalendarId) && e.TimeZoneId != null)
            .GroupBy(e => e.CalendarId)
            .Select(g => g.OrderByDescending(e => e.UpdatedAt).Select(e => new { e.CalendarId, e.TimeZoneId }).First())
            .ToListAsync(cancellationToken);
        var fromTasks = await db.Tasks.AsNoTracking()
            .Where(t => needed.Contains(t.CalendarId) && t.TimeZoneId != null)
            .GroupBy(t => t.CalendarId)
            .Select(g => g.OrderByDescending(t => t.UpdatedAt).Select(t => new { t.CalendarId, t.TimeZoneId }).First())
            .ToListAsync(cancellationToken);
        foreach (var z in fromEvents.Concat(fromTasks))
        {
            if (!zones.ContainsKey(z.CalendarId) && TimeZones.TryFind(z.TimeZoneId, out _))
            {
                zones[z.CalendarId] = z.TimeZoneId!;
            }
        }
        return zones;
    }

    /// <summary>"is due on Tuesday, 1 December 2026" / "is due at Tuesday, 1 December 2026, 18:00 (Europe/Vienna)".</summary>
    public static string Body(TaskItem task, bool use24Hour) =>
        task.DueAllDay
            ? $"\"{task.Title}\" is due on {ReminderDispatchJob.FormatDate(task.DueUtc!.Value)}."
            : $"\"{task.Title}\" is due at {ReminderDispatchJob.FormatLocal(task.DueUtc!.Value, task.TimeZoneId, use24Hour)}.";

    /// <summary>
    /// Sends the task reminders that came due in (windowStart, now] and logs each, one at a time,
    /// so a failure further on never sends the earlier ones twice. A reminder that can't be sent
    /// is logged as a warning and retried next tick while it is still in the window.
    /// </summary>
    /// <returns>How many were sent (mail among them is queued: the caller pokes the outbox).</returns>
    public static async Task<int> DispatchAsync(
        AppDbContext db, IMailOutbox mail, IWebPushSender push, DateTime windowStart, DateTime now,
        ILogger logger, CancellationToken cancellationToken)
    {
        var due = await InWindowAsync(db, windowStart, now, ownerUserId: null, channel: null, cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }
        var count = 0;
        var ids = due.Select(d => d.Reminder.Id).ToList();
        var sent = (await db.NotificationLogs.Where(n => ids.Contains(n.ReminderId))
                .Select(n => new { n.ReminderId, n.OccurrenceStartUtc })
                .ToListAsync(cancellationToken))
            .Select(n => (n.ReminderId, n.OccurrenceStartUtc))
            .ToHashSet();
        var ownerIds = due.Select(d => d.Task.Calendar!.OwnerUserId).Distinct().ToList();
        var owners = await db.Users.Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.Email, Use24: u.Use24HourClock != false), cancellationToken);
        var subsByOwner = (await db.PushSubscriptions.Where(s => ownerIds.Contains(s.UserId)).ToListAsync(cancellationToken))
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (reminder, task, dueAt) in due)
        {
            // Logged under the stored due: a changed due date is a new one to remind of.
            var key = ReminderOccurrences.Truncate(DateTime.SpecifyKind(task.DueUtc!.Value, DateTimeKind.Utc));
            if (!sent.Add((reminder.Id, key)))
            {
                continue;
            }
            var ownerId = task.Calendar!.OwnerUserId;
            var (email, use24) = owners.GetValueOrDefault(ownerId, (null, true));
            var body = Body(task, use24);
            try
            {
                if (reminder.Channel == ReminderChannel.WebPush)
                {
                    if (push.IsConfigured && subsByOwner.GetValueOrDefault(ownerId) is { Count: > 0 } subs)
                    {
                        var message = new PushMessage($"Task: {task.Title}", body, Url: "/", Tag: $"{reminder.Id}:{key:O}");
                        foreach (var sub in subs)
                        {
                            if (await push.SendAsync(sub, message, cancellationToken) == PushSendResult.Expired)
                            {
                                db.PushSubscriptions.Remove(sub);
                            }
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(email))
                {
                    var message = new MimeMessage();
                    message.To.Add(MailboxAddress.Parse(email));
                    message.Subject = $"Task due: {task.Title}";
                    message.Body = new TextPart("plain") { Text = body };
                    await mail.QueueAsync(ownerId, message, OutboxKind.Reminder, cancellationToken, save: false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Task reminder {ReminderId} for {TaskId} failed to send", reminder.Id, task.Id);
                sent.Remove((reminder.Id, key));
                continue;
            }
            db.NotificationLogs.Add(new NotificationLog { Id = Guid.NewGuid(), ReminderId = reminder.Id, OccurrenceStartUtc = key, SentAtUtc = now });
            await db.SaveChangesAsync(cancellationToken);
            count++;
        }
        return count;
    }
}
