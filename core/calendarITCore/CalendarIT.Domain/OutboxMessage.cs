namespace CalendarIT.Domain;

/// <summary>What an outgoing message is for — decides how long it stays worth sending.</summary>
public enum OutboxKind
{
    Invitation,
    Reply,
    Reminder,
    PasswordReset,
    EmailConfirmation,
    AdminNotice,
}

public enum OutboxStatus
{
    /// <summary>Waiting for its next attempt.</summary>
    Pending,

    Sent,

    /// <summary>Given up on: permanently refused, out of attempts, or no longer worth sending.</summary>
    Failed,
}

/// <summary>
/// An email waiting to go out through its owner's own mail account. Mail is queued here and sent
/// by a background job rather than inside the request that caused it, so a slow or broken mail
/// server never holds up saving an event, and a failed send is retried instead of lost.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>The user whose connected mail account sends this message — or, with
    /// <see cref="ViaSystem"/>, the user it is about.</summary>
    public Guid UserId { get; set; }

    /// <summary>Sent through the operator's system mail account (<c>SMTP_*</c>) instead of the
    /// user's own: account mail (email confirmation, password reset) that must reach the address
    /// without passing through a server the user picked.</summary>
    public bool ViaSystem { get; set; }

    public OutboxKind Kind { get; set; }

    public OutboxStatus Status { get; set; }

    /// <summary>For display in Settings only; the message itself carries the real headers.</summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>For display in Settings only.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The complete MIME message. Cleared once sent — only the record of it is kept.</summary>
    public byte[]? Mime { get; set; }

    public int Attempts { get; set; }

    /// <summary>The last failure, already phrased for the user (never a raw exception).</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime NextAttemptAtUtc { get; set; }

    /// <summary>After this, sending is pointless (a reminder for something already over, a reset
    /// link that has expired) and the message is dropped instead. Null = no deadline.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    public DateTime? SentAtUtc { get; set; }
}
