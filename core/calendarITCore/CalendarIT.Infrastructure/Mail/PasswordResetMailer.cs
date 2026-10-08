using CalendarIT.Domain;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>Delivers a password-reset link to a user who, by definition, cannot log in.</summary>
public interface IPasswordResetMailer
{
    /// <summary>
    /// Emails the link, or — when the account has no usable mail account — writes it to the
    /// application log so the operator can still recover the account.
    /// </summary>
    Task SendAsync(Guid userId, string toAddress, string resetLink, CancellationToken cancellationToken = default);
}

/// <summary>
/// Queues the reset mail for the user's <em>own</em> connected account.
///
/// This app has no global SMTP relay by design — all mail goes out through per-user accounts —
/// which is awkward here, because the one person who can't act is the user themselves. It works
/// anyway: their mailbox credential is stored (encrypted) on the server, so we can send as them
/// without them being signed in, and <see cref="Domain.MailAccount.FromAddress"/> exists so the
/// message can come from something like noreply@ rather than their personal address.
///
/// The mail is queued, not sent inline. Sending inline made the forgot-password endpoint take
/// seconds for a real account and milliseconds for an unknown one — which told anyone who asked
/// exactly which addresses have accounts here.
///
/// When no mail account is connected there is nothing to send with. Rather than leave the
/// account unrecoverable, the link is logged at warning level: on a self-hosted box whoever can
/// read the container log already has the database, so this grants no access they didn't have —
/// and it is the difference between a recoverable instance and one that needs SQL surgery. (If a
/// queued reset mail can't be delivered, the outbox job logs it the same way.)
/// </summary>
public sealed class PasswordResetMailer(
    IMailOutbox outbox,
    ILogger<PasswordResetMailer> logger) : IPasswordResetMailer
{
    public async Task SendAsync(Guid userId, string toAddress, string resetLink, CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = "Reset your CalendarIT password";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                Someone asked to reset the password for your CalendarIT account.

                Open this link to choose a new one:

                {resetLink}

                The link can be used once, and stops working as soon as the password changes.
                If this wasn't you, ignore this email — nothing has changed yet.
                """,
        };

        if (await outbox.QueueAsync(userId, message, OutboxKind.PasswordReset, cancellationToken))
        {
            return;
        }

        logger.LogWarning(
            "[password-reset:no-mail] user {UserId} ({Email}) has no working mail account, so the link " +
            "could not be emailed. Give it to them by hand — it expires and is single-use: {ResetLink}",
            userId, toAddress, resetLink);
    }
}
