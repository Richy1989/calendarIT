using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
///
/// <para>A series arrives whole (master plus RECURRENCE-ID overrides) and is written whole; an
/// update or cancellation of single instances carries only those, and touches only them.</para>
/// </summary>
public sealed class IncomingInvitationService(AppDbContext db, TimeProvider timeProvider) : IIncomingInvitationService
{
    public async Task<bool> ApplyRequestAsync(Guid recipientUserId, ImipRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await db.Events
            .Include(e => e.Reminders)
            .Include(e => e.Overrides).ThenInclude(o => o.Reminders)
            .AsSplitQuery()
            .SingleOrDefaultAsync(
                e => e.Uid == request.Uid && e.Calendar!.OwnerUserId == recipientUserId && e.SeriesMasterId == null,
                cancellationToken);

        // Only ever a received invitation — never the user's own event under a colliding UID.
        if (existing is { InvitationStatus: null })
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (request.Method == ImipRequestMethod.Cancel)
        {
            if (existing is null)
            {
                return false;
            }
            if (request.IsInstanceOnly && existing.RRule is not null)
            {
                // Cancelling some occurrences: suppress just those (and drop their edits).
                var cancelled = (request.Overrides ?? [])
                    .Select(ICalEventMapper.ReadRecurrenceIdUtc)
                    .OfType<DateTime>()
                    .ToHashSet();
                var suppressed = ExDates.Parse(existing.ExDates);
                suppressed.UnionWith(cancelled);
                existing.ExDates = ExDates.Format(suppressed.Order());
                foreach (var o in existing.Overrides.Where(o => o.RecurrenceIdUtc is { } r && cancelled.Contains(ExDates.TruncateToSeconds(r))).ToList())
                {
                    existing.Overrides.Remove(o);
                    db.Events.Remove(o);
                }
                existing.UpdatedAt = now;
            }
            else
            {
                db.Events.Remove(existing); // its overrides go with it
            }
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (existing is not null && request.Sequence < existing.Sequence)
        {
            return false; // a stale re-send of an older version; the current copy already wins
        }

        var writer = new SeriesWriter(db);
        CalendarEvent evt;
        if (request.IsInstanceOnly && existing is { RRule: not null })
        {
            // An update to single occurrences of a series we already have.
            writer.UpsertOverrides(existing, request.Overrides ?? [], now, categories: null, replaceAlarms: false);
            evt = existing;
        }
        else
        {
            var calendarId = existing?.CalendarId
                ?? (await DefaultCalendar.GetOrCreateAsync(db, timeProvider, recipientUserId, cancellationToken)).Id;
            // Copy the organizer's event fields (title/times/tz/rrule/…); no categories to resolve
            // against, so an incoming COLOR is kept as the legacy hex fallback. Someone else's
            // alarms aren't ours, so reminders are left alone.
            evt = writer.Write(
                existing, request.Event, request.IsInstanceOnly ? [] : request.Overrides ?? [],
                calendarId, request.Uid, now, categories: null, replaceAlarms: false);
            if (existing is null)
            {
                evt.InvitationStatus = AttendeeStatus.NeedsAction;
            }
        }

        // Uid, CreatedAt, InvitationStatus and OrganizerEmail are ours to own and are set here.
        evt.Sequence = Math.Max(evt.Sequence, request.Sequence);
        evt.OrganizerEmail = request.OrganizerEmail;
        foreach (var o in evt.Overrides)
        {
            SeriesWriter.InheritFromMaster(o, evt);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
