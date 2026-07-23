using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;

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
            await UpsertCopyAsync(master, inviteeId, cancellationToken);
        }

        // Withdraw from local guests just dropped (unless the same user is still invited under
        // another email casing).
        var dropped = await LocalUserIdsAsync(removed.Select(a => a.Email), organizerUserId, cancellationToken);
        foreach (var inviteeId in dropped.Where(id => !current.Contains(id)))
        {
            await DeleteCopyAsync(master.Uid, inviteeId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(CalendarEvent master, IReadOnlyCollection<Attendee> attendees, Guid organizerUserId, CancellationToken cancellationToken = default)
    {
        foreach (var inviteeId in await LocalUserIdsAsync(attendees.Select(a => a.Email), organizerUserId, cancellationToken))
        {
            await DeleteCopyAsync(master.Uid, inviteeId, cancellationToken);
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

    private async Task UpsertCopyAsync(CalendarEvent master, Guid inviteeUserId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Match the invitee's copy by shared UID, wherever they may have filed it.
        var copy = await db.Events
            .SingleOrDefaultAsync(e => e.Uid == master.Uid && e.Calendar!.OwnerUserId == inviteeUserId, cancellationToken);
        if (copy is null)
        {
            var calendar = await GetOrCreateDefaultCalendarAsync(inviteeUserId, cancellationToken);
            copy = new CalendarEvent
            {
                Id = Guid.NewGuid(),
                CalendarId = calendar.Id,
                Uid = master.Uid,
                CreatedAt = now,
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

    private async Task DeleteCopyAsync(string uid, Guid inviteeUserId, CancellationToken cancellationToken)
    {
        var copy = await db.Events
            .SingleOrDefaultAsync(e => e.Uid == uid && e.Calendar!.OwnerUserId == inviteeUserId, cancellationToken);
        if (copy is not null)
        {
            db.Events.Remove(copy);
        }
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
