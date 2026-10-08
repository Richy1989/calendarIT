using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Mail;

/// <summary>The user's mail account as returned to the client — never the password.</summary>
public sealed record MailAccountDto(
    string Address,
    string? FromAddress,
    string SmtpHost,
    int SmtpPort,
    bool SmtpUseSsl,
    string? ImapHost,
    int ImapPort,
    bool ImapUseSsl,
    string Username,
    int ScanIntervalMinutes,
    bool HasPassword);

/// <summary>Create/update payload. A null/empty <see cref="Password"/> keeps the stored one.</summary>
public sealed class SaveMailAccountRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Address { get; init; } = string.Empty;

    /// <summary>Optional display "From" for reminders / password resets. May be a non-mailbox
    /// address. Null/blank keeps the account address. Not validated as a deliverable mailbox.</summary>
    [MaxLength(320)]
    public string? FromAddress { get; init; }

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

    /// <summary>How often (minutes) to check the inbox for guest replies. 1–1440; default 5.</summary>
    [Range(1, 1440)]
    public int ScanIntervalMinutes { get; init; } = 5;

    [Required, MaxLength(320)]
    public string Username { get; init; } = string.Empty;

    /// <summary>Leave null/empty on update to keep the existing password.</summary>
    [MaxLength(500)]
    public string? Password { get; init; }
}

/// <summary>Outcome of a connection test.</summary>
public sealed record MailTestResult(bool Ok, string? Error);

/// <summary>
/// One message in the user's outbox, for Settings: what it is, where it's going, and how sending
/// went. <paramref name="Status"/> is "Pending", "Sent" or "Failed"; <paramref name="LastError"/>
/// is a user-facing reason, never a raw server response.
/// </summary>
public sealed record OutboxItemDto(
    Guid Id,
    string Kind,
    string Status,
    string Recipient,
    string Subject,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? SentAt,
    bool CanRetry);

/// <summary>Per-user mail account: the identity used to send (and later receive) invitations.</summary>
public interface IMailAccountService
{
    /// <summary>The user's account, or null when none is configured.</summary>
    Task<MailAccountDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<MailAccountDto> SaveAsync(Guid userId, SaveMailAccountRequest request, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Connects and authenticates against SMTP (and IMAP when configured).</summary>
    Task<MailTestResult> TestAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The user's most recent outgoing messages, newest first.</summary>
    Task<IReadOnlyList<OutboxItemDto>> ListOutboxAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Queues a failed message for another try. False when there's no such message, or
    /// it can't be retried (already sent, or no longer worth sending).</summary>
    Task<bool> RetryOutboxAsync(Guid userId, Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Removes a message from the outbox (unsent ones are then never sent).</summary>
    Task<bool> DiscardOutboxAsync(Guid userId, Guid messageId, CancellationToken cancellationToken = default);
}
