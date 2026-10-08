using CalendarIT.Domain;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// Identity of a user's outgoing system mail. All mail — invitations, reminders, password resets
/// — goes through each user's own connected account (there is no global relay), queued in the
/// <see cref="IMailOutbox"/> and sent by the outbox job.
/// </summary>
public static class UserMailSender
{
    /// <summary>The From identity for a user's outgoing system mail: the configured display
    /// <see cref="MailAccount.FromAddress"/> when set, otherwise the account address.</summary>
    public static MailboxAddress FromOf(MailAccount account)
    {
        var from = string.IsNullOrWhiteSpace(account.FromAddress) ? account.Address : account.FromAddress.Trim();
        return new MailboxAddress(from, from);
    }
}
