using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;
using MimeKit;

namespace CalendarIT.Tests;

/// <summary>Records what would have been queued, so dispatch can be asserted without SMTP.</summary>
public sealed class FakeMailOutbox : IMailOutbox
{
    public List<(Guid UserId, MimeMessage Message)> Sent { get; } = [];

    public List<OutboxKind> Kinds { get; } = [];

    /// <summary>Set false to act like a user with no connected mail account.</summary>
    public bool HasAccount { get; set; } = true;

    public Task<bool> QueueAsync(
        Guid userId, MimeMessage message, OutboxKind kind, CancellationToken cancellationToken = default, bool save = true)
    {
        if (!HasAccount)
        {
            return Task.FromResult(false);
        }
        Sent.Add((userId, message));
        Kinds.Add(kind);
        return Task.FromResult(true);
    }

    /// <summary>Set true to act like a server with a system mail account (SMTP_*).</summary>
    public bool HasSystemMail { get; set; }

    /// <summary>What was queued for the system account.</summary>
    public List<(Guid UserId, MimeMessage Message, OutboxKind Kind)> SentViaSystem { get; } = [];

    public Task<bool> QueueSystemAsync(Guid userId, MimeMessage message, OutboxKind kind, CancellationToken cancellationToken = default)
    {
        if (!HasSystemMail)
        {
            return Task.FromResult(false);
        }
        SentViaSystem.Add((userId, message, kind));
        return Task.FromResult(true);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
