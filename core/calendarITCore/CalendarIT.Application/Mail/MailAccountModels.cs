using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Mail;

/// <summary>The user's mail account as returned to the client — never the password.</summary>
public sealed record MailAccountDto(
    string Address,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseSsl,
    string? ImapHost,
    int ImapPort,
    bool ImapUseSsl,
    string Username,
    bool HasPassword);

/// <summary>Create/update payload. A null/empty <see cref="Password"/> keeps the stored one.</summary>
public sealed class SaveMailAccountRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Address { get; init; } = string.Empty;

    [Required, MaxLength(255)]
    public string SmtpHost { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 587;

    /// <summary>true = implicit TLS (465); false = STARTTLS (587).</summary>
    public bool SmtpUseSsl { get; init; }

    [MaxLength(255)]
    public string? ImapHost { get; init; }

    [Range(1, 65535)]
    public int ImapPort { get; init; } = 993;

    public bool ImapUseSsl { get; init; } = true;

    [Required, MaxLength(320)]
    public string Username { get; init; } = string.Empty;

    /// <summary>Leave null/empty on update to keep the existing password.</summary>
    [MaxLength(500)]
    public string? Password { get; init; }
}

/// <summary>Outcome of a connection test.</summary>
public sealed record MailTestResult(bool Ok, string? Error);

/// <summary>Per-user mail account: the identity used to send (and later receive) invitations.</summary>
public interface IMailAccountService
{
    /// <summary>The user's account, or null when none is configured.</summary>
    Task<MailAccountDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<MailAccountDto> SaveAsync(Guid userId, SaveMailAccountRequest request, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Connects and authenticates against SMTP (and IMAP when configured).</summary>
    Task<MailTestResult> TestAsync(Guid userId, CancellationToken cancellationToken = default);
}
