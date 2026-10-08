using CalendarIT.Domain;
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
/// Queues invitation mail in the user's outbox (<see cref="IMailOutbox"/>); the outbox job sends
/// it, retrying while the mail server is unreachable. Saving an event therefore never waits on
/// SMTP and never fails because of it. No-op when the user has no mail account — attendees are
/// still stored, just not notified.
/// </summary>
public sealed class InvitationMailer(
    MailAccountService accounts,
    IMailOutbox outbox,
    ILogger<InvitationMailer> logger) : IInvitationMailer
{
    public Task SendRequestAsync(Guid userId, CalendarEvent evt, IReadOnlyList<Attendee> recipients, CancellationToken cancellationToken = default) =>
        QueueAsync(userId, evt, recipients, InvitationBuilder.BuildRequest, "REQUEST", cancellationToken);

    public Task SendCancelAsync(Guid userId, CalendarEvent evt, IReadOnlyList<Attendee> recipients, CancellationToken cancellationToken = default) =>
        QueueAsync(userId, evt, recipients, InvitationBuilder.BuildCancel, "CANCEL", cancellationToken);

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
                "No mail account configured for user {UserId} — REPLY for event {EventId} not sent",
                userId, evt.Id);
            return;
        }
        await outbox.QueueAsync(userId, InvitationBuilder.BuildReply(account.Value.Account, evt, status), OutboxKind.Reply, cancellationToken);
    }

    private async Task QueueAsync(
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
                "No mail account configured for user {UserId} — {Method} for event {EventId} not sent to {Count} guest(s)",
                userId, method, evt.Id, recipients.Count);
            return;
        }
        foreach (var recipient in recipients)
        {
            await outbox.QueueAsync(userId, build(account.Value.Account, evt, recipient), OutboxKind.Invitation, cancellationToken, save: false);
        }
        await outbox.FlushAsync(cancellationToken);
    }
}
