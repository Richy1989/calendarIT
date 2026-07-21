namespace CalendarIT.Infrastructure.Notifications;

/// <summary>Sends a plain-text email. Implemented by SMTP, or a log-only dev fallback.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}
