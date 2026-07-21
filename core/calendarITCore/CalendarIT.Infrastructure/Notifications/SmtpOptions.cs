namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// SMTP configuration, from environment variables (<c>SMTP_HOST</c>, <c>SMTP_PORT</c>,
/// <c>SMTP_USER</c>, <c>SMTP_PASSWORD</c>, <c>SMTP_FROM</c>, <c>SMTP_USE_SSL</c>). When
/// <see cref="Host"/> is empty, email is logged instead of sent (dev-friendly).
/// </summary>
public sealed class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? User { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = "calendarit@localhost";

    public bool UseSsl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
