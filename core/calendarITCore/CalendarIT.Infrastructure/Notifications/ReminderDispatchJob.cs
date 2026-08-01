using CalendarIT.Application.Notifications;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using MimeKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Runs every minute: finds reminders whose trigger time (occurrence start minus lead)
/// falls in the last ~minute, and dispatches each once (idempotent via NotificationLog).
/// Recurrence is expanded per reminder so repeating events fire on every occurrence.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ReminderDispatchJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ReminderDispatchJob> logger) : IJob
{
    // Look back slightly further than the 1-minute cadence so a slow tick never drops a trigger.
    private static readonly TimeSpan Lookback = TimeSpan.FromSeconds(90);

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await RunAsync(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IUserMailSender>(),
            scope.ServiceProvider.GetRequiredService<IWebPushSender>(),
            context.CancellationToken);
    }

    /// <summary>
    /// One dispatch pass, against an already-resolved scope. Separate from <see cref="Execute"/>
    /// so the window arithmetic and the dedup can be tested without standing up Quartz.
    /// </summary>
    public async Task RunAsync(AppDbContext db, IUserMailSender mail, IWebPushSender push, CancellationToken cancellationToken = default)
    {
        var now = Truncate(timeProvider.GetUtcNow().UtcDateTime);
        var windowStart = now - Lookback;

        // Only reminders that could possibly be due are read. A one-off event fires at
        // Start - offset, so for its trigger to land in (windowStart, now] its start must lie in
        // (windowStart, now + longest offset] — a bounded range the database can filter on,
        // instead of reading every reminder ever created once a minute. Recurring masters have
        // to come along regardless: their occurrences aren't in the row.
        var longestOffset = await db.Reminders
            .Select(r => (int?)r.MinutesBefore)
            .MaxAsync(cancellationToken) ?? 0;
        var horizon = now.AddMinutes(longestOffset);

        var reminders = await db.Reminders
            .Include(r => r.Event!).ThenInclude(e => e.Calendar)
            .Where(r => r.Event!.RRule != null
                || (r.Event!.StartUtc > windowStart && r.Event!.StartUtc <= horizon))
            .ToListAsync(cancellationToken);
        if (reminders.Count == 0)
        {
            return;
        }

        // Which (reminder, occurrence) pairs actually come due this tick.
        var due = new List<(Reminder Reminder, DateTime OccurrenceStartUtc)>();
        foreach (var reminder in reminders)
        {
            var offset = TimeSpan.FromMinutes(reminder.MinutesBefore);
            // trigger in (windowStart, now]  ⇔  occurrence start in (windowStart+offset, now+offset]
            foreach (var occStart in OccurrencesInWindow(reminder.Event!, windowStart + offset, now + offset))
            {
                due.Add((reminder, occStart));
            }
        }
        if (due.Count == 0)
        {
            return;
        }

        // One query settles what was already sent, rather than a round trip per occurrence.
        var dueIds = due.Select(d => d.Reminder.Id).Distinct().ToList();
        var dueStarts = due.Select(d => d.OccurrenceStartUtc).Distinct().ToList();
        var sent = (await db.NotificationLogs
                .Where(n => dueIds.Contains(n.ReminderId) && dueStarts.Contains(n.OccurrenceStartUtc))
                .Select(n => new { n.ReminderId, n.OccurrenceStartUtc })
                .ToListAsync(cancellationToken))
            .Select(n => (n.ReminderId, n.OccurrenceStartUtc))
            .ToHashSet();

        var ownerIds = due.Select(d => d.Reminder.Event!.Calendar!.OwnerUserId).Distinct().ToList();
        var emailByOwner = await db.Users
            .Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        // Browser push targets, grouped by owner: one owner may have several (one per browser).
        var subsByOwner = (await db.PushSubscriptions
                .Where(s => ownerIds.Contains(s.UserId))
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (reminder, occStart) in due)
        {
            if (!sent.Add((reminder.Id, occStart)))
            {
                continue; // already delivered — or a duplicate within this same batch
            }

            var ev = reminder.Event!;
            var ownerUserId = ev.Calendar!.OwnerUserId;
            await DispatchAsync(
                reminder, ev, occStart, ownerUserId, emailByOwner.GetValueOrDefault(ownerUserId),
                subsByOwner.GetValueOrDefault(ownerUserId), mail, push, db, cancellationToken);

            // Recorded one at a time, right after sending: a failure later in the batch must not
            // roll back the record of what already went out, or those reminders send twice.
            db.NotificationLogs.Add(new NotificationLog
            {
                Id = Guid.NewGuid(),
                ReminderId = reminder.Id,
                OccurrenceStartUtc = occStart,
                SentAtUtc = now,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static IEnumerable<DateTime> OccurrencesInWindow(CalendarEvent ev, DateTime occFrom, DateTime occTo)
    {
        if (ev.RRule is null)
        {
            if (ev.StartUtc > occFrom && ev.StartUtc <= occTo)
            {
                yield return Truncate(ev.StartUtc);
            }
            yield break;
        }

        var end = ev.EndUtc ?? ev.StartUtc.AddHours(1);
        var exDates = RecurrenceExpander.ParseExDates(ev.ExDates);
        foreach (var occ in RecurrenceExpander.Expand(ev.StartUtc, end, ev.TimeZoneId, ev.RRule, exDates, occFrom, occTo.AddSeconds(1)))
        {
            if (occ.StartUtc > occFrom && occ.StartUtc <= occTo)
            {
                yield return Truncate(occ.StartUtc);
            }
        }
    }

    private async Task DispatchAsync(
        Reminder reminder, CalendarEvent ev, DateTime occStartUtc, Guid ownerUserId, string? ownerEmail,
        List<PushSubscription>? subscriptions, IUserMailSender mail, IWebPushSender push, AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (reminder.Channel == ReminderChannel.WebPush)
        {
            await DispatchWebPushAsync(reminder, ev, occStartUtc, subscriptions, push, db, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(ownerEmail))
        {
            logger.LogWarning("No email on file for event {EventId}; skipping reminder", ev.Id);
            return;
        }

        var localStart = FormatLocal(occStartUtc, ev.TimeZoneId);
        var subject = $"Reminder: {ev.Title}";
        var body = $"\"{ev.Title}\" starts at {localStart}." +
                   (string.IsNullOrWhiteSpace(ev.Location) ? "" : $"\nLocation: {ev.Location}");

        var message = new MimeMessage();
        message.To.Add(MailboxAddress.Parse(ownerEmail));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        // Sent through the owner's own connected mail account (From uses their FromAddress
        // override when set). Without a configured account there's nothing to send from, so the
        // reminder is logged instead — the same dev-friendly fallback the global SMTP path had.
        var sent = await mail.TrySendAsync(ownerUserId, message, cancellationToken);
        if (sent)
        {
            logger.LogInformation("Sent {Channel} reminder for {EventId} @ {Occurrence:o} to {Email}",
                reminder.Channel, ev.Id, occStartUtc, ownerEmail);
        }
        else
        {
            logger.LogInformation(
                "[reminder:no-mail-account] user {UserId} has no connected mail account; reminder for {EventId} not sent. To={Email} Subject={Subject}",
                ownerUserId, ev.Id, ownerEmail, subject);
        }
    }

    private async Task DispatchWebPushAsync(
        Reminder reminder, CalendarEvent ev, DateTime occStartUtc, List<PushSubscription>? subscriptions,
        IWebPushSender push, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!push.IsConfigured)
        {
            logger.LogInformation(
                "[reminder:no-vapid] Web Push reminder for {EventId} not sent: VAPID keys are not configured", ev.Id);
            return;
        }

        if (subscriptions is not { Count: > 0 })
        {
            logger.LogInformation(
                "[reminder:no-push-subscription] no browser subscriptions for event {EventId}; reminder not sent", ev.Id);
            return;
        }

        var localStart = FormatLocal(occStartUtc, ev.TimeZoneId);
        var body = $"Starts at {localStart}." +
                   (string.IsNullOrWhiteSpace(ev.Location) ? "" : $"\nLocation: {ev.Location}");
        // Same tag for every browser + this occurrence, so a second device doesn't stack a duplicate.
        var message = new PushMessage($"Reminder: {ev.Title}", body, Url: "/", Tag: $"{reminder.Id}:{occStartUtc:O}");

        foreach (var sub in subscriptions)
        {
            var result = await push.SendAsync(sub, message, cancellationToken);
            switch (result)
            {
                case PushSendResult.Sent:
                    logger.LogInformation("Sent WebPush reminder for {EventId} @ {Occurrence:o}", ev.Id, occStartUtc);
                    break;
                case PushSendResult.Expired:
                    // Gone for good — drop it so we stop trying. Persisted with the NotificationLog write.
                    db.PushSubscriptions.Remove(sub);
                    break;
            }
        }
    }

    private static string FormatLocal(DateTime utc, string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
                return $"{local:f} ({timeZoneId})";
            }
            catch (TimeZoneNotFoundException)
            {
                // fall through to UTC
            }
        }
        return $"{utc:f} UTC";
    }

    private static DateTime Truncate(DateTime dt) =>
        new(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
