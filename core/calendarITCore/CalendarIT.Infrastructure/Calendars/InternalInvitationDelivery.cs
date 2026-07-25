using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Same-instance invitation delivery. When a guest's email belongs to another registered
/// CalendarIT user, the event is mirrored straight onto that user's calendar (same UID) — no
/// email round-trip, so it works even if the invited user never configured mail. The copies are
/// kept in step as the organizer edits, and withdrawn when a guest is dropped or the event is
/// deleted. External guests are unaffected (they still get the emailed iMIP invite).
///
/// The copy deliberately carries no attendees or reminders of its own, so if the invited user
/// edits or deletes it, nothing fans out further invitations — it is just an event on their
/// calendar. (Letting the invited user reply Accept/Decline back to the organizer is a follow-up.)
///
/// <para>Copies are found again by shared UID, and a UID is whatever the organizer says it is —
/// an .ics import keeps the file's, a CalDAV PUT takes it from the body. So matching on UID alone
/// let any user address a row on someone else's calendar. Every copy is stamped with
/// <see cref="CalendarEvent.SourceOrganizerUserId"/> when it is created, and only a row carrying
/// this organizer's id is ever written to or withdrawn; anything else is left untouched and no
/// copy is delivered. Note this means copies delivered before that column existed no longer track
/// the organizer's edits — the guest keeps the event, it just goes stale.</para>
/// </summary>
public interface IInternalInvitationDelivery
{
    /// <summary>Upserts copies for the local guests currently on <paramref name="master"/>, and
    /// removes copies for local guests in <paramref name="removed"/> who are no longer invited.</summary>
    Task SyncAsync(CalendarEvent master, Guid organizerUserId, IReadOnlyCollection<Attendee> removed, CancellationToken cancellationToken = default);

    /// <summary>Removes the copies for all local guests — used when the whole event is deleted.</summary>
    Task RemoveAsync(CalendarEvent master, IReadOnlyCollection<Attendee> attendees, Guid organizerUserId, CancellationToken cancellationToken = default);
}

public sealed class InternalInvitationDelivery(AppDbContext db, TimeProvider timeProvider) : IInternalInvitationDelivery
{
    public async Task SyncAsync(CalendarEvent master, Guid organizerUserId, IReadOnlyCollection<Attendee> removed, CancellationToken cancellationToken = default)
    {
        var current = await LocalUserIdsAsync(master.Attendees.Select(a => a.Email), organizerUserId, cancellationToken);
        foreach (var inviteeId in current)
        {
            await UpsertCopyAsync(master, organizerUserId, inviteeId, cancellationToken);
        }

        // Withdraw from local guests just dropped (unless the same user is still invited under
        // another email casing).
        var dropped = await LocalUserIdsAsync(removed.Select(a => a.Email), organizerUserId, cancellationToken);
        foreach (var inviteeId in dropped.Where(id => !current.Contains(id)))
        {
            await DeleteCopyAsync(master.Uid, organizerUserId, inviteeId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(CalendarEvent master, IReadOnlyCollection<Attendee> attendees, Guid organizerUserId, CancellationToken cancellationToken = default)
    {
        foreach (var inviteeId in await LocalUserIdsAsync(attendees.Select(a => a.Email), organizerUserId, cancellationToken))
        {
            await DeleteCopyAsync(master.Uid, organizerUserId, inviteeId, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Registered users whose email matches one of <paramref name="emails"/>, excluding
    /// the organizer (never deliver a copy to yourself for inviting your own address).</summary>
    private async Task<List<Guid>> LocalUserIdsAsync(IEnumerable<string> emails, Guid organizerUserId, CancellationToken cancellationToken)
    {
        var normalized = emails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();
        if (normalized.Count == 0)
        {
            return [];
        }

        var ids = await db.Users
            .Where(u => u.NormalizedEmail != null && normalized.Contains(u.NormalizedEmail))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        return ids.Where(id => id != organizerUserId).Distinct().ToList();
    }

    private async Task UpsertCopyAsync(CalendarEvent master, Guid organizerUserId, Guid inviteeUserId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Match the invitee's copy by shared UID, wherever they may have filed it.
        var existing = await db.Events
            .SingleOrDefaultAsync(e => e.Uid == master.Uid && e.Calendar!.OwnerUserId == inviteeUserId, cancellationToken);

        // A row that isn't our copy is not ours to write to. The UID came from the organizer, who
        // may have chosen it (import, CalDAV) to collide with something the guest already has —
        // their own event, or a copy from a different organizer. Leave it, and deliver nothing:
        // adding a second row with the same UID would just hand over the spam instead.
        if (existing is not null && existing.SourceOrganizerUserId != organizerUserId)
        {
            return;
        }

        var copy = existing;
        if (copy is null)
        {
            var calendar = await DefaultCalendar.GetOrCreateAsync(db, timeProvider, inviteeUserId, cancellationToken);
            copy = new CalendarEvent
            {
                Id = Guid.NewGuid(),
                CalendarId = calendar.Id,
                Uid = master.Uid,
                CreatedAt = now,
                SourceOrganizerUserId = organizerUserId,
            };
            db.Events.Add(copy);
        }

        copy.Title = master.Title;
        copy.Description = master.Description;
        copy.Location = master.Location;
        copy.Color = master.Color;
        copy.StartUtc = master.StartUtc;
        copy.EndUtc = master.EndUtc;
        copy.IsAllDay = master.IsAllDay;
        copy.TimeZoneId = master.TimeZoneId;
        copy.RRule = master.RRule;
        copy.ExDates = master.ExDates;
        copy.UpdatedAt = now;
    }

    /// <summary>Withdraws the copy this organizer delivered — and only that. A row the guest owns
    /// themselves, or one mirrored from someone else, shares nothing but a UID and stays put.</summary>
    private async Task DeleteCopyAsync(string uid, Guid organizerUserId, Guid inviteeUserId, CancellationToken cancellationToken)
    {
        var copy = await db.Events.SingleOrDefaultAsync(
            e => e.Uid == uid
                && e.Calendar!.OwnerUserId == inviteeUserId
                && e.SourceOrganizerUserId == organizerUserId,
            cancellationToken);
        if (copy is not null)
        {
            db.Events.Remove(copy);
        }
    }

}
