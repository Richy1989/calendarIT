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

    /// <summary>iMIP REPLY (our RSVP) back to the organizer of an invitation we received.</summary>
    Task SendReplyAsync(Guid userId, CalendarEvent evt, AttendeeStatus status, CancellationToken cancellationToken = default);
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

    public async Task SendReplyAsync(Guid userId, CalendarEvent evt, AttendeeStatus status, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(evt.OrganizerEmail))
        {
            return; // nothing to reply to (a malformed invite with no organizer)
        }

        var account = await accounts.GetWithPasswordAsync(userId, cancellationToken);
        if (account is null)
        {
            logger.LogInformation(
                "No mail account configured for user {UserId} — REPLY for '{Title}' not sent to {Organizer}",
                userId, evt.Title, evt.OrganizerEmail);
            return;
        }
        var (mailAccount, password) = account.Value;
        await SendAllAsync(mailAccount, password, [InvitationBuilder.BuildReply(mailAccount, evt, status)], "REPLY", evt.Title, cancellationToken);
    }

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
        var messages = recipients.Select(r => build(mailAccount, evt, r)).ToList();
        await SendAllAsync(mailAccount, password, messages, method, evt.Title, cancellationToken);
    }

    /// <summary>Opens one SMTP session on the user's account and sends every message. A failed
    /// send is logged and swallowed — the event/RSVP is already stored, so it can be re-sent.</summary>
    private async Task SendAllAsync(
        MailAccount account, string password, IReadOnlyList<MimeMessage> messages,
        string method, string title, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new SmtpClient();
            var socketOptions = account.SmtpUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(account.SmtpHost, account.SmtpPort, socketOptions, cancellationToken);
            await client.AuthenticateAsync(account.Username, password, cancellationToken);
            foreach (var message in messages)
            {
                await client.SendAsync(message, cancellationToken);
            }
            await client.DisconnectAsync(quit: true, cancellationToken);
            logger.LogInformation("Sent iMIP {Method} for '{Title}' ({Count} message(s))", method, title, messages.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sending iMIP {Method} for '{Title}' failed", method, title);
        }
    }
}
