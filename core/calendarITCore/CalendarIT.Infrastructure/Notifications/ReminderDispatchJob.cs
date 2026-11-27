using System.Globalization;
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
    ILogger<ReminderDispatchJob> logger,
    IOutboxSignal? outboxSignal = null) : IJob
{
    // Look back slightly further than the 1-minute cadence so a slow tick never drops a trigger.
    private static readonly TimeSpan Lookback = TimeSpan.FromSeconds(90);

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await RunAsync(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IMailOutbox>(),
            scope.ServiceProvider.GetRequiredService<IWebPushSender>(),
            scope.ServiceProvider.GetRequiredService<ReminderSnoozes>(),
            context.CancellationToken);
    }

    /// <summary>
    /// One dispatch pass, against an already-resolved scope. Separate from <see cref="Execute"/>
    /// so the window arithmetic and the dedup can be tested without standing up Quartz.
    /// </summary>
    /// <param name="snoozes">Seals the Snooze button's token into browser reminders, and brings
    /// snoozed ones back; without it (tests) notifications carry no Snooze.</param>
    public async Task RunAsync(
        AppDbContext db, IMailOutbox mail, IWebPushSender push, ReminderSnoozes? snoozes = null,
        CancellationToken cancellationToken = default)
    {
        var now = ReminderOccurrences.Truncate(timeProvider.GetUtcNow().UtcDateTime);
        var windowStart = now - Lookback;

        try
        {
            await DeliverSnoozedAsync(db, push, snoozes, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Snoozed reminders are a second chance; failing them must not cost the first ones.
            logger.LogWarning(ex, "Snoozed reminders could not be delivered this run");
            db.ChangeTracker.Clear();
        }

        // Tasks, on their own: whatever happens there, the event reminders below still go out.
        try
        {
            if (await TaskReminders.DispatchAsync(db, mail, push, windowStart, now, logger, cancellationToken) > 0)
            {
                outboxSignal?.Poke();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Task reminders could not be evaluated this run");
            db.ChangeTracker.Clear();
        }

        // Only reminders that could possibly be due are read. A one-off event fires at
        // Start - offset, so for its trigger to land in (windowStart, now] its start must lie in
        // (windowStart, now + longest offset] — a bounded range the database can filter on,
        // instead of reading every reminder ever created once a minute. Recurring masters have
        // to come along regardless: their occurrences aren't in the row. An all-day event begins
        // at its owner's midnight, up to a day either side of the stored one, so its range is
        // that much wider.
        var longestOffset = await db.Reminders
            .Select(r => (int?)r.MinutesBefore)
            .MaxAsync(cancellationToken) ?? 0;
        var horizon = now.AddMinutes(longestOffset);
        var allDayFrom = windowStart - ReminderOccurrences.AllDaySlack;
        var allDayTo = horizon + ReminderOccurrences.AllDaySlack;

        var reminders = await db.Reminders
            .Include(r => r.Event!).ThenInclude(e => e.Calendar)
            .Where(r => r.Event!.RRule != null
                || (r.Event!.StartUtc > windowStart && r.Event!.StartUtc <= horizon)
                || (r.Event!.IsAllDay && r.Event!.StartUtc > allDayFrom && r.Event!.StartUtc <= allDayTo))
            // A disabled account's reminders pause.
            .Where(r => !db.Users.Any(u => u.Id == r.Event!.Calendar!.OwnerUserId && u.DisabledAtUtc != null))
            .ToListAsync(cancellationToken);
        if (reminders.Count == 0)
        {
            return;
        }

        // The zone a phone's zone-less all-day events are read in. Only a refinement: should it
        // fail, those events fall back to midnight UTC and everything else still goes out.
        IReadOnlyDictionary<Guid, string> fallbackZones;
        try
        {
            fallbackZones = await ReminderOccurrences.FallbackZonesAsync(db, reminders.Select(r => r.Event!), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not look up calendar zones for all-day reminders; using UTC");
            fallbackZones = new Dictionary<Guid, string>();
        }

        // Which (reminder, occurrence) pairs actually come due this tick.
        // This job serves every user at once, so nothing about one user's data may stop it: a row
        // that can't be evaluated is logged and skipped, and the rest still go out.
        var due = new List<(Reminder Reminder, DateTime OccurrenceStartUtc)>();
        foreach (var reminder in reminders)
        {
            var offset = TimeSpan.FromMinutes(reminder.MinutesBefore);
            try
            {
                // trigger in (windowStart, now]  ⇔  occurrence start in (windowStart+offset, now+offset]
                foreach (var occStart in ReminderOccurrences.InWindow(reminder.Event!, windowStart + offset, now + offset,
                             fallbackZones.GetValueOrDefault(reminder.Event!.CalendarId)))
                {
                    due.Add((reminder, occStart));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Skipping reminder {ReminderId}: its event {EventId} could not be evaluated",
                    reminder.Id, reminder.EventId);
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
        var owners = await db.Users
            .Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new Owner(u.Email, u.Use24HourClock != false), cancellationToken);

        // Browser push targets, grouped by owner: one owner may have several (one per browser).
        var subsByOwner = (await db.PushSubscriptions
                .Where(s => ownerIds.Contains(s.UserId))
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var queuedMail = false;
        foreach (var (reminder, occStart) in due)
        {
            if (!sent.Add((reminder.Id, occStart)))
            {
                continue; // already delivered — or a duplicate within this same batch
            }

            var ev = reminder.Event!;
            var ownerUserId = ev.Calendar!.OwnerUserId;
            try
            {
                await DispatchAsync(
                    reminder, ev, occStart, ownerUserId, owners.GetValueOrDefault(ownerUserId) ?? new Owner(null, true),
                    subsByOwner.GetValueOrDefault(ownerUserId), mail, push, db, snoozes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One user's broken mailbox must not hold up everyone else's reminders. Not
                // recorded as sent, so the next tick retries while the trigger is still in range.
                logger.LogWarning(ex, "Reminder {ReminderId} for {EventId} @ {Occurrence:o} failed to send",
                    reminder.Id, ev.Id, occStart);
                sent.Remove((reminder.Id, occStart));
                continue;
            }

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
            queuedMail = true;
        }

        if (queuedMail)
        {
            outboxSignal?.Poke();
        }
    }

    private async Task DispatchAsync(
        Reminder reminder, CalendarEvent ev, DateTime occStartUtc, Guid ownerUserId, Owner owner,
        List<PushSubscription>? subscriptions, IMailOutbox mail, IWebPushSender push, AppDbContext db,
        ReminderSnoozes? snoozes, CancellationToken cancellationToken)
    {
        if (reminder.Channel == ReminderChannel.WebPush)
        {
            await DispatchWebPushAsync(reminder, ev, occStartUtc, owner, subscriptions, push, db, snoozes, snoozed: false, cancellationToken);
            return;
        }

        var ownerEmail = owner.Email;

        if (string.IsNullOrWhiteSpace(ownerEmail))
        {
            logger.LogWarning("No email on file for event {EventId}; skipping reminder", ev.Id);
            return;
        }

        var subject = $"Reminder: {ev.Title}";
        var body = $"\"{ev.Title}\" {(ev.IsAllDay ? $"is on {FormatDate(occStartUtc)} (all day)" : $"starts at {FormatLocal(occStartUtc, ev.TimeZoneId, owner.Use24Hour)}")}." +
                   (string.IsNullOrWhiteSpace(ev.Location) ? "" : $"\nLocation: {ev.Location}");

        var message = new MimeMessage();
        message.To.Add(MailboxAddress.Parse(ownerEmail));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        // Queued for the owner's own connected mail account (From uses their FromAddress override
        // when set) — staged, not saved, so it commits together with the NotificationLog row below:
        // a crash between the two can neither lose the reminder nor send it twice. Without a
        // configured account there's nothing to send from, so the reminder is only logged.
        var queued = await mail.QueueAsync(ownerUserId, message, OutboxKind.Reminder, cancellationToken, save: false);
        if (queued)
        {
            logger.LogInformation("Queued {Channel} reminder for {EventId} @ {Occurrence:o}",
                reminder.Channel, ev.Id, occStartUtc);
        }
        else
        {
            logger.LogInformation(
                "[reminder:no-mail-account] user {UserId} has no connected mail account; reminder for {EventId} not sent",
                ownerUserId, ev.Id);
        }
    }

    private async Task DispatchWebPushAsync(
        Reminder reminder, CalendarEvent ev, DateTime occStartUtc, Owner owner, List<PushSubscription>? subscriptions,
        IWebPushSender push, AppDbContext db, ReminderSnoozes? snoozes, bool snoozed, CancellationToken cancellationToken)
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

        var body = (ev.IsAllDay ? $"All day on {FormatDate(occStartUtc)}." : $"Starts at {FormatLocal(occStartUtc, ev.TimeZoneId, owner.Use24Hour)}.") +
                   (string.IsNullOrWhiteSpace(ev.Location) ? "" : $"\nLocation: {ev.Location}");
        // Same tag for every browser + this occurrence, so a second device doesn't stack a duplicate.
        var message = new PushMessage(
            $"{(snoozed ? "Snoozed reminder" : "Reminder")}: {ev.Title}", body, Url: "/", Tag: $"{reminder.Id}:{occStartUtc:O}",
            SnoozeToken: snoozes?.CreateToken(ev.Calendar!.OwnerUserId, reminder.Id, occStartUtc));

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

    /// <summary>How late a snoozed reminder may still be pushed (the server was down, say): after
    /// that it is dropped — an hour-old "in 10 minutes" helps nobody.</summary>
    private static readonly TimeSpan SnoozeGrace = TimeSpan.FromHours(1);

    /// <summary>Pushes the snoozed reminders that have come due again, each once.</summary>
    private async Task DeliverSnoozedAsync(
        AppDbContext db, IWebPushSender push, ReminderSnoozes? snoozes, DateTime now, CancellationToken cancellationToken)
    {
        var due = await db.ReminderSnoozes
            .Include(s => s.Reminder!).ThenInclude(r => r.Event!).ThenInclude(e => e.Calendar)
            .Where(s => s.SentAtUtc == null && s.DueAtUtc <= now)
            .Where(s => !db.Users.Any(u => u.Id == s.Reminder!.Event!.Calendar!.OwnerUserId && u.DisabledAtUtc != null))
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return;
        }

        var ownerIds = due.Select(s => s.Reminder!.Event!.Calendar!.OwnerUserId).Distinct().ToList();
        var clocks = await db.Users
            .Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Use24HourClock != false, cancellationToken);
        var subsByOwner = (await db.PushSubscriptions.Where(s => ownerIds.Contains(s.UserId)).ToListAsync(cancellationToken))
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var snooze in due)
        {
            // Marked first: a push that fails is not retried, as with a first reminder whose
            // subscription is gone — better once too few than every minute.
            snooze.SentAtUtc = now;
            if (snooze.DueAtUtc < now - SnoozeGrace)
            {
                continue;
            }
            var ev = snooze.Reminder!.Event!;
            var ownerUserId = ev.Calendar!.OwnerUserId;
            await DispatchWebPushAsync(
                snooze.Reminder, ev, snooze.OccurrenceStartUtc, new Owner(null, clocks.GetValueOrDefault(ownerUserId, true)),
                subsByOwner.GetValueOrDefault(ownerUserId), push, db, snoozes, snoozed: true, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The reminder owner's address and clock (null = not chosen = 24-hour).</summary>
    private sealed record Owner(string? Email, bool Use24Hour);

    // The reminder's text is English, so its dates are too — never the server's own culture,
    // which in a container is the invariant one ("Tuesday, 01 September 2026") and elsewhere
    // whatever the host happens to run. Times follow the user's 12/24-hour choice.
    internal static string FormatDate(DateTime date) =>
        date.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>A timed start in the event's own zone. Not for all-day events: their stored
    /// start is a date, and reading it through a zone names the wrong hour or even the wrong day.</summary>
    internal static string FormatLocal(DateTime utc, string? timeZoneId, bool use24Hour)
    {
        var time = use24Hour ? "HH:mm" : "h:mm tt";
        if (TimeZones.Canonical(timeZoneId) is { } zoneId && TimeZones.TryFind(zoneId, out var tz))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
            return $"{FormatDate(local)}, {local.ToString(time, CultureInfo.InvariantCulture)} ({zoneId})";
        }
        return $"{FormatDate(utc)}, {utc.ToString(time, CultureInfo.InvariantCulture)} UTC";
    }
}
