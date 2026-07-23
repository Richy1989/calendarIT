using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>One parsed iMIP REPLY: which event (by UID) and guest, and their new status.</summary>
public sealed record ImipReply(string Uid, string AttendeeEmail, AttendeeStatus Status, int Sequence);

/// <summary>Applies a guest's iMIP REPLY to the matching event's attendee.</summary>
public interface IInvitationReplyService
{
    /// <summary>Updates the guest's participation status when the reply matches an event the
    /// given user organizes and a guest on it. Returns true only when a row actually changed.</summary>
    Task<bool> ApplyReplyAsync(Guid organizerUserId, ImipReply reply, CancellationToken cancellationToken = default);
}

/// <summary>
/// EF Core-backed reply application. The match is scoped to events the organizer owns, so a
/// reply can only ever touch the recipient's own calendar — an inbox message can't be forged
/// into editing someone else's event.
/// </summary>
public sealed class InvitationReplyService(AppDbContext db) : IInvitationReplyService
{
    public async Task<bool> ApplyReplyAsync(Guid organizerUserId, ImipReply reply, CancellationToken cancellationToken = default)
    {
        var evt = await db.Events
            .Include(e => e.Attendees)
            .Where(e => e.Uid == reply.Uid && e.Calendar!.OwnerUserId == organizerUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (evt is null)
        {
            return false; // unknown UID, or not this user's event
        }

        var attendee = evt.Attendees
            .FirstOrDefault(a => a.Email.Equals(reply.AttendeeEmail, StringComparison.OrdinalIgnoreCase));
        if (attendee is null || attendee.Status == reply.Status)
        {
            return false; // not an invited guest, or the status is already what they replied
        }

        attendee.Status = reply.Status;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
