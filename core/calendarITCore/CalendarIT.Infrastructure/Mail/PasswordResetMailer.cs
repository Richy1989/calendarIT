using CalendarIT.Domain;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>Delivers a password-reset link to a user who, by definition, cannot log in.</summary>
public interface IPasswordResetMailer
{
    /// <summary>
    /// Queues the link for the user's own mail account. When there is none, nothing is sent and the
    /// operator is told (without the link) how to issue one by hand.
    /// </summary>
    Task SendAsync(Guid userId, string toAddress, string resetLink, CancellationToken cancellationToken = default);
}

/// <summary>
/// Queues the reset mail for the operator's system account (<see cref="SystemMailOptions"/>) when
/// one is configured, else for the user's <em>own</em> connected account.
///
/// Without a system account all mail goes out through per-user accounts —
/// which is awkward here, because the one person who can't act is the user themselves. It works
/// anyway: their mailbox credential is stored (encrypted) on the server, so we can send as them
/// without them being signed in, and <see cref="Domain.MailAccount.FromAddress"/> exists so the
/// message can come from something like noreply@ rather than their personal address.
///
/// The mail is queued, not sent inline. Sending inline made the forgot-password endpoint take
/// seconds for a real account and milliseconds for an unknown one — which told anyone who asked
/// exactly which addresses have accounts here.
///
/// When no mail account is connected there is nothing to send with. The link is <em>not</em>
/// logged: logs end up in log shippers, support threads and bug reports, and a working reset link
/// there is an account takeover. Instead the log says how the operator issues one themselves —
/// the <c>reset-password-link</c> command, which prints it to their own terminal only. (A queued
/// reset mail that can't be delivered is handled the same way by the outbox job.)
/// </summary>
public sealed class PasswordResetMailer(
    IMailOutbox outbox,
    ILogger<PasswordResetMailer> logger) : IPasswordResetMailer
{
    /// <summary>What the operator runs instead; the link goes to their terminal, never to the log.</summary>
    public const string ResetCommandHint = "dotnet calendarITCore.dll reset-password-link <email> (see README, \"Password recovery\")";

    public async Task SendAsync(Guid userId, string toAddress, string resetLink, CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = "Reset your 24seven password";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                Someone asked to reset the password for your 24seven account.

                Open this link to choose a new one:

                {resetLink}

                The link can be used once, and stops working as soon as the password changes.
                If this wasn't you, ignore this email — nothing has changed yet.
                """,
        };

        // The operator's system account when there is one: it reaches the address without passing
        // through a server the account's owner picked. Otherwise the user's own account.
        if (await outbox.QueueSystemAsync(userId, message, OutboxKind.PasswordReset, cancellationToken)
            || await outbox.QueueAsync(userId, message, OutboxKind.PasswordReset, cancellationToken))
        {
            return;
        }

        logger.LogWarning(
            "[password-reset:no-mail] user {UserId} has no working mail account, so no reset link was sent. " +
            "To issue one by hand, run: " + ResetCommandHint,
            userId);
    }
}
