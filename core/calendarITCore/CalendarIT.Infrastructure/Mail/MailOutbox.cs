using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>Queues mail to go out through a user's own connected account.</summary>
public interface IMailOutbox
{
    /// <summary>
    /// Queues <paramref name="message"/> for sending through <paramref name="userId"/>'s mail
    /// account and returns true — or returns false, queuing nothing, when that user has no usable
    /// account. With <paramref name="save"/> false the message is only staged on the shared
    /// context, to be committed by the caller's own save (so it lands atomically with the work
    /// that produced it); otherwise it is saved and the sender is woken at once.
    /// </summary>
    Task<bool> QueueAsync(
        Guid userId, MimeMessage message, OutboxKind kind, CancellationToken cancellationToken = default, bool save = true);

    /// <summary>Saves what was staged with <c>save: false</c> and wakes the sender.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>Wakes the outbox sender so freshly queued mail goes out now, not at the next tick.</summary>
public interface IOutboxSignal
{
    void Poke();
}

public sealed class MailOutbox(
    AppDbContext db, MailAccountService accounts, TimeProvider clock, IOutboxSignal signal) : IMailOutbox
{
    /// <summary>Accounts already looked up in this scope — queuing an invitation to fifty guests
    /// shouldn't decrypt the same mailbox password fifty times.</summary>
    private readonly Dictionary<Guid, MailAccount?> _accounts = [];

    public async Task<bool> QueueAsync(
        Guid userId, MimeMessage message, OutboxKind kind, CancellationToken cancellationToken = default, bool save = true)
    {
        if (!_accounts.TryGetValue(userId, out var account))
        {
            account = (await accounts.GetWithPasswordAsync(userId, cancellationToken))?.Account;
            _accounts[userId] = account;
        }
        if (account is null)
        {
            return false;
        }
        if (message.From.Count == 0)
        {
            message.From.Add(UserMailSender.FromOf(account));
        }

        using var buffer = new MemoryStream();
        await message.WriteToAsync(buffer, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = kind,
            Status = OutboxStatus.Pending,
            Recipient = Clip(string.Join(", ", message.To.Mailboxes.Select(m => m.Address)), 320),
            Subject = Clip(message.Subject ?? string.Empty, 500),
            Mime = buffer.ToArray(),
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
            ExpiresAtUtc = now + LifetimeOf(kind),
        });

        if (save)
        {
            await db.SaveChangesAsync(cancellationToken);
            signal.Poke();
        }
        return true;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await db.SaveChangesAsync(cancellationToken);
        signal.Poke();
    }

    /// <summary>
    /// How long a message stays worth sending. A reminder that arrives hours late is noise, and a
    /// reset link outlives its token by nothing; an invitation can wait out a mail server's
    /// weekend outage.
    /// </summary>
    public static TimeSpan LifetimeOf(OutboxKind kind) => kind switch
    {
        OutboxKind.Reminder => TimeSpan.FromHours(1),
        OutboxKind.PasswordReset => TimeSpan.FromHours(2),
        _ => TimeSpan.FromDays(3),
    };

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
