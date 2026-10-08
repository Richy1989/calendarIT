using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;
using ICalCalendar = Ical.Net.Calendar;
using ICalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>iCalendar (.ics) import/export using Ical.Net, mapping to our event model.</summary>
public sealed class CalendarIoService(AppDbContext db, TimeProvider timeProvider) : ICalendarIoService
{
    /// <summary>Events one import may create. A personal calendar's whole history fits; a file
    /// far beyond it is a mistake or an attack, and is refused before anything is written.</summary>
    private const int MaxImportEvents = 50_000;

    public async Task<string> ExportAsync(
        Guid userId, IReadOnlyCollection<Guid>? calendarIds = null, CancellationToken cancellationToken = default)
    {
        // Series are exported whole: the master, then its edited occurrences under the same UID.
        var query = WithSeries(db.Events.AsNoTracking())
            .Where(e => e.Calendar!.OwnerUserId == userId && e.SeriesMasterId == null);
        if (calendarIds is { Count: > 0 })
        {
            query = query.Where(e => calendarIds.Contains(e.CalendarId));
        }
        var events = await query
            .OrderBy(e => e.StartUtc)
            .ToListAsync(cancellationToken);

        var cal = new ICalCalendar { ProductId = "-//CalendarIT//EN" };
        foreach (var e in events)
        {
            foreach (var ve in ICalEventMapper.ToICalEvents(e, e.Overrides, e.Calendar?.DefaultCategory))
            {
                cal.Events.Add(ve);
            }
        }
        return new CalendarSerializer().SerializeToString(cal)!;
    }

    public async Task<string?> ExportEventAsync(
        Guid userId, Guid eventId, CancellationToken cancellationToken = default)
    {
        var ev = await WithSeries(db.Events.AsNoTracking())
            .SingleOrDefaultAsync(x => x.Id == eventId && x.Calendar!.OwnerUserId == userId, cancellationToken);
        if (ev is null)
        {
            return null;
        }

        // Same mapper + serializer as the bulk export, so a copied event is byte-for-byte the
        // iCalendar any other client would get on a full .ics export. An edited occurrence copies
        // as the one-off it is.
        var cal = new ICalCalendar { ProductId = "-//CalendarIT//EN" };
        var inherited = ev.Calendar?.DefaultCategory;
        var vevents = ev.SeriesMasterId is null
            ? ICalEventMapper.ToICalEvents(ev, ev.Overrides, inherited)
            : [ICalEventMapper.ToICalEvent(ev, inherited)];
        foreach (var ve in vevents)
        {
            cal.Events.Add(ve);
        }
        return new CalendarSerializer().SerializeToString(cal);
    }

    public async Task<ImportResult> ImportAsync(
        Guid userId, string ics, Guid? calendarId = null, string? newCalendarName = null,
        CancellationToken cancellationToken = default, Guid? newCalendarCategoryId = null)
    {
        // Parse before anything is created, so a file that isn't iCalendar leaves no empty
        // "new calendar" behind — and comes back as a 400 the user can act on, not a 500.
        ICalCalendar? parsed;
        try
        {
            parsed = ICalCalendar.Load(ics);
        }
        catch (Exception)
        {
            throw new InvalidInputException("That file isn't a valid iCalendar (.ics) file.");
        }
        if (parsed is null)
        {
            return new ImportResult(0, 0);
        }
        if (parsed.Events.Count > MaxImportEvents)
        {
            throw new InvalidInputException($"That file holds more than {MaxImportEvents:N0} events — split it and import the parts.");
        }

        var calendar = await ResolveTargetCalendarAsync(userId, calendarId, newCalendarName, newCalendarCategoryId, cancellationToken);

        var existingUids = await db.Events
            .Where(e => e.Calendar!.OwnerUserId == userId && e.SeriesMasterId == null)
            .Select(e => e.Uid)
            .ToListAsync(cancellationToken);
        var known = new HashSet<string>(existingUids, StringComparer.OrdinalIgnoreCase);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var categories = await db.Categories
            .Where(c => c.OwnerUserId == userId)
            .ToListAsync(cancellationToken);
        var writer = new SeriesWriter(db);
        int imported = 0, skipped = 0;

        // One resource per UID: a series' master and its overrides arrive as separate VEVENTs.
        // A VEVENT without a UID is its own event.
        var groups = parsed.Events
            .GroupBy(ve => string.IsNullOrWhiteSpace(ve.Uid) ? $"{Guid.NewGuid():N}@calendarit" : ve.Uid);
        foreach (var group in groups)
        {
            var (master, overrides) = SeriesWriter.Split(group);
            var primary = master ?? overrides.FirstOrDefault();
            var uid = group.Key;
            if (primary is null || uid.Length > ICalEventMapper.MaxUidLength || !known.Add(uid))
            {
                skipped++;
                continue;
            }

            // A CATEGORIES name the user doesn't have yet becomes a new category, colored
            // from the incoming COLOR when present — so imports keep their grouping.
            EnsureCategoryExists(primary, userId, categories, now);
            // A file of overrides without their master is kept as a one-off. Events without a
            // category of their own take the calendar's default, if it has one.
            writer.Write(null, primary, master is null ? [] : overrides, calendar.Id, uid, now, categories,
                replaceAlarms: true, inheritedCategoryId: calendar.DefaultCategoryId);
            imported++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ImportResult(imported, skipped);
    }

    private static IQueryable<CalendarEvent> WithSeries(IQueryable<CalendarEvent> events) =>
        events
            .Include(e => e.Calendar).ThenInclude(c => c!.DefaultCategory)
            .Include(e => e.Category)
            .Include(e => e.Reminders)
            .Include(e => e.Overrides).ThenInclude(o => o.Category)
            .Include(e => e.Overrides).ThenInclude(o => o.Reminders)
            .AsSplitQuery();

    /// <summary>Creates (stages) a category for an incoming CATEGORIES name the user
    /// doesn't have yet, colored from the VEVENT's COLOR when present.</summary>
    private void EnsureCategoryExists(ICalEvent ve, Guid userId, List<Category> categories, DateTime now)
    {
        var name = ICalEventMapper.ReadCategoryName(ve);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100
            || categories.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var category = new Category
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Name = name,
            Color = ICalEventMapper.ReadColorHex(ve) ?? "#708090", // slategray default
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Categories.Add(category);
        categories.Add(category);
    }

    /// <summary>Where imported events land: a fresh calendar when a name was given (with the given
    /// default category, if it's the user's), else the requested calendar when the user owns it,
    /// else the default calendar.</summary>
    private async Task<Calendar> ResolveTargetCalendarAsync(
        Guid userId, Guid? calendarId, string? newCalendarName, Guid? newCalendarCategoryId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(newCalendarName))
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var name = newCalendarName.Trim();
            if (newCalendarCategoryId is { } categoryId
                && !await db.Categories.AnyAsync(c => c.Id == categoryId && c.OwnerUserId == userId, cancellationToken))
            {
                throw new InvalidInputException("That category doesn't exist.");
            }
            var created = new Calendar
            {
                Id = Guid.NewGuid(),
                OwnerUserId = userId,
                Name = name.Length > 200 ? name[..200] : name,
                DefaultCategoryId = newCalendarCategoryId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Calendars.Add(created);
            await db.SaveChangesAsync(cancellationToken);
            return created;
        }

        if (calendarId is { } id)
        {
            var owned = await db.Calendars
                .SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == userId, cancellationToken);
            if (owned is not null)
            {
                return owned;
            }
        }

        return await DefaultCalendar.GetOrCreateAsync(db, timeProvider, userId, cancellationToken);
    }

}
