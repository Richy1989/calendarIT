using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;

namespace CalendarIT.Tests;

/// <summary>Records which invitations would have been sent, instead of talking SMTP.</summary>
public sealed class FakeInvitationMailer : IInvitationMailer
{
    public List<(string Title, string[] Recipients)> Requests { get; } = [];

    public List<(string Title, string[] Recipients)> Cancels { get; } = [];

    public Task SendRequestAsync(Guid userId, CalendarEvent evt, IReadOnlyList<Attendee> recipients, CancellationToken cancellationToken = default)
    {
        if (recipients.Count > 0)
        {
            Requests.Add((evt.Title, recipients.Select(r => r.Email).ToArray()));
        }
        return Task.CompletedTask;
    }

    public Task SendCancelAsync(Guid userId, CalendarEvent evt, IReadOnlyList<Attendee> recipients, CancellationToken cancellationToken = default)
    {
        if (recipients.Count > 0)
        {
            Cancels.Add((evt.Title, recipients.Select(r => r.Email).ToArray()));
        }
        return Task.CompletedTask;
    }
}
