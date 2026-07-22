using CalendarIT.Application.Mail;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
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
    TimeProvider timeProvider) : IMailAccountService
{
    private const string ProtectorPurpose = "MailAccount";

    public async Task<MailAccountDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var entity = await db.MailAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<MailAccountDto> SaveAsync(Guid userId, SaveMailAccountRequest request, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = await db.MailAccounts
            .SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);

        if (entity is null)
        {
            entity = new MailAccount { UserId = userId, CreatedAt = now };
            db.MailAccounts.Add(entity);
        }

        entity.Address = request.Address.Trim();
        entity.SmtpHost = request.SmtpHost.Trim();
        entity.SmtpPort = request.SmtpPort;
        entity.SmtpUseSsl = request.SmtpUseSsl;
        entity.ImapHost = string.IsNullOrWhiteSpace(request.ImapHost) ? null : request.ImapHost.Trim();
        entity.ImapPort = request.ImapPort;
        entity.ImapUseSsl = request.ImapUseSsl;
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

        try
        {
            using (var smtp = new SmtpClient())
            {
                var socketOptions = entity.SmtpUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
                await smtp.ConnectAsync(entity.SmtpHost, entity.SmtpPort, socketOptions, cancellationToken);
                await smtp.AuthenticateAsync(entity.Username, password, cancellationToken);
                await smtp.DisconnectAsync(quit: true, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(entity.ImapHost))
            {
                using var imap = new ImapClient();
                var socketOptions = entity.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
                await imap.ConnectAsync(entity.ImapHost, entity.ImapPort, socketOptions, cancellationToken);
                await imap.AuthenticateAsync(entity.Username, password, cancellationToken);
                await imap.DisconnectAsync(quit: true, cancellationToken);
            }

            return new MailTestResult(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new MailTestResult(false, ex.Message);
        }
    }

    /// <summary>The tracked entity plus its clear-text password — for internal senders only.</summary>
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
        new(a.Address, a.SmtpHost, a.SmtpPort, a.SmtpUseSsl,
            a.ImapHost, a.ImapPort, a.ImapUseSsl, a.Username,
            HasPassword: UnprotectPassword(a) is not null);
}
