using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using Quartz;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Sends queued mail (<see cref="OutboxMessage"/>) through each owner's own account: one SMTP
/// session per user per pass, retries with backoff on temporary failures, and a clear stop for
/// permanent ones. Runs every 30 seconds, and immediately when something is queued
/// (<see cref="IOutboxSignal"/>).
///
/// <para>A user's mail server is the one thing here nobody else controls. It is contacted per
/// user, in isolation, under a timeout, so one that is down, slow or refusing logins only ever
/// delays that user's own mail.</para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class OutboxDispatchJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OutboxDispatchJob> logger) : IJob
{
    public static readonly JobKey Key = new("outbox-dispatch");

    /// <summary>Messages taken per pass; the rest wait for the next one.</summary>
    private const int BatchSize = 200;

    /// <summary>Attempts before a message is given up on.</summary>
    public const int MaxAttempts = 8;

    /// <summary>What the operator runs to confirm an account without mail.</summary>
    public const string EmailConfirmationHint = "dotnet calendarITCore.dll confirm-email <email>";

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await RunAsync(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<MailAccountService>(),
            scope.ServiceProvider.GetRequiredService<MailConnectionOptions>(),
            context.CancellationToken,
            system: scope.ServiceProvider.GetService<SystemMailOptions>());
    }

    /// <summary>One pass. Separate from <see cref="Execute"/> so it can be tested without Quartz.</summary>
    public async Task RunAsync(
        AppDbContext db, MailAccountService accounts, MailConnectionOptions options, CancellationToken cancellationToken = default,
        Func<MailAccount, string, CancellationToken, Task<IMailSession>>? connect = null,
        SystemMailOptions? system = null)
    {
        // The system account is the operator's own server: its host isn't held to the users' policy.
        connect ??= async (account, password, ct) => new SmtpSession(await MailConnections.OpenSmtpAsync(
            account, password, account.UserId == Guid.Empty ? SystemMailOptions.Connection : options, ct));
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var due = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAtUtc <= now)
            .OrderBy(m => m.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        // System mail goes out in one session of its own, ahead of the users' batches.
        foreach (var batch in due.GroupBy(m => m.ViaSystem ? Guid.Empty : m.UserId).OrderBy(b => b.Key != Guid.Empty))
        {
            try
            {
                await SendBatchAsync(db, accounts, batch.Key, [.. batch], now, connect, system, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let one user's batch stop the others (or the job).
                logger.LogWarning(ex, "Outbox batch for user {UserId} failed unexpectedly", batch.Key);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SendBatchAsync(
        AppDbContext db, MailAccountService accounts, Guid userId, List<OutboxMessage> messages, DateTime now,
        Func<MailAccount, string, CancellationToken, Task<IMailSession>> connect, SystemMailOptions? system,
        CancellationToken cancellationToken)
    {
        // userId is Guid.Empty for the batch of system mail (see RunAsync).
        var viaSystem = userId == Guid.Empty;
        var live = new List<OutboxMessage>();
        foreach (var m in messages)
        {
            if (m.ExpiresAtUtc is { } expires && expires <= now)
            {
                GiveUp(db, m, "It expired before it could be sent.");
            }
            else if (m.Mime is null)
            {
                GiveUp(db, m, "The message content is missing.");
            }
            else
            {
                live.Add(m);
            }
        }
        if (live.Count == 0)
        {
            return;
        }

        var loaded = viaSystem
            ? system is { IsConfigured: true } ? (Account: system.ToAccount(), Password: system.Password ?? string.Empty) : default((MailAccount Account, string Password)?)
            : await accounts.GetWithPasswordAsync(userId, cancellationToken);
        if (loaded is null)
        {
            foreach (var m in live)
            {
                GiveUp(db, m, viaSystem
                    ? "The server's own mail account (SMTP_*) is no longer configured."
                    : "No mail account is connected, or its stored password can't be read.");
            }
            return;
        }

        IMailSession client;
        try
        {
            client = await connect(loaded.Value.Account, loaded.Value.Password, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Couldn't connect or log in: everything for this user waits for the next attempt.
            var reason = MailConnections.Describe(ex);
            logger.LogWarning(ex, "Outbox: couldn't open the mail account of {Sender}: {Reason}", SenderOf(userId), reason);
            foreach (var m in live)
            {
                Retry(db, m, now, reason);
            }
            return;
        }

        using (client)
        {
            for (var i = 0; i < live.Count; i++)
            {
                var m = live[i];
                try
                {
                    using var stream = new MemoryStream(m.Mime!);
                    var message = await MimeMessage.LoadAsync(stream, cancellationToken);
                    await client.SendAsync(message, cancellationToken);
                    MarkSent(db, m, now);
                }
                catch (SmtpCommandException ex) when (ex.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted)
                {
                    // Refused for this message alone and for good; the session is still usable.
                    GiveUp(db, m, MailConnections.Describe(ex));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // The session is in an unknown state: this and the rest wait for the next attempt.
                    var reason = MailConnections.Describe(ex);
                    logger.LogWarning(ex, "Outbox: sending failed for {Sender}: {Reason}", SenderOf(userId), reason);
                    foreach (var rest in live.Skip(i))
                    {
                        Retry(db, rest, now, reason);
                    }
                    return;
                }
            }
            try
            {
                await client.CloseAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Everything was sent; a failed goodbye changes nothing.
            }
        }
    }

    private static string SenderOf(Guid userId) => userId == Guid.Empty ? "the system account (SMTP_*)" : $"user {userId}";

    /// <summary>Mail whose body is a live credential (a reset or confirmation link): deleted once
    /// delivered or given up on, never kept for Settings.</summary>
    private static bool CarriesToken(OutboxKind kind) => kind is OutboxKind.PasswordReset or OutboxKind.EmailConfirmation;

    private void MarkSent(AppDbContext db, OutboxMessage m, DateTime now)
    {
        if (CarriesToken(m.Kind))
        {
            // The body holds a live token; once delivered there's no reason to keep it.
            db.OutboxMessages.Remove(m);
            logger.LogInformation("Sent a {Kind} link to user {UserId}", m.Kind == OutboxKind.PasswordReset ? "password-reset" : "confirmation", m.UserId);
            return;
        }
        m.Status = OutboxStatus.Sent;
        m.SentAtUtc = now;
        m.Attempts++;
        m.LastError = null;
        m.Mime = null; // the record stays for Settings; the content isn't needed any more
        logger.LogInformation("Sent {Kind} mail {MessageId} for user {UserId}", m.Kind, m.Id, m.UserId);
    }

    private void Retry(AppDbContext db, OutboxMessage m, DateTime now, string reason)
    {
        m.Attempts++;
        m.LastError = reason;
        if (m.Attempts >= MaxAttempts)
        {
            GiveUp(db, m, reason);
            return;
        }
        m.NextAttemptAtUtc = now + Backoff(m.Attempts);
    }

    /// <summary>Wait before attempt n+1: quick at first (a blip), then backing off to hours (an outage).</summary>
    public static TimeSpan Backoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromHours(1),
        5 => TimeSpan.FromHours(3),
        _ => TimeSpan.FromHours(6),
    };

    private void GiveUp(AppDbContext db, OutboxMessage m, string reason)
    {
        if (m.Kind == OutboxKind.PasswordReset)
        {
            // The link itself never goes to the log (see PasswordResetMailer): the message, and the
            // live token in it, are deleted, and the operator is told how to issue a new one.
            logger.LogWarning(
                "[password-reset:no-mail] the reset mail for user {UserId} could not be sent ({Reason}). " +
                "To issue a link by hand, run: " + PasswordResetMailer.ResetCommandHint,
                m.UserId, reason);
            db.OutboxMessages.Remove(m);
            return;
        }
        if (m.Kind == OutboxKind.EmailConfirmation)
        {
            logger.LogWarning(
                "[email-confirmation:no-mail] the confirmation mail for user {UserId} could not be sent ({Reason}). " +
                "The user can ask for a new one, or confirm the account by hand: " + EmailConfirmationHint,
                m.UserId, reason);
            db.OutboxMessages.Remove(m);
            return;
        }
        m.Status = OutboxStatus.Failed;
        m.LastError = reason;
        logger.LogWarning("Gave up on {Kind} mail {MessageId} for user {UserId}: {Reason}", m.Kind, m.Id, m.UserId, reason);
    }
}

/// <summary>Pokes the outbox job through Quartz; a no-op where no scheduler runs (tests, tools).</summary>
public sealed class QuartzOutboxSignal(ISchedulerFactory? schedulers, ILogger<QuartzOutboxSignal> logger) : IOutboxSignal
{
    public void Poke()
    {
        if (schedulers is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var scheduler = await schedulers.GetScheduler();
                if (scheduler.IsStarted && !scheduler.IsShutdown)
                {
                    await scheduler.TriggerJob(OutboxDispatchJob.Key);
                }
            }
            catch (Exception ex)
            {
                // The regular tick picks the message up anyway.
                logger.LogDebug(ex, "Couldn't trigger the outbox job early");
            }
        });
    }
}

/// <summary>An open, authenticated connection to a user's outgoing mail server.</summary>
public interface IMailSession : IDisposable
{
    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

/// <summary>The real session: MailKit SMTP.</summary>
public sealed class SmtpSession(SmtpClient client) : IMailSession
{
    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken) =>
        await client.SendAsync(message, cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken) => client.DisconnectAsync(quit: true, cancellationToken);

    public void Dispose() => client.Dispose();
}
