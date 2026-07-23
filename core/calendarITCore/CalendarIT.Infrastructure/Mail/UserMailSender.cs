using CalendarIT.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// Sends a single message through a user's own connected mail account (SMTP). This is the
/// shared path for system notifications — reminders today, password resets later — so the app
/// no longer needs a global SMTP relay. The message's From is filled from the account's
/// <see cref="MailAccount.FromAddress"/> (falling back to its address) when the caller left it unset.
/// </summary>
public interface IUserMailSender
{
    /// <summary>Sends <paramref name="message"/> via the user's account. Returns false — sending
    /// nothing — when the user has no usable mail account (never configured, or keys lost). SMTP
    /// failures throw, so the caller can decide whether to retry.</summary>
    Task<bool> TrySendAsync(Guid userId, MimeMessage message, CancellationToken cancellationToken = default);
}

public sealed class UserMailSender(MailAccountService accounts) : IUserMailSender
{
    public async Task<bool> TrySendAsync(Guid userId, MimeMessage message, CancellationToken cancellationToken = default)
    {
        var loaded = await accounts.GetWithPasswordAsync(userId, cancellationToken);
        if (loaded is null)
        {
            return false;
        }
        var (account, password) = loaded.Value;

        if (message.From.Count == 0)
        {
            message.From.Add(FromOf(account));
        }

        using var client = new SmtpClient();
        var socketOptions = account.SmtpUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(account.SmtpHost, account.SmtpPort, socketOptions, cancellationToken);
        await client.AuthenticateAsync(account.Username, password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
        return true;
    }

    /// <summary>The From identity for a user's outgoing system mail: the configured display
    /// <see cref="MailAccount.FromAddress"/> when set, otherwise the account address.</summary>
    public static MailboxAddress FromOf(MailAccount account)
    {
        var from = string.IsNullOrWhiteSpace(account.FromAddress) ? account.Address : account.FromAddress.Trim();
        return new MailboxAddress(from, from);
    }
}
