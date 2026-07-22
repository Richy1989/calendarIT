using CalendarIT.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>Sends invitation emails for an event's attendees from the user's own account.</summary>
public interface IInvitationMailer
{
    /// <summary>iMIP REQUEST (invite or update) to each attendee in <paramref name="recipients"/>.</summary>
    Task SendRequestAsync(Guid userId, CalendarEvent evt, IReadOnlyList<Attendee> recipients, CancellationToken cancellationToken = default);

    /// <summary>iMIP CANCEL to each attendee in <paramref name="recipients"/>.</summary>
    Task SendCancelAsync(Guid userId, CalendarEvent evt, IReadOnlyList<Attendee> recipients, CancellationToken cancellationToken = default);
}

/// <summary>
/// v1 sends inline with the save request: a failed send is logged as a warning and never
/// fails the event save (re-saving re-sends). No-op when the user has no mail account —
/// attendees are still stored, just not notified. An outbox + retry job is a follow-up.
/// </summary>
public sealed class InvitationMailer(
    MailAccountService accounts,
    ILogger<InvitationMailer> logger) : IInvitationMailer
{
    public Task SendRequestAsync(Guid userId, CalendarEvent evt, IReadOnlyList<Attendee> recipients, CancellationToken cancellationToken = default) =>
        SendAsync(userId, evt, recipients, InvitationBuilder.BuildRequest, "REQUEST", cancellationToken);

    public Task SendCancelAsync(Guid userId, CalendarEvent evt, IReadOnlyList<Attendee> recipients, CancellationToken cancellationToken = default) =>
        SendAsync(userId, evt, recipients, InvitationBuilder.BuildCancel, "CANCEL", cancellationToken);

    private async Task SendAsync(
        Guid userId,
        CalendarEvent evt,
        IReadOnlyList<Attendee> recipients,
        Func<MailAccount, CalendarEvent, Attendee, MimeMessage> build,
        string method,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        var account = await accounts.GetWithPasswordAsync(userId, cancellationToken);
        if (account is null)
        {
            logger.LogInformation(
                "No mail account configured for user {UserId} — {Method} for '{Title}' not sent to {Count} guest(s)",
                userId, method, evt.Title, recipients.Count);
            return;
        }
        var (mailAccount, password) = account.Value;

        try
        {
            using var client = new SmtpClient();
            var socketOptions = mailAccount.SmtpUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(mailAccount.SmtpHost, mailAccount.SmtpPort, socketOptions, cancellationToken);
            await client.AuthenticateAsync(mailAccount.Username, password, cancellationToken);
            foreach (var recipient in recipients)
            {
                await client.SendAsync(build(mailAccount, evt, recipient), cancellationToken);
            }
            await client.DisconnectAsync(quit: true, cancellationToken);
            logger.LogInformation("Sent iMIP {Method} for '{Title}' to {Count} guest(s)", method, evt.Title, recipients.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The event itself is already saved; the invite can be re-sent by saving again.
            logger.LogWarning(ex, "Sending iMIP {Method} for '{Title}' failed", method, evt.Title);
        }
    }
}
