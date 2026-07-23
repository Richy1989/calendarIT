using System.Globalization;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>EF Core-backed <see cref="IEventService"/>. All queries are scoped by owner.</summary>
public sealed class EventService(
    AppDbContext db, TimeProvider timeProvider, IInvitationMailer mailer, IInternalInvitationDelivery delivery) : IEventService
{
    public async Task<IReadOnlyList<EventDto>> GetEventsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var fromUtc = from?.UtcDateTime;
        var toUtc = to?.UtcDateTime;
        var results = new List<EventDto>();

        // Single (non-recurring) events: filter by range in SQL.
        var singles = db.Events.AsNoTracking().Include(e => e.Reminders).Include(e => e.Attendees).Include(e => e.Category)
            .Where(e => e.Calendar!.OwnerUserId == userId && e.RRule == null);
        if (toUtc is not null)
        {
            singles = singles.Where(e => e.StartUtc < toUtc);
        }
        if (fromUtc is not null)
        {
            singles = singles.Where(e => (e.EndUtc ?? e.StartUtc) >= fromUtc);
        }
        results.AddRange((await singles.ToListAsync(cancellationToken)).Select(ToDto));

        // Recurring series: fetch masters, expand within the window (needs a bounded range).
        if (fromUtc is not null && toUtc is not null)
        {
            var masters = await db.Events.AsNoTracking().Include(e => e.Reminders).Include(e => e.Attendees).Include(e => e.Category)
                .Where(e => e.Calendar!.OwnerUserId == userId && e.RRule != null)
                .ToListAsync(cancellationToken);

            foreach (var m in masters)
            {
                var end = m.EndUtc ?? m.StartUtc.AddHours(1);
                var exDates = ParseExDates(m.ExDates);
                var reminders = MapReminders(m.Reminders);
                var attendees = MapAttendees(m.Attendees);
                foreach (var occ in RecurrenceExpander.Expand(m.StartUtc, end, m.TimeZoneId, m.RRule!, exDates, fromUtc.Value, toUtc.Value))
                {
                    results.Add(new EventDto(
                        m.Id, m.CalendarId, m.Title, m.Description, m.Location, m.CategoryId, DisplayColor(m),
                        new DateTimeOffset(occ.StartUtc, TimeSpan.Zero),
                        new DateTimeOffset(occ.EndUtc, TimeSpan.Zero),
                        m.IsAllDay, Recurring: true, Recurrence: m.RRule, Reminders: reminders, Attendees: attendees));
                }
            }
        }

        return results.OrderBy(r => r.Start).ToList();
    }

    public async Task<IReadOnlyList<EventSearchResult>> SearchAsync(
        Guid userId, string query, int limit, CancellationToken cancellationToken = default)
    {
        var q = query?.Trim() ?? string.Empty;
        if (q.Length == 0)
        {
            return [];
        }
        limit = Math.Clamp(limit, 1, 50);

        // Case-insensitive substring on title/location. ToLower().Contains() translates to
        // `lower(col) LIKE '%q%'` on both SQLite and Npgsql, so this stays provider-agnostic.
        var needle = q.ToLowerInvariant();
        var matches = await db.Events.AsNoTracking().Include(e => e.Category)
            .Where(e => e.Calendar!.OwnerUserId == userId
                && (e.Title.ToLower().Contains(needle)
                    || (e.Location != null && e.Location.ToLower().Contains(needle))))
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var results = matches.Select(e =>
        {
            var start = e.RRule is null ? e.StartUtc : RepresentativeOccurrenceUtc(e, now);
            return new EventSearchResult(
                e.Id, e.Title, e.Location, DisplayColor(e),
                new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)),
                e.IsAllDay, e.RRule is not null);
        });

        // Upcoming first (soonest to now), then the nearest past occurrences.
        return results
            .OrderBy(r => r.Start.UtcDateTime < now)
            .ThenBy(r => Math.Abs((r.Start.UtcDateTime - now).Ticks))
            .Take(limit)
            .ToList();
    }

    // For a recurring master, the date we surface in search: the next occurrence within the
    // next 2 years, else the most recent occurrence in the past 5 years, else the series start.
    private static DateTime RepresentativeOccurrenceUtc(CalendarEvent e, DateTime now)
    {
        var end = e.EndUtc ?? e.StartUtc.AddHours(1);
        var exDates = RecurrenceExpander.ParseExDates(e.ExDates);

        var upcoming = RecurrenceExpander
            .Expand(e.StartUtc, end, e.TimeZoneId, e.RRule!, exDates, now, now.AddYears(2))
            .FirstOrDefault();
        if (upcoming.StartUtc != default)
        {
            return upcoming.StartUtc;
        }

        var past = RecurrenceExpander
            .Expand(e.StartUtc, end, e.TimeZoneId, e.RRule!, exDates, now.AddYears(-5), now)
            .LastOrDefault();
        return past.StartUtc != default ? past.StartUtc : e.StartUtc;
    }

    public async Task<EventDto?> GetByIdAsync(Guid userId, Guid eventId, CancellationToken cancellationToken = default)
    {
        var entity = await db.Events.AsNoTracking().Include(e => e.Reminders).Include(e => e.Attendees).Include(e => e.Category)
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .SingleOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<EventDto> CreateAsync(Guid userId, SaveEventRequest request, CancellationToken cancellationToken = default)
    {
        var calendarId = await ResolveCalendarIdAsync(userId, request.CalendarId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var entity = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = $"{Guid.NewGuid():N}@calendarit",
            CreatedAt = now,
        };
        Apply(entity, request, now);
        entity.CategoryId = await ResolveCategoryIdAsync(userId, request.CategoryId, fallback: null, cancellationToken);
        entity.Reminders = MapReminders(request.Reminders);
        entity.Attendees = MapAttendees(request.Attendees, existing: null);

        db.Events.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(entity).Reference(e => e.Category).LoadAsync(cancellationToken);

        // Invite the guests (no-op without a configured mail account; failures only log).
        await mailer.SendRequestAsync(userId, entity, [.. entity.Attendees], cancellationToken);
        // Guests who are local users get the event straight on their own calendar.
        await delivery.SyncAsync(entity, userId, [], cancellationToken);
        return ToDto(entity);
    }

    public async Task<EventDto?> UpdateAsync(Guid userId, Guid eventId, SaveEventRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await db.Events.Include(e => e.Reminders).Include(e => e.Attendees)
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return null;
        }

        // A provided CalendarId moves the event; null leaves it where it is. Only calendars
        // the user owns are accepted — anything else keeps the current one.
        if (request.CalendarId is { } target && target != entity.CalendarId)
        {
            var owned = await db.Calendars.AnyAsync(
                c => c.Id == target && c.OwnerUserId == userId, cancellationToken);
            if (owned)
            {
                entity.CalendarId = target;
            }
        }

        Apply(entity, request, timeProvider.GetUtcNow().UtcDateTime);
        entity.CategoryId = await ResolveCategoryIdAsync(userId, request.CategoryId, fallback: entity.CategoryId, cancellationToken);
        // Replace the reminder set; orphaned rows are deleted (required FK). The new rows
        // must be marked Added explicitly: they carry pre-set Guid keys, and EF assumes
        // navigation-discovered entities with a set store-generated key already exist
        // (it would issue an UPDATE for a row that was never inserted).
        entity.Reminders.Clear();
        foreach (var reminder in MapReminders(request.Reminders))
        {
            entity.Reminders.Add(reminder);
            db.Entry(reminder).State = EntityState.Added;
        }

        // Attendees: null leaves the set untouched (e.g. drag edits that don't send it);
        // otherwise merge in place — retained guests keep their row (and status), guests
        // no longer listed are removed, new ones are added.
        var removed = new List<Attendee>();
        if (request.Attendees is not null)
        {
            var next = MapAttendees(request.Attendees, entity.Attendees);
            foreach (var old in entity.Attendees
                         .Where(a => !next.Any(n => n.Email.Equals(a.Email, StringComparison.OrdinalIgnoreCase)))
                         .ToList())
            {
                removed.Add(new Attendee { Email = old.Email, Name = old.Name, Status = old.Status });
                entity.Attendees.Remove(old);
            }
            foreach (var added in next
                         .Where(n => !entity.Attendees.Any(a => a.Email.Equals(n.Email, StringComparison.OrdinalIgnoreCase)))
                         .ToList())
            {
                entity.Attendees.Add(added);
                db.Entry(added).State = EntityState.Added; // see reminder note above
            }
        }
        if (entity.Attendees.Count > 0 || removed.Count > 0)
        {
            entity.Sequence++; // guests' calendars use SEQUENCE to spot the newer version
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(entity).Reference(e => e.Category).LoadAsync(cancellationToken);

        // Everyone still invited gets the updated event; the removed get a cancellation.
        await mailer.SendRequestAsync(userId, entity, [.. entity.Attendees], cancellationToken);
        await mailer.SendCancelAsync(userId, entity, removed, cancellationToken);
        // Mirror the change onto local guests' calendars (new copies, edits, and withdrawals).
        await delivery.SyncAsync(entity, userId, removed, cancellationToken);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid eventId, DateTimeOffset? occurrence, CancellationToken cancellationToken = default)
    {
        var entity = await db.Events.Include(e => e.Attendees)
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return false;
        }

        // Exclude a single occurrence of a series (EXDATE) rather than deleting everything.
        if (occurrence is not null && entity.RRule is not null)
        {
            var exDates = ParseExDates(entity.ExDates);
            exDates.Add(TruncateToSeconds(occurrence.Value.UtcDateTime));
            entity.ExDates = FormatExDates(exDates);
            entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            // Propagate the excluded occurrence to local guests' copies.
            await delivery.SyncAsync(entity, userId, [], cancellationToken);
            return true;
        }

        var invited = entity.Attendees.ToList();
        entity.Sequence++; // the cancellation must outrank the last invite
        db.Events.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        await mailer.SendCancelAsync(userId, entity, invited, cancellationToken);
        // Withdraw the event from local guests' calendars too.
        await delivery.RemoveAsync(entity, invited, userId, cancellationToken);
        return true;
    }

    private static void Apply(CalendarEvent entity, SaveEventRequest request, DateTime now)
    {
        entity.Title = request.Title.Trim();
        entity.Description = request.Description;
        entity.Location = request.Location;
        entity.StartUtc = request.Start.UtcDateTime;
        entity.EndUtc = request.End?.UtcDateTime;
        entity.IsAllDay = request.AllDay;
        entity.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone;
        entity.RRule = string.IsNullOrWhiteSpace(request.Recurrence) ? null : request.Recurrence.Trim();
        entity.ExDates = null; // editing the series resets any per-occurrence exclusions
        entity.UpdatedAt = now;
    }

    /// <summary>Display color: the category's color, else the legacy per-event fallback.</summary>
    private static string? DisplayColor(CalendarEvent e) => e.Category?.Color ?? e.Color;

    private static EventDto ToDto(CalendarEvent e) =>
        new(
            e.Id, e.CalendarId, e.Title, e.Description, e.Location, e.CategoryId, DisplayColor(e),
            new DateTimeOffset(DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc)),
            e.EndUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(e.EndUtc.Value, DateTimeKind.Utc)),
            e.IsAllDay, Recurring: e.RRule is not null, Recurrence: e.RRule,
            Reminders: MapReminders(e.Reminders), Attendees: MapAttendees(e.Attendees));

    private static IReadOnlyList<AttendeeDto> MapAttendees(IEnumerable<Attendee> attendees) =>
        attendees
            .OrderBy(a => a.Email, StringComparer.OrdinalIgnoreCase)
            .Select(a => new AttendeeDto(a.Email, a.Name, a.Status.ToString()))
            .ToList();

    /// <summary>Save-request guests → entities: trimmed, deduplicated by email, and keeping
    /// the participation status of guests who were already on the event.</summary>
    private static List<Attendee> MapAttendees(IReadOnlyList<AttendeeInput>? inputs, ICollection<Attendee>? existing)
    {
        if (inputs is null)
        {
            return [];
        }
        var previous = existing?.ToDictionary(a => a.Email, a => a.Status, StringComparer.OrdinalIgnoreCase);
        return inputs
            .Where(i => !string.IsNullOrWhiteSpace(i.Email))
            .Select(i => (Email: i.Email.Trim(), i.Name))
            .DistinctBy(i => i.Email, StringComparer.OrdinalIgnoreCase)
            .Select(i => new Attendee
            {
                Id = Guid.NewGuid(),
                Email = i.Email,
                Name = string.IsNullOrWhiteSpace(i.Name) ? null : i.Name.Trim(),
                Status = previous is not null && previous.TryGetValue(i.Email, out var kept) ? kept : AttendeeStatus.NeedsAction,
            })
            .ToList();
    }

    private static IReadOnlyList<ReminderDto> MapReminders(IEnumerable<Reminder> reminders) =>
        reminders
            .OrderBy(r => r.MinutesBefore)
            .Select(r => new ReminderDto(r.MinutesBefore, r.Channel.ToString()))
            .ToList();

    private static List<Reminder> MapReminders(IReadOnlyList<ReminderInput>? inputs)
    {
        if (inputs is null)
        {
            return [];
        }
        return inputs
            .Select(i => new Reminder
            {
                Id = Guid.NewGuid(),
                MinutesBefore = i.MinutesBefore,
                Channel = Enum.TryParse<ReminderChannel>(i.Channel, ignoreCase: true, out var c) ? c : ReminderChannel.Email,
            })
            .ToList();
    }

    private static HashSet<DateTime> ParseExDates(string? raw)
    {
        var set = new HashSet<DateTime>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateTime.TryParse(line, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                set.Add(TruncateToSeconds(DateTime.SpecifyKind(dt, DateTimeKind.Utc)));
            }
        }
        return set;
    }

    private static string FormatExDates(IEnumerable<DateTime> exDates) =>
        string.Join('\n', exDates.Select(d => d.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));

    private static DateTime TruncateToSeconds(DateTime dt) =>
        new(dt.Ticks - (dt.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    /// <summary>Null clears the category; a category the user owns is assigned; anything
    /// else keeps <paramref name="fallback"/> (the current assignment on update).</summary>
    private async Task<Guid?> ResolveCategoryIdAsync(Guid userId, Guid? requested, Guid? fallback, CancellationToken cancellationToken)
    {
        if (requested is not { } id)
        {
            return null;
        }
        var owned = await db.Categories.AnyAsync(
            c => c.Id == id && c.OwnerUserId == userId, cancellationToken);
        return owned ? id : fallback;
    }

    /// <summary>The calendar a new event lands in: the requested one when the user owns it,
    /// otherwise the default (first) calendar, created on first use.</summary>
    private async Task<Guid> ResolveCalendarIdAsync(Guid userId, Guid? requested, CancellationToken cancellationToken)
    {
        if (requested is { } id)
        {
            var owned = await db.Calendars.AnyAsync(
                c => c.Id == id && c.OwnerUserId == userId, cancellationToken);
            if (owned)
            {
                return id;
            }
        }
        return (await GetOrCreateDefaultCalendarAsync(userId, cancellationToken)).Id;
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
        calendar = new Calendar
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Name = "Personal",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);
        return calendar;
    }
}
