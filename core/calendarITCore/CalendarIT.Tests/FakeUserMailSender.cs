using CalendarIT.Infrastructure.Mail;
using MimeKit;

namespace CalendarIT.Tests;

/// <summary>Records what would have been sent, so dispatch can be asserted without SMTP.</summary>
public sealed class FakeUserMailSender : IUserMailSender
{
    public List<(Guid UserId, MimeMessage Message)> Sent { get; } = [];

    /// <summary>Set false to act like a user with no connected mail account.</summary>
    public bool HasAccount { get; set; } = true;

    public Task<bool> TrySendAsync(Guid userId, MimeMessage message, CancellationToken cancellationToken = default)
    {
        if (!HasAccount)
        {
            return Task.FromResult(false);
        }
        Sent.Add((userId, message));
        return Task.FromResult(true);
    }
}
