using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

        // Single (non-recurring) events — and edited occurrences, which are one-offs standing in
        // for an instant their series suppresses: filter by range in SQL.
        var singles = db.Events.AsNoTracking().Include(e => e.Reminders).Include(e => e.Attendees).Include(e => e.Category).Include(e => e.Calendar!.DefaultCategory)
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
            // A series starting after the window can't have an occurrence inside it, so it never
            // needs expanding. (The other direction can't be filtered here: whether an old series
            // still runs is encoded in its RRULE, which SQL can't read.)
            var masters = await db.Events.AsNoTracking().Include(e => e.Reminders).Include(e => e.Attendees).Include(e => e.Category).Include(e => e.Calendar!.DefaultCategory)
                .Where(e => e.Calendar!.OwnerUserId == userId && e.RRule != null && e.StartUtc < toUtc)
                .ToListAsync(cancellationToken);

            // Bounded twice over: per series (an hourly rule over a long window) and per response
            // (many series at once), so no single request can be made to build an unbounded list.
            var budget = MaxOccurrencesPerResponse;
            foreach (var m in masters)
            {
                var end = m.EndUtc ?? m.StartUtc.AddHours(1);
                var exDates = ExDates.Parse(m.ExDates);
                var reminders = MapReminders(m.Reminders);
                var attendees = MapAttendees(m.Attendees);
                var occurrences = RecurrenceExpander
                    .Expand(m.StartUtc, end, m.TimeZoneId, m.RRule!, exDates, fromUtc.Value, toUtc.Value, m.IsAllDay)
                    .Take(Math.Min(MaxOccurrencesPerSeries, budget));
                foreach (var occ in occurrences)
                {
                    budget--;
                    results.Add(new EventDto(
                        m.Id, m.CalendarId, m.Title, m.Description, m.Location, m.CategoryId, DisplayColor(m),
                        new DateTimeOffset(occ.StartUtc, TimeSpan.Zero),
                        new DateTimeOffset(occ.EndUtc, TimeSpan.Zero),
                        m.IsAllDay, Recurring: true, Recurrence: m.RRule, Reminders: reminders, Attendees: attendees,
                        InvitationStatus: m.InvitationStatus?.ToString(), OrganizerEmail: m.OrganizerEmail,
                        RecurrenceId: new DateTimeOffset(occ.StartUtc, TimeSpan.Zero),
                        EffectiveCategoryId: EffectiveCategoryId(m)));
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
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var matching = db.Events.AsNoTracking().Include(e => e.Category).Include(e => e.Calendar!.DefaultCategory)
            .Where(e => e.Calendar!.OwnerUserId == userId
                && (e.Title.ToLower().Contains(needle)
                    || (e.Location != null && e.Location.ToLower().Contains(needle))));

        // The result is "the nearest few hits either side of now", so the database can do the
        // narrowing: the soonest `limit` ahead and the latest `limit` behind is a superset of any
        // `limit` we could return. Reading every match instead — which a one-letter query makes
        // the whole calendar — and sorting it here was the expensive part, made worse by search
        // running on every keystroke.
        var upcoming = await matching
            .Where(e => e.RRule == null && e.StartUtc >= now)
            .OrderBy(e => e.StartUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var past = await matching
            .Where(e => e.RRule == null && e.StartUtc < now)
            .OrderByDescending(e => e.StartUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        // Series can't be ordered in SQL — their stored start is the first occurrence, which says
        // nothing about when the next one falls — so they're expanded here. There are far fewer
        // series than events, and the cap stops a pathological calendar from making search crawl.
        var recurring = await matching
            .Where(e => e.RRule != null)
            .OrderByDescending(e => e.StartUtc) // a stable pick when there are more than the cap
            .Take(MaxRecurringSearchCandidates)
            .ToListAsync(cancellationToken);

        var matches = upcoming.Concat(past).Concat(recurring);
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

    /// <summary>Occurrences one series contributes to a range query: an hourly series over the
    /// agenda's three-month window, with room to spare.</summary>
    private const int MaxOccurrencesPerSeries = 3_000;

    /// <summary>Occurrences across all series in one range query.</summary>
    private const int MaxOccurrencesPerResponse = 20_000;

    /// <summary>How many series a single search will expand. Bounds the work when a short query
    /// matches a large calendar.</summary>
    private const int MaxRecurringSearchCandidates = 200;

    /// <summary>Ceiling on occurrences walked per series. An RRULE arrives from outside (import,
    /// CalDAV, an emailed invitation) and may repeat by the second, so the walk needs an end.</summary>
    private const int MaxOccurrenceScan = 5_000;

    // For a recurring master, the date we surface in search: the next occurrence within the
    // next 2 years, else the most recent occurrence in the past 5 years, else the series start.
    private static DateTime RepresentativeOccurrenceUtc(CalendarEvent e, DateTime now)
    {
        var end = e.EndUtc ?? e.StartUtc.AddHours(1);
        var exDates = ExDates.Parse(e.ExDates);

        // Cheap: expansion is lazy, so this stops at the first hit.
        var upcoming = RecurrenceExpander
            .Expand(e.StartUtc, end, e.TimeZoneId, e.RRule!, exDates, now, now.AddYears(2), e.IsAllDay)
            .FirstOrDefault();
        if (upcoming.StartUtc != default)
        {
            return upcoming.StartUtc;
        }

        // Finding the *last* one has to walk them all, hence the cap.
        var past = RecurrenceExpander
            .Expand(e.StartUtc, end, e.TimeZoneId, e.RRule!, exDates, now.AddYears(-5), now, e.IsAllDay)
            .Take(MaxOccurrenceScan)
            .LastOrDefault();
        return past.StartUtc != default ? past.StartUtc : e.StartUtc;
    }

    public async Task<EventDto?> GetByIdAsync(Guid userId, Guid eventId, CancellationToken cancellationToken = default)
    {
        var entity = await db.Events.AsNoTracking().Include(e => e.Reminders).Include(e => e.Attendees).Include(e => e.Category).Include(e => e.Calendar!.DefaultCategory)
            .AsSplitQuery()
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
        await LoadDisplayAsync(entity, cancellationToken);

        // Invite the guests (no-op without a configured mail account; failures only log).
        await mailer.SendRequestAsync(userId, entity, [.. entity.Attendees], cancellationToken);
        // Guests who are local users get the event straight on their own calendar.
        await delivery.SyncAsync(entity, userId, [], cancellationToken);
        return ToDto(entity);
    }

    public async Task<EventDto?> UpdateAsync(Guid userId, Guid eventId, SaveEventRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await SeriesQuery()
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return null;
        }

        // An edited occurrence saved from its own editor: it's still one occurrence of its series.
        if (entity.SeriesMasterId is { } masterId)
        {
            return await UpsertOccurrenceAsync(
                userId, masterId, new DateTimeOffset(DateTime.SpecifyKind(entity.RecurrenceIdUtc!.Value, DateTimeKind.Utc)),
                request, cancellationToken);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

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

        var recurrenceMoved = Apply(entity, request, now);
        entity.CategoryId = await ResolveCategoryIdAsync(userId, request.CategoryId, fallback: entity.CategoryId, cancellationToken);
        ReplaceReminders(entity, request.Reminders);

        // Edited occurrences follow their series: into its new calendar, and out when the series
        // moved or stopped repeating — their original instants no longer exist.
        foreach (var o in entity.Overrides.ToList())
        {
            if (recurrenceMoved || entity.RRule is null)
            {
                entity.Overrides.Remove(o);
                db.Events.Remove(o);
            }
            else
            {
                o.CalendarId = entity.CalendarId;
            }
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
                db.Entry(added).State = EntityState.Added; // see ReplaceReminders
                AddOnce(entity.Attendees, added);
            }
        }
        if (entity.Attendees.Count > 0 || removed.Count > 0)
        {
            entity.Sequence++; // guests' calendars use SEQUENCE to spot the newer version
        }

        await db.SaveChangesAsync(cancellationToken);
        await LoadDisplayAsync(entity, cancellationToken);

        // Everyone still invited gets the updated event; the removed get a cancellation.
        await mailer.SendRequestAsync(userId, entity, [.. entity.Attendees], cancellationToken);
        await mailer.SendCancelAsync(userId, entity, removed, cancellationToken);
        // Mirror the change onto local guests' calendars (new copies, edits, and withdrawals).
        await delivery.SyncAsync(entity, userId, removed, cancellationToken);
        return ToDto(entity);
    }

    public async Task<EventDto?> UpsertOccurrenceAsync(
        Guid userId, Guid seriesId, DateTimeOffset occurrence, SaveEventRequest request, CancellationToken cancellationToken = default)
    {
        var master = await SeriesQuery()
            .Where(e => e.Id == seriesId && e.Calendar!.OwnerUserId == userId && e.SeriesMasterId == null && e.RRule != null)
            .SingleOrDefaultAsync(cancellationToken);
        if (master is null)
        {
            return null;
        }

        var rid = ExDates.TruncateToSeconds(occurrence.UtcDateTime);
        var row = master.Overrides.FirstOrDefault(o => o.RecurrenceIdUtc is { } r && ExDates.TruncateToSeconds(r) == rid);
        if (row is null && !IsLiveOccurrence(master, rid))
        {
            return null; // not an instant this series produces, or one already deleted
        }
        if (request.End is { } requestedEnd && requestedEnd < request.Start)
        {
            throw new InvalidInputException("The end can't be before the start.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            row = new CalendarEvent
            {
                Id = Guid.NewGuid(),
                SeriesMasterId = master.Id,
                RecurrenceIdUtc = rid,
                CreatedAt = now,
            };
            db.Events.Add(row); // before joining the navigation — see ReplaceReminders
            AddOnce(master.Overrides, row);
            var suppressed = ExDates.Parse(master.ExDates);
            suppressed.Add(rid);
            master.ExDates = ExDates.Format(suppressed.Order());
        }

        row.Title = request.Title.Trim();
        row.Description = request.Description;
        row.Location = request.Location;
        row.StartUtc = request.Start.UtcDateTime;
        row.EndUtc = request.End?.UtcDateTime;
        row.IsAllDay = request.AllDay;
        row.TimeZoneId = TimeZones.Normalize(request.TimeZone);
        row.CategoryId = await ResolveCategoryIdAsync(userId, request.CategoryId, fallback: row.CategoryId ?? master.CategoryId, cancellationToken);
        row.UpdatedAt = now;
        SeriesWriter.InheritFromMaster(row, master);
        ReplaceReminders(row, request.Reminders);

        await TouchSeriesAsync(userId, master, now, cancellationToken);
        await LoadDisplayAsync(row, cancellationToken);
        return ToDto(row);
    }

    public async Task<bool> ResetOccurrenceAsync(
        Guid userId, Guid seriesId, DateTimeOffset occurrence, CancellationToken cancellationToken = default)
    {
        var master = await SeriesQuery()
            .Where(e => e.Id == seriesId && e.Calendar!.OwnerUserId == userId && e.SeriesMasterId == null && e.RRule != null)
            .SingleOrDefaultAsync(cancellationToken);
        if (master is null)
        {
            return false;
        }

        var rid = ExDates.TruncateToSeconds(occurrence.UtcDateTime);
        foreach (var o in master.Overrides.Where(o => o.RecurrenceIdUtc is { } r && ExDates.TruncateToSeconds(r) == rid).ToList())
        {
            master.Overrides.Remove(o);
            db.Events.Remove(o);
        }
        var suppressed = ExDates.Parse(master.ExDates);
        suppressed.Remove(rid);
        master.ExDates = suppressed.Count == 0 ? null : ExDates.Format(suppressed.Order());

        await TouchSeriesAsync(userId, master, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        return true;
    }

    public async Task<EventDto?> RespondToInvitationAsync(
        Guid userId, Guid eventId, string status, CancellationToken cancellationToken = default)
    {
        // Only a real RSVP is meaningful here; "NeedsAction" (or anything unrecognised) is rejected.
        if (!Enum.TryParse<AttendeeStatus>(status, ignoreCase: true, out var parsed)
            || parsed == AttendeeStatus.NeedsAction)
        {
            return null;
        }

        var target = await db.Events.AsNoTracking()
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .Select(e => new { e.Id, e.SeriesMasterId })
            .SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return null;
        }

        // An RSVP answers the whole invitation, so it is recorded on the series and its
        // occurrences alike, whichever of them it was given on.
        var entity = await SeriesQuery().Include(e => e.Category).Include(e => e.Calendar!.DefaultCategory)
            .SingleAsync(e => e.Id == (target.SeriesMasterId ?? target.Id), cancellationToken);
        // Only a received invitation (non-null status) can be responded to — never the user's own event.
        if (entity.InvitationStatus is null)
        {
            return null;
        }

        if (entity.InvitationStatus != parsed)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            entity.InvitationStatus = parsed;
            entity.UpdatedAt = now;
            foreach (var o in entity.Overrides)
            {
                o.InvitationStatus = parsed;
                o.UpdatedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // Tell the organizer, even if the status was already set (they may have missed the first
        // reply). A missing mail account / send failure is logged, not fatal — the local RSVP stands.
        await mailer.SendReplyAsync(userId, entity, parsed, cancellationToken);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid eventId, DateTimeOffset? occurrence, CancellationToken cancellationToken = default)
    {
        var entity = await SeriesQuery()
            .Where(e => e.Id == eventId && e.Calendar!.OwnerUserId == userId)
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // An edited occurrence's own row: deleting it deletes that occurrence. Its instant is
        // already suppressed on the series, so removing the row is all it takes.
        if (entity.SeriesMasterId is { } masterId)
        {
            var series = await SeriesQuery().SingleAsync(e => e.Id == masterId, cancellationToken);
            var row = series.Overrides.Single(o => o.Id == entity.Id);
            series.Overrides.Remove(row);
            db.Events.Remove(row);
            await TouchSeriesAsync(userId, series, now, cancellationToken);
            return true;
        }

        // Exclude a single occurrence of a series (EXDATE) rather than deleting everything —
        // along with its edited version, if it has one.
        if (occurrence is not null && entity.RRule is not null)
        {
            var rid = ExDates.TruncateToSeconds(occurrence.Value.UtcDateTime);
            foreach (var o in entity.Overrides.Where(o => o.RecurrenceIdUtc is { } r && ExDates.TruncateToSeconds(r) == rid).ToList())
            {
                entity.Overrides.Remove(o);
                db.Events.Remove(o);
            }
            var exDates = ExDates.Parse(entity.ExDates);
            exDates.Add(rid);
            entity.ExDates = ExDates.Format(exDates.Order());
            await TouchSeriesAsync(userId, entity, now, cancellationToken);
            return true;
        }

        var invited = entity.Attendees.ToList();
        entity.Sequence++; // the cancellation must outrank the last invite
        db.Events.Remove(entity); // its overrides go with it (cascade)
        await db.SaveChangesAsync(cancellationToken);
        await mailer.SendCancelAsync(userId, entity, invited, cancellationToken);
        // Withdraw the event from local guests' calendars too.
        await delivery.RemoveAsync(entity, invited, userId, cancellationToken);
        return true;
    }

    /// <summary>An event with everything a series write touches: reminders, guests, and its
    /// edited occurrences with their reminders.</summary>
    private IQueryable<CalendarEvent> SeriesQuery() =>
        db.Events
            .Include(e => e.Reminders)
            .Include(e => e.Attendees)
            .Include(e => e.Overrides).ThenInclude(o => o.Reminders)
            .AsSplitQuery();

    /// <summary>Whether <paramref name="rid"/> is an occurrence the series currently shows.</summary>
    private static bool IsLiveOccurrence(CalendarEvent master, DateTime rid)
    {
        var end = master.EndUtc ?? master.StartUtc.AddHours(1);
        return RecurrenceExpander
            .Expand(master.StartUtc, end, master.TimeZoneId, master.RRule!, ExDates.Parse(master.ExDates),
                rid.AddSeconds(-1), rid.AddSeconds(1), master.IsAllDay)
            .Any(o => ExDates.TruncateToSeconds(o.StartUtc) == rid);
    }

    /// <summary>
    /// Saves a change made to one occurrence and lets everyone holding the series know: the
    /// series' modification time moves (a CalDAV client sees a new ETag on the one resource that
    /// carries every occurrence), guests get the updated series, and local guests' copies follow.
    /// </summary>
    private async Task TouchSeriesAsync(Guid userId, CalendarEvent master, DateTime now, CancellationToken cancellationToken)
    {
        master.UpdatedAt = now;
        if (master.Attendees.Count > 0)
        {
            master.Sequence++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await mailer.SendRequestAsync(userId, master, [.. master.Attendees], cancellationToken);
        await delivery.SyncAsync(master, userId, [], cancellationToken);
    }

    /// <summary>
    /// Replaces a row's reminders with the request's; orphaned rows are deleted (required FK).
    /// New rows are marked Added explicitly before they join the navigation: they carry preset
    /// Guid keys, and EF takes a navigation-discovered entity with a set key for an existing row
    /// (it would issue an UPDATE for a row that was never inserted).
    /// </summary>
    private void ReplaceReminders(CalendarEvent entity, IReadOnlyList<ReminderInput>? inputs)
    {
        entity.Reminders.Clear();
        foreach (var reminder in MapReminders(inputs))
        {
            reminder.EventId = entity.Id;
            db.Entry(reminder).State = EntityState.Added;
            AddOnce(entity.Reminders, reminder); // tracking it may already have linked it
        }
    }

    /// <summary>
    /// Adds <paramref name="item"/> unless it is already there. Tracking a new row with its
    /// foreign key set makes EF link it into the parent's collection by itself; adding it again
    /// would list it twice in memory (one row in the database, two in the response).
    /// </summary>
    internal static void AddOnce<T>(ICollection<T> collection, T item)
    {
        if (!collection.Contains(item))
        {
            collection.Add(item);
        }
    }

    /// <summary>Copies the request onto the entity; returns whether the series moved (its rule,
    /// anchor or all-day-ness changed), which invalidates its exclusions and edited occurrences.</summary>
    private static bool Apply(CalendarEvent entity, SaveEventRequest request, DateTime now)
    {
        // Every stored rule is re-parsed on every read — by this user's calendar and by the
        // reminder job that serves everyone — so one that doesn't parse is refused here.
        if (!RecurrenceRules.TryNormalize(request.Recurrence, out var rrule, out var ruleError))
        {
            throw new InvalidInputException(ruleError!);
        }
        if (request.End is { } requestedEnd && requestedEnd < request.Start)
        {
            throw new InvalidInputException("The end can't be before the start.");
        }
        // Exclusions are stored as absolute instants, so they only stop lining up with the
        // series when the rule or its anchor moves — a rename or a new location must leave
        // them alone, or deleting one occurrence and later editing the series brings it back.
        var recurrenceMoved = rrule != entity.RRule
            || request.Start.UtcDateTime != entity.StartUtc
            || request.AllDay != entity.IsAllDay;

        entity.Title = request.Title.Trim();
        entity.Description = request.Description;
        entity.Location = request.Location;
        entity.StartUtc = request.Start.UtcDateTime;
        entity.EndUtc = request.End?.UtcDateTime;
        entity.IsAllDay = request.AllDay;
        // A zone id we can't resolve is dropped rather than stored: it would otherwise throw
        // out of every later expansion, i.e. one bad save breaks the whole calendar view.
        entity.TimeZoneId = TimeZones.Normalize(request.TimeZone);
        entity.RRule = rrule;
        if (recurrenceMoved)
        {
            entity.ExDates = null; // the old exclusions no longer refer to real occurrences
        }
        entity.UpdatedAt = now;
        return recurrenceMoved;
    }

    /// <summary>Display color: the event's own category, else its calendar's default category, else
    /// the legacy per-event color. Needs Category and Calendar.DefaultCategory loaded.</summary>
    private static string? DisplayColor(CalendarEvent e) =>
        e.Category?.Color ?? (e.CategoryId is null ? e.Calendar?.DefaultCategory?.Color : null) ?? e.Color;

    /// <summary>The category the event shows as: its own, else its calendar's default.</summary>
    private static Guid? EffectiveCategoryId(CalendarEvent e) => e.CategoryId ?? e.Calendar?.DefaultCategoryId;

    /// <summary>Loads what <see cref="ToDto"/> needs for color after a write.</summary>
    private async Task LoadDisplayAsync(CalendarEvent entity, CancellationToken cancellationToken)
    {
        await db.Entry(entity).Reference(e => e.Category).LoadAsync(cancellationToken);
        await db.Entry(entity).Reference(e => e.Calendar).LoadAsync(cancellationToken);
        if (entity.Calendar is { } calendar)
        {
            await db.Entry(calendar).Reference(c => c.DefaultCategory).LoadAsync(cancellationToken);
        }
    }

    private static EventDto ToDto(CalendarEvent e) =>
        new(
            e.Id, e.CalendarId, e.Title, e.Description, e.Location, e.CategoryId, DisplayColor(e),
            new DateTimeOffset(DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc)),
            e.EndUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(e.EndUtc.Value, DateTimeKind.Utc)),
            e.IsAllDay, Recurring: e.RRule is not null || e.SeriesMasterId is not null, Recurrence: e.RRule,
            Reminders: MapReminders(e.Reminders), Attendees: MapAttendees(e.Attendees),
            InvitationStatus: e.InvitationStatus?.ToString(), OrganizerEmail: e.OrganizerEmail,
            SeriesMasterId: e.SeriesMasterId,
            RecurrenceId: e.RecurrenceIdUtc is { } rid ? new DateTimeOffset(DateTime.SpecifyKind(rid, DateTimeKind.Utc)) : null,
            EffectiveCategoryId: EffectiveCategoryId(e));

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
        return (await DefaultCalendar.GetOrCreateAsync(db, timeProvider, userId, cancellationToken)).Id;
    }
}
