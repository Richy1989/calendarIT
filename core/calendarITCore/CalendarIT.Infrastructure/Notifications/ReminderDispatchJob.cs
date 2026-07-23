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
        var cancellationToken = context.CancellationToken;
        var now = Truncate(timeProvider.GetUtcNow().UtcDateTime);
        var windowStart = now - Lookback;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mail = scope.ServiceProvider.GetRequiredService<IUserMailSender>();

        var reminders = await db.Reminders
            .Include(r => r.Event!).ThenInclude(e => e.Calendar)
            .ToListAsync(cancellationToken);
        if (reminders.Count == 0)
        {
            return;
        }

        // Resolve owner emails once.
        var ownerIds = reminders.Select(r => r.Event!.Calendar!.OwnerUserId).Distinct().ToList();
        var emailByOwner = await db.Users
            .Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        foreach (var reminder in reminders)
        {
            var ev = reminder.Event!;
            var offset = TimeSpan.FromMinutes(reminder.MinutesBefore);
            // trigger in (windowStart, now]  ⇔  occurrence start in (windowStart+offset, now+offset]
            var occFrom = windowStart + offset;
            var occTo = now + offset;

            foreach (var occStart in OccurrencesInWindow(ev, occFrom, occTo))
            {
                var already = await db.NotificationLogs
                    .AnyAsync(n => n.ReminderId == reminder.Id && n.OccurrenceStartUtc == occStart, cancellationToken);
                if (already)
                {
                    continue;
                }

                var ownerUserId = ev.Calendar!.OwnerUserId;
                await DispatchAsync(reminder, ev, occStart, ownerUserId, emailByOwner.GetValueOrDefault(ownerUserId), mail, cancellationToken);

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
        IUserMailSender mail, CancellationToken cancellationToken)
    {
        if (reminder.Channel == ReminderChannel.WebPush)
        {
            logger.LogInformation("Web Push reminder for {EventId} @ {Occurrence:o} — delivery lands in Phase 5b", ev.Id, occStartUtc);
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
