using CalendarIT.Infrastructure.Mail;

namespace CalendarIT.Tests;

/// <summary>Captures the reset link instead of emailing it.</summary>
public sealed class FakePasswordResetMailer : IPasswordResetMailer
{
    public List<(Guid UserId, string To, string Link)> Sent { get; } = [];

    public string? LastLink => Sent.Count == 0 ? null : Sent[^1].Link;

    public Task SendAsync(Guid userId, string toAddress, string resetLink, CancellationToken cancellationToken = default)
    {
        Sent.Add((userId, toAddress, resetLink));
        return Task.CompletedTask;
    }
}
