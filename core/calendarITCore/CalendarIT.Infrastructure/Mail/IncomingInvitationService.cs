using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>Applies an inbound iMIP REQUEST/CANCEL to the recipient's own calendar.</summary>
public interface IIncomingInvitationService
{
    /// <summary>
    /// A REQUEST adds (or updates) the event on the recipient's default calendar as a received
    /// invitation (status NeedsAction); a CANCEL removes it. Returns true only when a row
    /// actually changed. Idempotent: a re-sent REQUEST with the same or older SEQUENCE is a no-op.
    /// </summary>
    Task<bool> ApplyRequestAsync(Guid recipientUserId, ImipRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// EF Core-backed delivery of invitations others send us over email. The event is written onto
/// the recipient's own default calendar, keyed by shared UID + owner, and flagged with
/// <see cref="CalendarEvent.InvitationStatus"/> so the UI can show it as pending. It carries no
/// attendees or reminders of its own, so the recipient editing or deleting it never fans out
/// further invitations — it is just an event on their calendar (mirrors the same-instance copy).
///
/// An inbound message can only ever touch the mailbox owner's calendar, and it will never
/// overwrite an event the user actually owns: a matching UID whose <c>InvitationStatus</c> is
/// null (i.e. their own event) is left untouched, so a forged or colliding UID can't stomp it.
/// </summary>
public sealed class IncomingInvitationService(AppDbContext db, TimeProvider timeProvider) : IIncomingInvitationService
{
    public async Task<bool> ApplyRequestAsync(Guid recipientUserId, ImipRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await db.Events
            .SingleOrDefaultAsync(e => e.Uid == request.Uid && e.Calendar!.OwnerUserId == recipientUserId, cancellationToken);

        if (request.Method == ImipRequestMethod.Cancel)
        {
            // Only withdraw a received invitation — never delete the user's own event.
            if (existing is null || existing.InvitationStatus is null)
            {
                return false;
            }
            db.Events.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var evt = existing;
        if (evt is null)
        {
            var calendar = await GetOrCreateDefaultCalendarAsync(recipientUserId, cancellationToken);
            evt = new CalendarEvent
            {
                Id = Guid.NewGuid(),
                CalendarId = calendar.Id,
                Uid = request.Uid,
                CreatedAt = now,
                InvitationStatus = AttendeeStatus.NeedsAction,
            };
            db.Events.Add(evt);
        }
        else if (evt.InvitationStatus is null)
        {
            return false; // a UID that matches one of the user's own events — leave it alone
        }
        else if (request.Sequence < evt.Sequence)
        {
            return false; // a stale re-send of an older version; the current copy already wins
        }

        // Copy the organizer's event fields (title/times/tz/rrule/…); no categories to resolve
        // against, so an incoming COLOR is kept as the legacy hex fallback. Uid, CreatedAt,
        // InvitationStatus and OrganizerEmail are ours to own and are set here, not by Apply.
        ICalEventMapper.Apply(request.Event, evt, now);
        evt.Sequence = request.Sequence;
        evt.OrganizerEmail = request.OrganizerEmail;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<Calendar> GetOrCreateDefaultCalendarAsync(Guid userId, CancellationToken cancellationToken)
    {
        var calendar = await db.Calendars
            .Where(c => c.OwnerUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (calendar is not null)
        {
            return calendar;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        calendar = new Calendar { Id = Guid.NewGuid(), OwnerUserId = userId, Name = "Personal", CreatedAt = now, UpdatedAt = now };
        db.Calendars.Add(calendar);
        return calendar;
    }
}
