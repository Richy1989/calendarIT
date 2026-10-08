using CalendarIT.Application;
using CalendarIT.Application.Mail;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Net;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// EF Core-backed <see cref="IMailAccountService"/>. The mailbox password is stored as
/// Data-Protection ciphertext; if the protection keys were lost (e.g. wiped appdata),
/// decryption failure degrades to "no password stored" so the user simply re-enters it.
/// </summary>
public sealed class MailAccountService(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    TimeProvider timeProvider,
    MailConnectionOptions? connectionOptions = null,
    IOutboxSignal? outboxSignal = null) : IMailAccountService
{
    private const string ProtectorPurpose = "MailAccount";

    public async Task<MailAccountDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var entity = await db.MailAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    private MailConnectionOptions Options => connectionOptions ?? new MailConnectionOptions();

    public async Task<MailAccountDto> SaveAsync(Guid userId, SaveMailAccountRequest request, CancellationToken cancellationToken = default)
    {
        // An address the server may not connect to is refused up front, rather than stored and
        // then failing every send. (Host names are checked when they're actually connected to.)
        foreach (var host in new[] { request.SmtpHost, request.ImapHost })
        {
            if (!string.IsNullOrWhiteSpace(host) && OutboundHostPolicy.IsBlockedLiteral(host, Options.HostScope))
            {
                throw new InvalidInputException($"Connections to '{host.Trim()}' aren't allowed on this server.");
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = await db.MailAccounts
            .SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);

        if (entity is null)
        {
            entity = new MailAccount { UserId = userId, CreatedAt = now };
            db.MailAccounts.Add(entity);
        }

        entity.Address = request.Address.Trim();
        entity.FromAddress = string.IsNullOrWhiteSpace(request.FromAddress) ? null : request.FromAddress.Trim();
        entity.SmtpHost = request.SmtpHost.Trim();
        entity.SmtpPort = request.SmtpPort;
        entity.SmtpUseSsl = request.SmtpUseSsl;
        entity.ImapHost = string.IsNullOrWhiteSpace(request.ImapHost) ? null : request.ImapHost.Trim();
        entity.ImapPort = request.ImapPort;
        entity.ImapUseSsl = request.ImapUseSsl;
        entity.ScanIntervalMinutes = request.ScanIntervalMinutes;
        entity.Username = request.Username.Trim();
        if (!string.IsNullOrEmpty(request.Password))
        {
            entity.PasswordProtected = dataProtection.CreateProtector(ProtectorPurpose).Protect(request.Password);
        }
        entity.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var entity = await db.MailAccounts
            .SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (entity is null)
        {
            return false;
        }
        db.MailAccounts.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<MailTestResult> TestAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var entity = await db.MailAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (entity is null)
        {
            return new MailTestResult(false, "No email account is configured.");
        }
        var password = UnprotectPassword(entity);
        if (password is null)
        {
            return new MailTestResult(false, "The stored password can't be read — please re-enter it.");
        }

        var stage = "SMTP";
        try
        {
            using (var smtp = await MailConnections.OpenSmtpAsync(entity, password, Options, cancellationToken))
            {
                await smtp.DisconnectAsync(quit: true, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(entity.ImapHost))
            {
                stage = "IMAP";
                using var imap = await MailConnections.OpenImapAsync(entity, password, Options, cancellationToken);
                await imap.DisconnectAsync(quit: true, cancellationToken);
            }

            return new MailTestResult(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new MailTestResult(false, $"{stage}: {MailConnections.Describe(ex)}");
        }
    }

    public async Task<IReadOnlyList<OutboxItemDto>> ListOutboxAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.CreatedAtUtc)
            .Take(50)
            .Select(m => new
            {
                m.Id, m.Kind, m.Status, m.Recipient, m.Subject, m.Attempts, m.LastError,
                m.CreatedAtUtc, m.NextAttemptAtUtc, m.SentAtUtc, m.ExpiresAtUtc, HasContent = m.Mime != null,
            })
            .ToListAsync(cancellationToken);
        return rows.Select(m => new OutboxItemDto(
                m.Id, m.Kind.ToString(), m.Status.ToString(), m.Recipient, m.Subject, m.Attempts, m.LastError,
                Utc(m.CreatedAtUtc),
                m.Status == OutboxStatus.Pending ? Utc(m.NextAttemptAtUtc) : null,
                m.SentAtUtc is { } sent ? Utc(sent) : null,
                CanRetry: m.Status == OutboxStatus.Failed && m.HasContent && (m.ExpiresAtUtc is null || m.ExpiresAtUtc > now)))
            .ToList();
    }

    public async Task<bool> RetryOutboxAsync(Guid userId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var message = await db.OutboxMessages.SingleOrDefaultAsync(m => m.Id == messageId && m.UserId == userId, cancellationToken);
        if (message is not { Status: OutboxStatus.Failed, Mime: not null } || message.ExpiresAtUtc <= now)
        {
            return false;
        }
        message.Status = OutboxStatus.Pending;
        message.Attempts = 0;
        message.NextAttemptAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        outboxSignal?.Poke();
        return true;
    }

    public async Task<bool> DiscardOutboxAsync(Guid userId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var deleted = await db.OutboxMessages
            .Where(m => m.Id == messageId && m.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>
    /// A detached copy of the account plus its clear-text password — for internal senders only.
    /// Deliberately untracked: callers only read connection settings from it, and returning a
    /// tracked entity would invite edits that quietly never persist.
    /// </summary>
    internal async Task<(MailAccount Account, string Password)?> GetWithPasswordAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var entity = await db.MailAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (entity is null)
        {
            return null;
        }
        var password = UnprotectPassword(entity);
        return password is null ? null : (entity, password);
    }

    /// <summary>Decrypts a tracked account's mailbox password for internal senders/scanners;
    /// null when none is stored or the protection keys can't read it.</summary>
    internal string? Unprotect(MailAccount account) => UnprotectPassword(account);

    private string? UnprotectPassword(MailAccount entity)
    {
        if (string.IsNullOrEmpty(entity.PasswordProtected))
        {
            return null;
        }
        try
        {
            return dataProtection.CreateProtector(ProtectorPurpose).Unprotect(entity.PasswordProtected);
        }
        catch (Exception)
        {
            return null; // keys rotated/lost — treat as not configured
        }
    }

    private MailAccountDto ToDto(MailAccount a) =>
        new(a.Address, a.FromAddress, a.SmtpHost, a.SmtpPort, a.SmtpUseSsl,
            a.ImapHost, a.ImapPort, a.ImapUseSsl, a.Username,
            a.ScanIntervalMinutes,
            // Deliberately a real decrypt, not just "is there ciphertext": if the protection keys
            // were lost the stored password is useless, and the UI has to prompt for it again
            // rather than claim one is configured. Worth an unprotect per settings load.
            HasPassword: UnprotectPassword(a) is not null);
}
