using System.Globalization;
using System.Security.Claims;
using System.Xml.Linq;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Persistence;
using Ical.Net.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;
using TaskItem = CalendarIT.Domain.TaskItem;
using DomainEvent = CalendarIT.Domain.CalendarEvent;
using ICalCalendar = Ical.Net.Calendar;

namespace CalendarIT.CalDav;

/// <summary>
/// Minimal CalDAV server (RFC 4791): discovery (principal → calendar-home → calendars),
/// PROPFIND listings with ETags/CTag, calendar-query / calendar-multiget REPORTs, and
/// GET/PUT/DELETE of single VEVENT resources, and incremental sync (RFC 6578 sync-collection):
/// a client sends the token of its last sync and gets back only what changed or went since.
///
/// Layout (hrefs are always absolute paths):
///   /dav/                     → root; advertises the current-user-principal
///   /dav/principal/           → principal; advertises the calendar-home-set
///   /dav/calendars/           → home; children are the user's calendars
///   /dav/calendars/{id}/      → one calendar; children are {uid}.ics event resources
/// </summary>
public sealed class CalDavHandler(AppDbContext db, TimeProvider timeProvider, QuotaOptions? quotaOptions = null)
{
    private readonly QuotaOptions quotas = quotaOptions ?? QuotaOptions.Default;

    /// <summary>RFC 4918's answer to a write the account has no room for.</summary>
    private const int InsufficientStorage = 507;

    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CS = "http://calendarserver.org/ns/";

    private const string PrincipalHref = "/dav/principal/";
    private const string HomeHref = "/dav/calendars/";
    private const string CalendarContentType = "text/calendar; charset=utf-8; component=VEVENT";

    // ---------------------------------------------------------------- OPTIONS

    public Task<IResult> Options(HttpContext ctx)
    {
        // "calendar-access" is what tells clients this WebDAV server speaks CalDAV.
        ctx.Response.Headers["DAV"] = "1, calendar-access";
        ctx.Response.Headers.Allow = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, REPORT";
        return Task.FromResult(Results.StatusCode(StatusCodes.Status200OK));
    }

    // ---------------------------------------------------------------- PROPFIND

    public async Task<IResult> PropfindRoot(HttpContext ctx)
    {
        var requested = await ReadRequestedPropsAsync(ctx);
        return MultiStatus(BuildResponse("/dav/", requested, new Dictionary<XName, object?>
        {
            [D + "resourcetype"] = new XElement(D + "collection"),
            [D + "current-user-principal"] = new XElement(D + "href", PrincipalHref),
            [D + "displayname"] = "24seven",
        }));
    }

    public async Task<IResult> PropfindPrincipal(HttpContext ctx)
    {
        var requested = await ReadRequestedPropsAsync(ctx);
        var name = ctx.User.FindFirstValue(ClaimTypes.Name) ?? "24seven";
        return MultiStatus(BuildResponse(PrincipalHref, requested, new Dictionary<XName, object?>
        {
            [D + "resourcetype"] = new object[] { new XElement(D + "collection"), new XElement(D + "principal") },
            [D + "displayname"] = name,
            [D + "current-user-principal"] = new XElement(D + "href", PrincipalHref),
            [C + "calendar-home-set"] = new XElement(D + "href", HomeHref),
            [C + "calendar-user-address-set"] = new XElement(D + "href", $"mailto:{name}"),
        }));
    }

    public async Task<IResult> PropfindHome(HttpContext ctx)
    {
        var userId = GetUserId(ctx.User);
        var requested = await ReadRequestedPropsAsync(ctx);

        var responses = new List<XElement>
        {
            BuildResponse(HomeHref, requested, new Dictionary<XName, object?>
            {
                [D + "resourcetype"] = new XElement(D + "collection"),
                [D + "current-user-principal"] = new XElement(D + "href", PrincipalHref),
                [D + "displayname"] = "Calendars",
            }),
        };

        if (Depth(ctx) >= 1)
        {
            // A fresh account may have no calendar yet (the web app creates one lazily);
            // create the default here so a first-time DAVx⁵ setup finds something to sync.
            var calendars = await GetOrCreateCalendarsAsync(userId);
            foreach (var cal in calendars)
            {
                responses.Add(BuildResponse(CalendarHref(cal.Id), requested, await CalendarPropsAsync(cal)));
            }
        }

        return MultiStatus([.. responses]);
    }

    public async Task<IResult> PropfindCalendar(Guid calendarId, HttpContext ctx)
    {
        var cal = await GetOwnedCalendarAsync(ctx.User, calendarId, ctx.RequestAborted);
        if (cal is null)
        {
            return Results.NotFound();
        }

        var requested = await ReadRequestedPropsAsync(ctx);
        var responses = new List<XElement> { BuildResponse(CalendarHref(cal.Id), requested, await CalendarPropsAsync(cal)) };

        if (Depth(ctx) >= 1)
        {
            var events = await db.Events.AsNoTracking()
                .Where(e => e.CalendarId == cal.Id && e.SeriesMasterId == null) // overrides live inside their master's resource
                .Select(e => new { e.Uid, e.UpdatedAt })
                .ToListAsync(ctx.RequestAborted);
            var tasks = await db.Tasks.AsNoTracking()
                .Where(t => t.CalendarId == cal.Id)
                .Select(t => new { t.Uid, t.UpdatedAt })
                .ToListAsync(ctx.RequestAborted);
            foreach (var e in events.Concat(tasks.Select(t => new { t.Uid, t.UpdatedAt })))
            {
                responses.Add(BuildResponse(EventHref(cal.Id, e.Uid), requested, new Dictionary<XName, object?>
                {
                    [D + "resourcetype"] = null, // plain resource
                    [D + "getetag"] = ETagOf(e.UpdatedAt),
                    [D + "getcontenttype"] = CalendarContentType,
                }));
            }
        }

        return MultiStatus([.. responses]);
    }

    public async Task<IResult> PropfindEvent(Guid calendarId, string resource, HttpContext ctx)
    {
        var e = await FindEventAsync(ctx.User, calendarId, resource, track: false, ctx.RequestAborted);
        var t = e is null ? await FindTaskAsync(ctx.User, calendarId, resource, track: false, ctx.RequestAborted) : null;
        if (e is null && t is null)
        {
            return Results.NotFound();
        }
        var requested = await ReadRequestedPropsAsync(ctx);
        return MultiStatus(BuildResponse(EventHref(calendarId, e?.Uid ?? t!.Uid), requested, new Dictionary<XName, object?>
        {
            [D + "resourcetype"] = null, // plain resource
            [D + "getetag"] = ETagOf(e?.UpdatedAt ?? t!.UpdatedAt),
            [D + "getcontenttype"] = CalendarContentType,
        }));
    }

    // ---------------------------------------------------------------- REPORT

    public async Task<IResult> Report(Guid calendarId, HttpContext ctx)
    {
        var cal = await GetOwnedCalendarAsync(ctx.User, calendarId, ctx.RequestAborted);
        if (cal is null)
        {
            return Results.NotFound();
        }

        XDocument body;
        try
        {
            body = await XDocument.LoadAsync(ctx.Request.Body, LoadOptions.None, ctx.RequestAborted);
        }
        catch (System.Xml.XmlException)
        {
            return Results.BadRequest();
        }

        var root = body.Root?.Name;
        List<DomainEvent> events;
        if (root == C + "calendar-multiget")
        {
            // Resolve each requested href back to a UID; unknown ones get a 404 response below.
            var uids = body.Root!.Elements(D + "href")
                .Select(h => UidFromHref(h.Value))
                .Where(u => u is not null)
                .Cast<string>()
                .ToList();
            events = await WithSeries(db.Events.AsNoTracking())
                .Where(e => e.CalendarId == cal.Id && e.SeriesMasterId == null && uids.Contains(e.Uid))
                .ToListAsync(ctx.RequestAborted);

            var tasks = await db.Tasks.AsNoTracking()
                .Where(t => t.CalendarId == cal.Id && uids.Contains(t.Uid))
                .ToListAsync(ctx.RequestAborted);

            var found = new HashSet<string>(events.Select(e => e.Uid).Concat(tasks.Select(t => t.Uid)), StringComparer.OrdinalIgnoreCase);
            var responses = events.Select(e => EventDataResponse(cal.Id, e)).Concat(tasks.Select(t => TaskDataResponse(cal.Id, t))).ToList();
            responses.AddRange(uids.Where(u => !found.Contains(u)).Select(u =>
                new XElement(D + "response",
                    new XElement(D + "href", EventHref(cal.Id, u)),
                    new XElement(D + "status", "HTTP/1.1 404 Not Found"))));
            return MultiStatus([.. responses]);
        }

        if (root == D + "sync-collection")
        {
            return await SyncCollectionAsync(cal, body, ctx.RequestAborted);
        }

        if (root == C + "calendar-query")
        {
            // Which components the query is about: VTODO, VEVENT, or (no component filter) both.
            // A task query returns every task: its other filters (due ranges, COMPLETED) may only
            // ever widen the answer — the client applies them again — never narrow it wrongly.
            var components = body.Descendants(C + "comp-filter")
                .Select(f => f.Attribute("name")?.Value?.ToUpperInvariant())
                .Where(n => n is "VEVENT" or "VTODO")
                .ToHashSet();
            var wantsTasks = components.Count == 0 || components.Contains("VTODO");
            var wantsEvents = components.Count == 0 || components.Contains("VEVENT");
            var taskResponses = wantsTasks
                ? (await db.Tasks.AsNoTracking().Where(t => t.CalendarId == cal.Id).ToListAsync(ctx.RequestAborted))
                    .Select(t => TaskDataResponse(cal.Id, t))
                    .ToList()
                : [];
            if (!wantsEvents)
            {
                return MultiStatus([.. taskResponses]);
            }

            // Honour the client's time-range (RFC 4791 §9.9). Clients ask for a window — the next
            // few months, typically — and returning the entire calendar on every poll instead
            // meant the response grew with the account's whole history rather than the window.
            var (rangeStart, rangeEnd) = ReadTimeRange(body);

            var query = WithSeries(db.Events.AsNoTracking())
                .Where(e => e.CalendarId == cal.Id && e.SeriesMasterId == null);
            // An all-day event is a date wherever the client is, stored at midnight UTC: its real
            // span lies up to a day either side, and runs to the end of its last day. Its bounds
            // are widened accordingly — the client drops what it doesn't want, but can't ask again
            // for what was left out.
            if (rangeEnd is { } end)
            {
                // Nothing that begins after the window can appear in it, series included.
                var allDayEnd = end.AddDays(1);
                query = query.Where(e => e.IsAllDay ? e.StartUtc < allDayEnd : e.StartUtc < end);
            }
            if (rangeStart is { } start)
            {
                // A series has to be kept regardless: its RRULE — which SQL can't evaluate — may
                // put occurrences inside the window long after the stored start.
                var allDayStart = start.AddDays(-2);
                query = query.Where(e => e.RRule != null
                    || (e.IsAllDay ? (e.EndUtc ?? e.StartUtc) > allDayStart : (e.EndUtc ?? e.StartUtc) >= start));
            }

            events = await query.ToListAsync(ctx.RequestAborted);
            // SQL had to keep every series; drop those with nothing in the window — a weekly
            // meeting that ended years ago shouldn't be resent on every poll.
            if (rangeStart is { } from)
            {
                events = events.Where(e => e.RRule is null || SeriesTouches(e, from, rangeEnd)).ToList();
            }
            return MultiStatus([.. events.Select(e => EventDataResponse(cal.Id, e)), .. taskResponses]);
        }

        return Results.StatusCode(StatusCodes.Status403Forbidden); // unsupported report
    }

    /// <summary>
    /// Whether a series has an occurrence, or an edited occurrence, in [from, to). All-day series
    /// get the same day of slack either side as in SQL; a series whose rule no longer expands is
    /// kept — the client should still see the resource it holds, never have it vanish.
    /// </summary>
    private static bool SeriesTouches(DomainEvent series, DateTime from, DateTime? to)
    {
        var (lo, hi) = series.IsAllDay ? (from.AddDays(-2), to?.AddDays(1)) : (from, to);
        var upper = hi ?? lo.AddYears(200); // open-ended: any later occurrence counts
        if (series.Overrides.Any(o => o.StartUtc < upper && (o.EndUtc ?? o.StartUtc) >= lo))
        {
            return true;
        }
        if (!RecurrenceRules.TryNormalize(series.RRule, out _, out _))
        {
            return true;
        }
        return RecurrenceExpander.Expand(
            series.StartUtc, series.EndUtc ?? series.StartUtc, series.TimeZoneId, series.RRule!,
            RecurrenceExpander.ParseExDates(series.ExDates), lo, upper, series.IsAllDay).Any();
    }

    /// <summary>
    /// The <c>&lt;C:time-range&gt;</c> a calendar-query asks for, wherever it sits in the filter
    /// tree. Both bounds are optional (RFC 4791 allows either alone), and anything unparseable is
    /// treated as absent — a filter we don't understand must widen the result, never narrow it.
    /// </summary>
    private static (DateTime? Start, DateTime? End) ReadTimeRange(XDocument body)
    {
        var range = body.Descendants(C + "time-range").FirstOrDefault();
        return range is null
            ? (null, null)
            : (ParseICalUtc(range.Attribute("start")?.Value), ParseICalUtc(range.Attribute("end")?.Value));
    }

    /// <summary>Parses an iCalendar UTC stamp ("20260901T000000Z"), or null.</summary>
    private static DateTime? ParseICalUtc(string? value) =>
        DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;

    // ---------------------------------------------------------------- resources

    public async Task<IResult> GetEvent(Guid calendarId, string resource, HttpContext ctx)
    {
        var e = await FindEventAsync(ctx.User, calendarId, resource, track: false, ctx.RequestAborted);
        if (e is null)
        {
            var t = await FindTaskAsync(ctx.User, calendarId, resource, track: false, ctx.RequestAborted);
            if (t is null)
            {
                return Results.NotFound();
            }
            ctx.Response.Headers.ETag = ETagOf(t.UpdatedAt);
            return Results.Text(TaskICalMapper.Serialize(t), CalendarContentType);
        }
        ctx.Response.Headers.ETag = ETagOf(e.UpdatedAt);
        return Results.Text(Serialize(e), CalendarContentType);
    }

    public async Task<IResult> PutEvent(Guid calendarId, string resource, HttpContext ctx)
    {
        var cal = await GetOwnedCalendarAsync(ctx.User, calendarId, ctx.RequestAborted);
        if (cal is null || !resource.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound();
        }
        if (cal.SourceUrl is not null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden); // a subscription: read-only
        }

        var ics = await ReadBodyAsync(ctx, MaxResourceBytes);
        if (ics is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        ICalCalendar? parsed;
        try
        {
            parsed = ICalCalendar.Load(ics);
        }
        catch (Exception)
        {
            parsed = null;
        }

        // A resource holding a VTODO (and no VEVENT) is a task.
        if (parsed is not null && parsed.Events.Count == 0 && parsed.Todos.FirstOrDefault() is { } todo)
        {
            return await PutTaskAsync(cal, resource, todo, ctx);
        }

        // One resource is one series: the master plus any overrides (RECURRENCE-ID) of the same
        // UID. A resource of overrides alone (an invitation to one instance) is kept as a one-off.
        var (masterVe, overrideVes) = SeriesWriter.Split(parsed?.Events.ToList() ?? []);
        var primary = masterVe ?? overrideVes.FirstOrDefault();
        if (primary is null)
        {
            return Results.BadRequest();
        }
        if (masterVe is null)
        {
            overrideVes = [];
        }

        // DAVx⁵ (our target client) names resources {UID}.ics, so the body's UID and the
        // resource name agree; the body's UID wins when present because it is what a later
        // export/import round-trip preserves.
        var uid = string.IsNullOrWhiteSpace(primary.Uid) ? Uri.UnescapeDataString(resource[..^4]) : primary.Uid;
        if (uid.Length > ICalEventMapper.MaxUidLength)
        {
            return Results.BadRequest();
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ifMatch = ctx.Request.Headers.IfMatch.ToString();
        var ifNoneMatch = ctx.Request.Headers.IfNoneMatch.ToString();

        var existing = await db.Events
            .Include(e => e.Reminders)
            .Include(e => e.Overrides).ThenInclude(o => o.Reminders)
            .AsSplitQuery()
            .FirstOrDefaultAsync(e => e.CalendarId == cal.Id && e.Uid == uid && e.SeriesMasterId == null, ctx.RequestAborted);

        if (existing is null && !string.IsNullOrEmpty(ifMatch))
        {
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        }
        if (existing is not null &&
            (ifNoneMatch == "*" || (!string.IsNullOrEmpty(ifMatch) && ifMatch != ETagOf(existing.UpdatedAt))))
        {
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        }
        if (existing is null && await db.Tasks.AnyAsync(t => t.CalendarId == cal.Id && t.Uid == uid, ctx.RequestAborted))
        {
            return Results.StatusCode(StatusCodes.Status409Conflict); // that name is a task, not an event
        }

        if (existing is null)
        {
            try
            {
                await Quotas.EnsureEventsAsync(db, quotas, cal.OwnerUserId, 1 + overrideVes.Count, ctx.RequestAborted);
            }
            catch (QuotaExceededException)
            {
                return Results.StatusCode(InsufficientStorage);
            }
        }

        // For category resolution: an incoming CATEGORIES/COLOR is matched against the
        // user's categories (by name, else nearest color) inside the mapper.
        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.OwnerUserId == cal.OwnerUserId)
            .ToListAsync(ctx.RequestAborted);

        // Two-way reminder sync: VALARMs a client sends become the reminder set; a VEVENT with
        // none keeps what's there (many clients drop alarms they don't manage, and wiping the
        // web-set reminders on every poll would be worse than missing a phone-side delete).
        var series = new SeriesWriter(db).Write(
            existing, primary, overrideVes, cal.Id, uid, now, categories, replaceAlarms: true,
            inheritedCategoryId: cal.DefaultCategoryId);

        try
        {
            await db.SaveChangesAsync(ctx.RequestAborted);
        }
        catch (DbUpdateException) when (existing is null)
        {
            // Another request created the same UID between our lookup and this insert (the
            // unique index caught it). The client's view is stale either way.
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        ctx.Response.Headers.ETag = ETagOf(series.UpdatedAt);
        return Results.StatusCode(existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent);
    }

    public async Task<IResult> DeleteEvent(Guid calendarId, string resource, HttpContext ctx)
    {
        var e = await FindEventAsync(ctx.User, calendarId, resource, track: true, ctx.RequestAborted);
        if (e is null)
        {
            return await DeleteTaskAsync(calendarId, resource, ctx);
        }
        if (await db.Calendars.AnyAsync(c => c.Id == e.CalendarId && c.SourceUrl != null, ctx.RequestAborted))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden); // a subscription: read-only
        }

        var ifMatch = ctx.Request.Headers.IfMatch.ToString();
        if (!string.IsNullOrEmpty(ifMatch) && ifMatch != ETagOf(e.UpdatedAt))
        {
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        db.Events.Remove(e);
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.StatusCode(StatusCodes.Status204NoContent);
    }

    private async Task<IResult> PutTaskAsync(Calendar cal, string resource, Ical.Net.CalendarComponents.Todo todo, HttpContext ctx)
    {
        var uid = string.IsNullOrWhiteSpace(todo.Uid) ? Uri.UnescapeDataString(resource[..^4]) : todo.Uid;
        if (uid.Length > ICalEventMapper.MaxUidLength)
        {
            return Results.BadRequest();
        }
        var existing = await db.Tasks.FirstOrDefaultAsync(t => t.CalendarId == cal.Id && t.Uid == uid, ctx.RequestAborted);
        var ifMatch = ctx.Request.Headers.IfMatch.ToString();
        var ifNoneMatch = ctx.Request.Headers.IfNoneMatch.ToString();
        if (existing is null && !string.IsNullOrEmpty(ifMatch))
        {
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        }
        if (existing is not null &&
            (ifNoneMatch == "*" || (!string.IsNullOrEmpty(ifMatch) && ifMatch != ETagOf(existing.UpdatedAt))))
        {
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        }
        if (existing is null && await db.Events.AnyAsync(e => e.CalendarId == cal.Id && e.Uid == uid && e.SeriesMasterId == null, ctx.RequestAborted))
        {
            return Results.StatusCode(StatusCodes.Status409Conflict); // that name is an event, not a task
        }
        if (existing is null)
        {
            try
            {
                await Quotas.EnsureTasksAsync(db, quotas, cal.OwnerUserId, 1, ctx.RequestAborted);
            }
            catch (QuotaExceededException)
            {
                return Results.StatusCode(InsufficientStorage);
            }
        }

        var task = await new TaskWriter(db).WriteAsync(existing, todo, cal.Id, uid, timeProvider.GetUtcNow().UtcDateTime, ctx.RequestAborted);
        try
        {
            await db.SaveChangesAsync(ctx.RequestAborted);
        }
        catch (DbUpdateException) when (existing is null)
        {
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed); // created meanwhile
        }
        ctx.Response.Headers.ETag = ETagOf(task.UpdatedAt);
        return Results.StatusCode(existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent);
    }

    private async Task<IResult> DeleteTaskAsync(Guid calendarId, string resource, HttpContext ctx)
    {
        var t = await FindTaskAsync(ctx.User, calendarId, resource, track: true, ctx.RequestAborted);
        if (t is null)
        {
            return Results.NotFound();
        }
        var ifMatch = ctx.Request.Headers.IfMatch.ToString();
        if (!string.IsNullOrEmpty(ifMatch) && ifMatch != ETagOf(t.UpdatedAt))
        {
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        }
        // A phone deletes subtasks one by one; any it keeps stay, as tasks of their own.
        foreach (var child in await db.Tasks.Where(c => c.ParentId == t.Id).ToListAsync(ctx.RequestAborted))
        {
            child.ParentId = null;
        }
        db.Tasks.Remove(t);
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.StatusCode(StatusCodes.Status204NoContent);
    }

    // ---------------------------------------------------------------- helpers

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid)
            ? guid
            : throw new InvalidOperationException("Authenticated CalDAV user has no valid id claim.");
    }

    private async Task<Calendar?> GetOwnedCalendarAsync(ClaimsPrincipal user, Guid calendarId, CancellationToken ct = default)
    {
        var userId = GetUserId(user);
        return await db.Calendars.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == calendarId && c.OwnerUserId == userId, ct);
    }

    /// <summary>A query for whole resources: the master with what serializing it needs, its
    /// overrides included.</summary>
    private static IQueryable<DomainEvent> WithSeries(IQueryable<DomainEvent> events) =>
        events
            .Include(e => e.Calendar).ThenInclude(c => c!.DefaultCategory)
            .Include(e => e.Category)
            .Include(e => e.Reminders)
            .Include(e => e.Overrides).ThenInclude(o => o.Category)
            .Include(e => e.Overrides).ThenInclude(o => o.Reminders)
            .AsSplitQuery();

    /// <summary>
    /// The request body as text, or null once it passes <paramref name="maxBytes"/>. A single
    /// event is a few kilobytes; anything near the limit is not a calendar object, and parsing it
    /// would only spend memory on the attempt.
    /// </summary>
    private static async Task<string?> ReadBodyAsync(HttpContext ctx, int maxBytes)
    {
        if (ctx.Request.ContentLength > maxBytes)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private async Task<List<Calendar>> GetOrCreateCalendarsAsync(Guid userId)
    {
        var calendars = await db.Calendars.AsNoTracking()
            .Where(c => c.OwnerUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();
        if (calendars.Count > 0)
        {
            return calendars;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cal = new Calendar { Id = Guid.NewGuid(), OwnerUserId = userId, Name = "Personal", CreatedAt = now, UpdatedAt = now };
        db.Calendars.Add(cal);
        await db.SaveChangesAsync();
        return [cal];
    }

    /// <summary>The task a resource name stands for, in a calendar of the user's, or null.</summary>
    private async Task<TaskItem?> FindTaskAsync(
        ClaimsPrincipal user, Guid calendarId, string resource, bool track, CancellationToken ct)
    {
        if (!resource.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var cal = await GetOwnedCalendarAsync(user, calendarId, ct);
        if (cal is null)
        {
            return null;
        }
        var uid = Uri.UnescapeDataString(resource[..^4]);
        var query = track ? db.Tasks : db.Tasks.AsNoTracking();
        return await query.FirstOrDefaultAsync(t => t.CalendarId == cal.Id && t.Uid == uid, ct);
    }

    private async Task<DomainEvent?> FindEventAsync(
        ClaimsPrincipal user, Guid calendarId, string resource, bool track, CancellationToken ct)
    {
        if (!resource.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var cal = await GetOwnedCalendarAsync(user, calendarId, ct);
        if (cal is null)
        {
            return null;
        }
        var uid = Uri.UnescapeDataString(resource[..^4]);
        var query = track ? db.Events : db.Events.AsNoTracking();
        return await WithSeries(query)
            .FirstOrDefaultAsync(e => e.CalendarId == cal.Id && e.Uid == uid && e.SeriesMasterId == null, ct);
    }

    private async Task<Dictionary<XName, object?>> CalendarPropsAsync(Calendar cal)
    {
        // CTag changes whenever anything in the collection changes — clients poll it to
        // decide whether a full listing is worth doing.
        var agg = await db.Events.AsNoTracking()
            .Where(e => e.CalendarId == cal.Id)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Max = g.Max(e => (DateTime?)e.UpdatedAt) })
            .FirstOrDefaultAsync();
        var taskAgg = await db.Tasks.AsNoTracking()
            .Where(t => t.CalendarId == cal.Id)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Max = g.Max(t => (DateTime?)t.UpdatedAt) })
            .FirstOrDefaultAsync();
        var newest = new[] { agg?.Max, taskAgg?.Max }.Max();
        var ctag = $"{(newest?.Ticks ?? 0) / 10:x}-{agg?.Count ?? 0}-{taskAgg?.Count ?? 0}";

        return new Dictionary<XName, object?>
        {
            [D + "resourcetype"] = new object[] { new XElement(D + "collection"), new XElement(C + "calendar") },
            [D + "displayname"] = cal.Name,
            [D + "current-user-principal"] = new XElement(D + "href", PrincipalHref),
            [D + "owner"] = new XElement(D + "href", PrincipalHref),
            [C + "supported-calendar-component-set"] = new object[]
            {
                new XElement(C + "comp", new XAttribute("name", "VEVENT")),
                new XElement(C + "comp", new XAttribute("name", "VTODO")),
            },
            [CS + "getctag"] = ctag,
            [D + "sync-token"] = SyncToken(DateTime.UtcNow),
            [D + "supported-report-set"] = new object[]
            {
                SupportedReport(C + "calendar-multiget"),
                SupportedReport(C + "calendar-query"),
                SupportedReport(D + "sync-collection"),
            },
            // A subscription is read-only; clients that read this (iOS, DAVx5) show it so and
            // don't offer to edit. Writes are refused regardless.
            [D + "current-user-privilege-set"] = cal.SourceUrl is null
                ? new object[] { Privilege("read"), Privilege("write"), Privilege("write-content"), Privilege("bind"), Privilege("unbind") }
                : new object[] { Privilege("read") },
        };
    }

    private static XElement Privilege(string name) => new(D + "privilege", new XElement(D + name));

    private static XElement SupportedReport(XName report) =>
        new(D + "supported-report", new XElement(D + "report", new XElement(report)));

    // ---------------------------------------------------------------- sync-collection (RFC 6578)

    private const string SyncTokenPrefix = "http://calendarit.app/ns/sync/";

    /// <summary>
    /// A token is the moment it was handed out. Changes are what was modified from a little
    /// before then on: a write stamps its time when it starts but becomes visible when it
    /// commits, so without the overlap one committing just after a token was issued could be
    /// missed for good. Reporting a few unchanged resources again is harmless — the client sees
    /// the same ETag.
    /// </summary>
    private static readonly TimeSpan SyncOverlap = TimeSpan.FromMinutes(2);

    /// <summary>How far back a token is honoured: as long as deletions are remembered
    /// (<c>MaintenanceJob</c> keeps them a little longer). An older one means "start over".</summary>
    public static readonly TimeSpan SyncTokenLifetime = TimeSpan.FromDays(60);

    private static string SyncToken(DateTime utc) => SyncTokenPrefix + utc.Ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>The instant a token stands for; null when it isn't one of ours or has expired.</summary>
    private static DateTime? ReadSyncToken(string token, DateTime now)
    {
        if (!token.StartsWith(SyncTokenPrefix, StringComparison.Ordinal)
            || !long.TryParse(token.AsSpan(SyncTokenPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > now.AddMinutes(5).Ticks)
        {
            return null;
        }
        var at = new DateTime(ticks, DateTimeKind.Utc);
        return at < now - SyncTokenLifetime ? null : at;
    }

    private async Task<IResult> SyncCollectionAsync(Calendar cal, XDocument body, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var token = body.Root!.Element(D + "sync-token")?.Value.Trim() ?? string.Empty;
        DateTime? since = null;
        if (token.Length > 0)
        {
            since = ReadSyncToken(token, now);
            if (since is null)
            {
                // RFC 6578 §3.2: the client drops its state and syncs from scratch.
                return Results.Content(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                    + new XElement(D + "error", new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName), new XElement(D + "valid-sync-token"))
                        .ToString(SaveOptions.DisableFormatting),
                    "application/xml; charset=utf-8",
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        // Only what was asked for: a first sync usually wants ETags and fetches the data with a
        // multiget, and sending every calendar's whole content here would defeat the point.
        var wanted = body.Root!.Element(D + "prop")?.Elements().Select(e => e.Name).ToHashSet() ?? [D + "getetag"];
        var withData = wanted.Contains(C + "calendar-data");

        var query = db.Events.AsNoTracking().Where(e => e.CalendarId == cal.Id && e.SeriesMasterId == null);
        if (since is { } from)
        {
            var changedFrom = from - SyncOverlap;
            query = query.Where(e => e.UpdatedAt >= changedFrom);
        }
        var changed = withData
            ? await WithSeries(query).ToListAsync(ct)
            : (await query.Select(e => new { e.Uid, e.UpdatedAt }).ToListAsync(ct))
                .Select(e => new DomainEvent { Uid = e.Uid, UpdatedAt = e.UpdatedAt })
                .ToList();

        var responses = new List<XElement>();
        foreach (var e in changed)
        {
            responses.Add(withData
                ? EventDataResponse(cal.Id, e)
                : BuildResponse(EventHref(cal.Id, e.Uid), [.. wanted], new Dictionary<XName, object?>
                {
                    [D + "getetag"] = ETagOf(e.UpdatedAt),
                    [D + "getcontenttype"] = CalendarContentType,
                }));
        }

        var taskQuery = db.Tasks.AsNoTracking().Where(t => t.CalendarId == cal.Id);
        if (since is { } tasksFrom)
        {
            var changedFrom = tasksFrom - SyncOverlap;
            taskQuery = taskQuery.Where(t => t.UpdatedAt >= changedFrom);
        }
        foreach (var t in await taskQuery.ToListAsync(ct))
        {
            responses.Add(withData
                ? TaskDataResponse(cal.Id, t)
                : BuildResponse(EventHref(cal.Id, t.Uid), [.. wanted], new Dictionary<XName, object?>
                {
                    [D + "getetag"] = ETagOf(t.UpdatedAt),
                    [D + "getcontenttype"] = CalendarContentType,
                }));
        }

        if (since is { } deletedFrom)
        {
            var cutoff = deletedFrom - SyncOverlap;
            var goneUids = await db.DeletedResources.AsNoTracking()
                .Where(d => d.CalendarId == cal.Id && d.DeletedAtUtc >= cutoff)
                .Select(d => d.Uid)
                .Distinct()
                .ToListAsync(ct);
            if (goneUids.Count > 0)
            {
                // A UID deleted and written again since is a change, not a removal.
                var present = await db.Events.AsNoTracking()
                    .Where(e => e.CalendarId == cal.Id && e.SeriesMasterId == null && goneUids.Contains(e.Uid))
                    .Select(e => e.Uid)
                    .ToListAsync(ct);
                present.AddRange(await db.Tasks.AsNoTracking()
                    .Where(t => t.CalendarId == cal.Id && goneUids.Contains(t.Uid))
                    .Select(t => t.Uid)
                    .ToListAsync(ct));
                foreach (var uid in goneUids.Except(present, StringComparer.Ordinal))
                {
                    responses.Add(new XElement(D + "response",
                        new XElement(D + "href", EventHref(cal.Id, uid)),
                        new XElement(D + "status", "HTTP/1.1 404 Not Found")));
                }
            }
        }

        var doc = new XElement(D + "multistatus",
            new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cs", CS.NamespaceName),
            responses,
            new XElement(D + "sync-token", SyncToken(now)));
        return Results.Content(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + doc.ToString(SaveOptions.DisableFormatting),
            "application/xml; charset=utf-8",
            statusCode: StatusCodes.Status207MultiStatus);
    }

    private static string CalendarHref(Guid id) => $"{HomeHref}{id:N}/";

    private static string EventHref(Guid calendarId, string uid) =>
        $"{CalendarHref(calendarId)}{Uri.EscapeDataString(uid)}.ics";

    /// <summary>Maps an href from a multiget back to the event UID it names, or null.</summary>
    private static string? UidFromHref(string href)
    {
        var name = href.TrimEnd('/').Split('/').LastOrDefault();
        return name is not null && name.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(name[..^4])
            : null;
    }

    /// <summary>
    /// The resource's ETag, from its last-modified time at microsecond precision — the finest
    /// both databases keep. Postgres stores microseconds, so a tick-precise tag handed out after a
    /// write never matched the one read back, and every following edit from a phone was refused
    /// with 412.
    /// </summary>
    private static string ETagOf(DateTime updatedAt) => $"\"{updatedAt.Ticks / 10:x}\"";

    /// <summary>Largest calendar object a PUT may carry.</summary>
    private const int MaxResourceBytes = 1024 * 1024;

    private static string Serialize(DomainEvent e)
    {
        var cal = new ICalCalendar { ProductId = "-//CalendarIT//EN" };
        foreach (var ve in ICalEventMapper.ToICalEvents(e, e.Overrides, e.Calendar?.DefaultCategory))
        {
            cal.Events.Add(ve);
        }
        return new CalendarSerializer().SerializeToString(cal)!;
    }

    private static XElement TaskDataResponse(Guid calendarId, TaskItem t) =>
        new(D + "response",
            new XElement(D + "href", EventHref(calendarId, t.Uid)),
            new XElement(D + "propstat",
                new XElement(D + "prop",
                    new XElement(D + "getetag", ETagOf(t.UpdatedAt)),
                    new XElement(D + "getcontenttype", CalendarContentType),
                    new XElement(C + "calendar-data", TaskICalMapper.Serialize(t))),
                new XElement(D + "status", "HTTP/1.1 200 OK")));

    private XElement EventDataResponse(Guid calendarId, DomainEvent e) =>
        new(D + "response",
            new XElement(D + "href", EventHref(calendarId, e.Uid)),
            new XElement(D + "propstat",
                new XElement(D + "prop",
                    new XElement(D + "getetag", ETagOf(e.UpdatedAt)),
                    new XElement(D + "getcontenttype", CalendarContentType),
                    new XElement(C + "calendar-data", Serialize(e))),
                new XElement(D + "status", "HTTP/1.1 200 OK")));

    /// <summary>Depth header: 0 stays on the collection itself; anything else lists children.</summary>
    private static int Depth(HttpContext ctx) => ctx.Request.Headers["Depth"].ToString() == "0" ? 0 : 1;

    /// <summary>
    /// Reads the PROPFIND body and returns the requested property names, or null for
    /// allprop / an empty body (meaning: return everything we know).
    /// </summary>
    private static async Task<List<XName>?> ReadRequestedPropsAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is null or 0)
        {
            return null;
        }
        try
        {
            var doc = await XDocument.LoadAsync(ctx.Request.Body, LoadOptions.None, ctx.RequestAborted);
            var prop = doc.Root?.Element(D + "prop");
            return prop?.Elements().Select(e => e.Name).ToList();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// One multistatus response for a resource: requested props we know go in a 200
    /// propstat, requested props we don't in a 404 propstat (per RFC 4918 §9.1).
    /// </summary>
    private static XElement BuildResponse(string href, List<XName>? requested, Dictionary<XName, object?> known)
    {
        var found = new XElement(D + "prop");
        var missing = new List<XName>();

        if (requested is null)
        {
            foreach (var (name, value) in known)
            {
                found.Add(new XElement(name, value));
            }
        }
        else
        {
            foreach (var name in requested)
            {
                var match = known.Keys.FirstOrDefault(k =>
                    string.Equals(k.LocalName, name.LocalName, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    found.Add(new XElement(match, known[match]));
                }
                else
                {
                    missing.Add(name);
                }
            }
        }

        var response = new XElement(D + "response",
            new XElement(D + "href", href),
            new XElement(D + "propstat", found, new XElement(D + "status", "HTTP/1.1 200 OK")));
        if (missing.Count > 0)
        {
            response.Add(new XElement(D + "propstat",
                new XElement(D + "prop", missing.Select(m => new XElement(m))),
                new XElement(D + "status", "HTTP/1.1 404 Not Found")));
        }
        return response;
    }

    private static IResult MultiStatus(params XElement[] responses)
    {
        var doc = new XElement(D + "multistatus",
            new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cs", CS.NamespaceName),
            responses);
        return Results.Content(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + doc.ToString(SaveOptions.DisableFormatting),
            "application/xml; charset=utf-8",
            statusCode: StatusCodes.Status207MultiStatus);
    }
}
