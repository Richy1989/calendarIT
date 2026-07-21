using Microsoft.Extensions.Logging;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Dev fallback used when SMTP isn't configured: logs the email instead of sending it,
/// so reminders can be exercised without a mail server.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[email:not-configured] To={To} Subject={Subject} Body={Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
