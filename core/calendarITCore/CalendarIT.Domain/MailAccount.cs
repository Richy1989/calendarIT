namespace CalendarIT.Domain;

/// <summary>
/// A user's personal email account, used to send appointment invitations (iMIP) from
/// their own address — and, once inbox scanning ships, to receive them. One per user
/// (<see cref="UserId"/> is the primary key). The mailbox password is stored as
/// Data-Protection ciphertext (<see cref="PasswordProtected"/>), never in the clear.
/// </summary>
public class MailAccount
{
    /// <summary>Owning user; also the primary key (one account per user).</summary>
    public Guid UserId { get; set; }

    /// <summary>The email identity invitations are sent from (e.g. rich@example.com).</summary>
    public string Address { get; set; } = string.Empty;

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    /// <summary>true = implicit TLS (465); false = STARTTLS (587).</summary>
    public bool SmtpUseSsl { get; set; }

    /// <summary>Optional for now — used by inbox scanning once it ships.</summary>
    public string? ImapHost { get; set; }

    public int ImapPort { get; set; } = 993;

    public bool ImapUseSsl { get; set; } = true;

    /// <summary>Mailbox login; often equals <see cref="Address"/>.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Mailbox password as Data-Protection ciphertext.</summary>
    public string PasswordProtected { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
