using System.Security.Cryptography;
using System.Text;
using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Calendar = CalendarIT.Domain.Calendar;
using ICalCalendar = Ical.Net.Calendar;
using ICalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Subscribed calendars: a read-only calendar mirroring an .ics feed — a holiday calendar, a
/// sports schedule, a colleague's published calendar. The feed is outside input like any import
/// (fetched by <see cref="CalendarFeedFetcher"/>, written through <see cref="SeriesWriter"/>,
/// which clips and validates), and it is mirrored by UID: what appears is added, what changes is
/// updated, what disappears is removed. An unchanged event keeps its modification time, so phones
/// don't re-download the whole calendar after every refresh.
/// </summary>
public sealed class CalendarSubscriptionService(
    AppDbContext db,
    TimeProvider timeProvider,
    CalendarFeedFetcher fetcher,
    ILogger<CalendarSubscriptionService> logger) : ICalendarSubscriptionService
{
    /// <summary>Events kept per subscription; a feed with more is refused, not truncated.</summary>
    public const int MaxEvents = 20_000;

    /// <summary>Subscriptions refreshed per job run, so one run stays short whatever the count.</summary>
    private const int BatchSize = 20;

    public async Task<CalendarDto> SubscribeAsync(Guid userId, SubscribeCalendarRequest request, CancellationToken cancellationToken = default)
    {
        var url = CalendarFeedFetcher.Normalize(request.Url)
            ?? throw new InvalidInputException("That isn't a web address of a calendar (https://… or webcal://…) on the public internet.");
        var refresh = CheckedRefresh(request.RefreshMinutes);
        if (request.DefaultCategoryId is { } categoryId
            && !await db.Categories.AnyAsync(c => c.Id == categoryId && c.OwnerUserId == userId, cancellationToken))
        {
            throw new InvalidInputException("That category doesn't exist.");
        }

        // Fetched before anything is stored: a wrong address leaves no empty calendar behind.
        var result = await fetcher.FetchAsync(url, null, null, cancellationToken);
        if (result is FeedFetchResult.Failed failed)
        {
            throw new InvalidInputException(failed.Message);
        }
        var fetched = (FeedFetchResult.Fetched)result;
        var parsed = Parse(fetched.Ics) ?? throw new InvalidInputException("That address didn't return a readable calendar (.ics) file.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var name = request.Name?.Trim() is { Length: > 0 } given ? given : FeedName(parsed) ?? new Uri(url).Host;
        var calendar = new Calendar
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Name = name.Length > 200 ? name[..200] : name,
            DefaultCategoryId = request.DefaultCategoryId,
            SourceUrl = url,
            RefreshMinutes = refresh,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Calendars.Add(calendar);
        Mirror(calendar, parsed, existing: [], now);
        Succeeded(calendar, fetched, now);
        await db.SaveChangesAsync(cancellationToken);
        return await new CalendarService(db, timeProvider).ToDtoAsync(calendar, cancellationToken);
    }

    public async Task<CalendarDto?> RefreshAsync(Guid userId, Guid calendarId, CancellationToken cancellationToken = default)
    {
        var calendar = await db.Calendars.SingleOrDefaultAsync(
            c => c.Id == calendarId && c.OwnerUserId == userId && c.SourceUrl != null, cancellationToken);
        if (calendar is null)
        {
            return null;
        }
        // Asked for by hand: fetch in full, even if the server would say "not modified".
        calendar.SourceETag = null;
        calendar.SourceLastModified = null;
        await RefreshOneAsync(calendar, cancellationToken);
        return await new CalendarService(db, timeProvider).ToDtoAsync(calendar, cancellationToken);
    }

    public async Task<CalendarDto?> SetRefreshMinutesAsync(Guid userId, Guid calendarId, int minutes, CancellationToken cancellationToken = default)
    {
        var calendar = await db.Calendars.SingleOrDefaultAsync(
            c => c.Id == calendarId && c.OwnerUserId == userId && c.SourceUrl != null, cancellationToken);
        if (calendar is null)
        {
            return null;
        }
        calendar.RefreshMinutes = CheckedRefresh(minutes);
        calendar.NextFetchUtc = (calendar.LastFetchedUtc ?? timeProvider.GetUtcNow().UtcDateTime).AddMinutes(minutes);
        await db.SaveChangesAsync(cancellationToken);
        return await new CalendarService(db, timeProvider).ToDtoAsync(calendar, cancellationToken);
    }

    /// <summary>The job's work: the subscriptions whose refresh is due, oldest first. One that
    /// fails is recorded on its calendar and the rest go on.</summary>
    public async Task<int> RefreshDueAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var due = await db.Calendars
            .Where(c => c.SourceUrl != null && (c.NextFetchUtc == null || c.NextFetchUtc <= now))
            .OrderBy(c => c.NextFetchUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var calendar in due)
        {
            try
            {
                await RefreshOneAsync(calendar, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Refreshing subscribed calendar {CalendarId} failed", calendar.Id);
                db.ChangeTracker.Clear();
                await db.Calendars.Where(c => c.Id == calendar.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.LastFetchError, "The calendar couldn't be read.")
                    .SetProperty(c => c.NextFetchUtc, now.AddMinutes(calendar.RefreshMinutes ?? 360)), cancellationToken);
            }
        }
        return due.Count;
    }

    private async Task RefreshOneAsync(Calendar calendar, CancellationToken cancellationToken)
    {
        var result = await fetcher.FetchAsync(calendar.SourceUrl!, calendar.SourceETag, calendar.SourceLastModified, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        calendar.NextFetchUtc = now.AddMinutes(calendar.RefreshMinutes ?? 360);
        switch (result)
        {
            case FeedFetchResult.NotModified:
                calendar.LastFetchedUtc = now;
                calendar.LastFetchError = null;
                break;
            case FeedFetchResult.Failed failed:
                calendar.LastFetchError = failed.Message.Length > 500 ? failed.Message[..500] : failed.Message;
                break;
            case FeedFetchResult.Fetched fetched:
                var parsed = Parse(fetched.Ics);
                if (parsed is null)
                {
                    calendar.LastFetchError = "The calendar file couldn't be read.";
                    break;
                }
                if (parsed.Events.Count > MaxEvents)
                {
                    calendar.LastFetchError = $"The calendar holds more than {MaxEvents:N0} events.";
                    break;
                }
                var existing = await db.Events
                    .Include(e => e.Reminders)
                    .Include(e => e.Overrides).ThenInclude(o => o.Reminders)
                    .Where(e => e.CalendarId == calendar.Id && e.SeriesMasterId == null)
                    .AsSplitQuery()
                    .ToListAsync(cancellationToken);
                Mirror(calendar, parsed, existing, now);
                Succeeded(calendar, fetched, now);
                break;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Succeeded(Calendar calendar, FeedFetchResult.Fetched fetched, DateTime now)
    {
        calendar.LastFetchedUtc = now;
        calendar.LastFetchError = null;
        calendar.NextFetchUtc = now.AddMinutes(calendar.RefreshMinutes ?? 360);
        calendar.SourceETag = fetched.ETag is { Length: <= 200 } etag ? etag : null;
        calendar.SourceLastModified = fetched.LastModified is { Length: <= 100 } modified ? modified : null;
    }

    /// <summary>Makes the calendar's events what the feed says, by UID. Staged, not saved.</summary>
    private void Mirror(Calendar calendar, ICalCalendar parsed, List<CalendarEvent> existing, DateTime now)
    {
        var byUid = existing.ToDictionary(e => e.Uid, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var writer = new SeriesWriter(db);

        foreach (var group in parsed.Events.GroupBy(UidOf))
        {
            var uid = group.Key;
            if (uid.Length > ICalEventMapper.MaxUidLength || !seen.Add(uid))
            {
                continue;
            }
            var (master, overrides) = SeriesWriter.Split(group);
            var primary = master ?? overrides.FirstOrDefault();
            if (primary is null)
            {
                continue;
            }
            byUid.TryGetValue(uid, out var row);
            var before = row?.UpdatedAt;
            // No categories are made from the feed's CATEGORIES (a busy feed would fill the
            // user's list); its events take the calendar's default category instead. Alarms are
            // the feed author's, not the user's: none are copied.
            var written = writer.Write(row, primary, master is null ? [] : overrides, calendar.Id, uid, now,
                categories: null, replaceAlarms: false, inheritedCategoryId: calendar.DefaultCategoryId);
            if (before is { } unchanged && !HasChanges(written))
            {
                KeepModificationTime(written, unchanged);
            }
        }

        foreach (var gone in existing.Where(e => !seen.Contains(e.Uid)))
        {
            db.Events.Remove(gone); // its edited occurrences go with it
        }
        if (existing.Count > 0 || seen.Count > 0)
        {
            calendar.UpdatedAt = now;
        }
    }

    /// <summary>Whether a rewrite changed anything but its modification times.</summary>
    private bool HasChanges(CalendarEvent master)
    {
        db.ChangeTracker.DetectChanges();
        return db.ChangeTracker.Entries()
            .Where(e => e.Entity is CalendarEvent ev && (ev.Id == master.Id || ev.SeriesMasterId == master.Id)
                     || e.Entity is Reminder r && r.EventId == master.Id)
            .Any(e => e.State is EntityState.Added or EntityState.Deleted
                   || (e.State == EntityState.Modified
                       && e.Properties.Any(p => p.IsModified && p.Metadata.Name != nameof(CalendarEvent.UpdatedAt))));
    }

    private void KeepModificationTime(CalendarEvent master, DateTime updatedAt)
    {
        foreach (var entry in db.ChangeTracker.Entries<CalendarEvent>()
                     .Where(e => e.Entity.Id == master.Id || e.Entity.SeriesMasterId == master.Id))
        {
            var property = entry.Property(e => e.UpdatedAt);
            property.CurrentValue = property.OriginalValue;
            property.IsModified = false;
        }
        master.UpdatedAt = updatedAt;
    }

    /// <summary>A VEVENT's UID — or, for one without (they exist), a stable stand-in made from what
    /// it is, so it keeps its identity from one refresh to the next.</summary>
    private static string UidOf(ICalEvent ve)
    {
        if (!string.IsNullOrWhiteSpace(ve.Uid))
        {
            return ve.Uid;
        }
        var key = $"{ve.Start?.Value:O}|{ve.Summary}|{ve.RecurrenceIdentifier?.StartTime?.Value:O}";
        return $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]}@feed";
    }

    private static ICalCalendar? Parse(string ics)
    {
        try
        {
            return ICalCalendar.Load(ics);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The feed's own name (X-WR-CALNAME), when it gives a sensible one.</summary>
    private static string? FeedName(ICalCalendar parsed)
    {
        var name = parsed.Properties.FirstOrDefault(p => string.Equals(p.Name, "X-WR-CALNAME", StringComparison.OrdinalIgnoreCase))?.Value?.ToString()?.Trim();
        return string.IsNullOrEmpty(name) ? null : new string(name.Where(c => !char.IsControl(c)).ToArray());
    }

    private static int CheckedRefresh(int minutes) =>
        SubscribeCalendarRequest.RefreshChoices.Contains(minutes)
            ? minutes
            : throw new InvalidInputException("Refresh every 1, 6 or 12 hours, or once a day.");
}
