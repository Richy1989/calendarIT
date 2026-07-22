using System.Security.Claims;
using System.Xml.Linq;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Persistence;
using Ical.Net.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;
using DomainEvent = CalendarIT.Domain.CalendarEvent;
using ICalCalendar = Ical.Net.Calendar;

namespace CalendarIT.CalDav;

/// <summary>
/// Minimal CalDAV server (RFC 4791): discovery (principal → calendar-home → calendars),
/// PROPFIND listings with ETags/CTag, calendar-query / calendar-multiget REPORTs, and
/// GET/PUT/DELETE of single VEVENT resources. Enough for DAVx⁵ and similar clients to
/// sync both ways. No sync-collection (RFC 6578) yet — clients fall back to CTag polling
/// with full listings, which is fine at personal-calendar scale.
///
/// Layout (hrefs are always absolute paths):
///   /dav/                     → root; advertises the current-user-principal
///   /dav/principal/           → principal; advertises the calendar-home-set
///   /dav/calendars/           → home; children are the user's calendars
///   /dav/calendars/{id}/      → one calendar; children are {uid}.ics event resources
/// </summary>
public sealed class CalDavHandler(AppDbContext db, TimeProvider timeProvider)
{
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
            [D + "displayname"] = "CalendarIT",
        }));
    }

    public async Task<IResult> PropfindPrincipal(HttpContext ctx)
    {
        var requested = await ReadRequestedPropsAsync(ctx);
        var name = ctx.User.FindFirstValue(ClaimTypes.Name) ?? "CalendarIT";
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
        var cal = await GetOwnedCalendarAsync(ctx.User, calendarId);
        if (cal is null)
        {
            return Results.NotFound();
        }

        var requested = await ReadRequestedPropsAsync(ctx);
        var responses = new List<XElement> { BuildResponse(CalendarHref(cal.Id), requested, await CalendarPropsAsync(cal)) };

        if (Depth(ctx) >= 1)
        {
            var events = await db.Events.AsNoTracking()
                .Where(e => e.CalendarId == cal.Id)
                .Select(e => new { e.Uid, e.UpdatedAt })
                .ToListAsync(ctx.RequestAborted);
            foreach (var e in events)
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
        if (e is null)
        {
            return Results.NotFound();
        }
        var requested = await ReadRequestedPropsAsync(ctx);
        return MultiStatus(BuildResponse(EventHref(calendarId, e.Uid), requested, new Dictionary<XName, object?>
        {
            [D + "resourcetype"] = null, // plain resource
            [D + "getetag"] = ETagOf(e.UpdatedAt),
            [D + "getcontenttype"] = CalendarContentType,
        }));
    }

    // ---------------------------------------------------------------- REPORT

    public async Task<IResult> Report(Guid calendarId, HttpContext ctx)
    {
        var cal = await GetOwnedCalendarAsync(ctx.User, calendarId);
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
            events = await db.Events.AsNoTracking()
                .Where(e => e.CalendarId == cal.Id && uids.Contains(e.Uid))
                .ToListAsync(ctx.RequestAborted);

            var found = new HashSet<string>(events.Select(e => e.Uid), StringComparer.OrdinalIgnoreCase);
            var responses = events.Select(e => EventDataResponse(cal.Id, e)).ToList();
            responses.AddRange(uids.Where(u => !found.Contains(u)).Select(u =>
                new XElement(D + "response",
                    new XElement(D + "href", EventHref(cal.Id, u)),
                    new XElement(D + "status", "HTTP/1.1 404 Not Found"))));
            return MultiStatus([.. responses]);
        }

        if (root == C + "calendar-query")
        {
            // Time-range filters are not applied server-side: the whole calendar is returned
            // and the client (which expands recurrences anyway) narrows it down.
            events = await db.Events.AsNoTracking()
                .Where(e => e.CalendarId == cal.Id)
                .ToListAsync(ctx.RequestAborted);
            return MultiStatus([.. events.Select(e => EventDataResponse(cal.Id, e))]);
        }

        return Results.StatusCode(StatusCodes.Status403Forbidden); // unsupported report
    }

    // ---------------------------------------------------------------- resources

    public async Task<IResult> GetEvent(Guid calendarId, string resource, HttpContext ctx)
    {
        var e = await FindEventAsync(ctx.User, calendarId, resource, track: false, ctx.RequestAborted);
        if (e is null)
        {
            return Results.NotFound();
        }
        ctx.Response.Headers.ETag = ETagOf(e.UpdatedAt);
        return Results.Text(Serialize(e), CalendarContentType);
    }

    public async Task<IResult> PutEvent(Guid calendarId, string resource, HttpContext ctx)
    {
        var cal = await GetOwnedCalendarAsync(ctx.User, calendarId);
        if (cal is null || !resource.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound();
        }

        string ics;
        using (var reader = new StreamReader(ctx.Request.Body))
        {
            ics = await reader.ReadToEndAsync(ctx.RequestAborted);
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
        var ve = parsed?.Events.FirstOrDefault();
        if (ve?.Start is null)
        {
            return Results.BadRequest();
        }

        // DAVx⁵ (our target client) names resources {UID}.ics, so the body's UID and the
        // resource name agree; the body's UID wins when present because it is what a later
        // export/import round-trip preserves.
        var uid = string.IsNullOrWhiteSpace(ve.Uid) ? Uri.UnescapeDataString(resource[..^4]) : ve.Uid;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ifMatch = ctx.Request.Headers.IfMatch.ToString();
        var ifNoneMatch = ctx.Request.Headers.IfNoneMatch.ToString();

        var existing = await db.Events.FirstOrDefaultAsync(
            e => e.CalendarId == cal.Id && e.Uid == uid, ctx.RequestAborted);

        if (existing is null)
        {
            if (!string.IsNullOrEmpty(ifMatch))
            {
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
            }
            var created = ICalEventMapper.FromICalEvent(ve, cal.Id, uid, now);
            db.Events.Add(created);
            await db.SaveChangesAsync(ctx.RequestAborted);
            ctx.Response.Headers.ETag = ETagOf(created.UpdatedAt);
            return Results.StatusCode(StatusCodes.Status201Created);
        }

        if (ifNoneMatch == "*" || (!string.IsNullOrEmpty(ifMatch) && ifMatch != ETagOf(existing.UpdatedAt)))
        {
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        ICalEventMapper.Apply(ve, existing, now);
        await db.SaveChangesAsync(ctx.RequestAborted);
        ctx.Response.Headers.ETag = ETagOf(existing.UpdatedAt);
        return Results.StatusCode(StatusCodes.Status204NoContent);
    }

    public async Task<IResult> DeleteEvent(Guid calendarId, string resource, HttpContext ctx)
    {
        var e = await FindEventAsync(ctx.User, calendarId, resource, track: true, ctx.RequestAborted);
        if (e is null)
        {
            return Results.NotFound();
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

    // ---------------------------------------------------------------- helpers

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid)
            ? guid
            : throw new InvalidOperationException("Authenticated CalDAV user has no valid id claim.");
    }

    private async Task<Calendar?> GetOwnedCalendarAsync(ClaimsPrincipal user, Guid calendarId)
    {
        var userId = GetUserId(user);
        return await db.Calendars.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == calendarId && c.OwnerUserId == userId);
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

    private async Task<DomainEvent?> FindEventAsync(
        ClaimsPrincipal user, Guid calendarId, string resource, bool track, CancellationToken ct)
    {
        if (!resource.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var cal = await GetOwnedCalendarAsync(user, calendarId);
        if (cal is null)
        {
            return null;
        }
        var uid = Uri.UnescapeDataString(resource[..^4]);
        var query = track ? db.Events : db.Events.AsNoTracking();
        return await query.FirstOrDefaultAsync(e => e.CalendarId == cal.Id && e.Uid == uid, ct);
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
        var ctag = $"{agg?.Max?.Ticks ?? 0:x}-{agg?.Count ?? 0}";

        return new Dictionary<XName, object?>
        {
            [D + "resourcetype"] = new object[] { new XElement(D + "collection"), new XElement(C + "calendar") },
            [D + "displayname"] = cal.Name,
            [D + "current-user-principal"] = new XElement(D + "href", PrincipalHref),
            [D + "owner"] = new XElement(D + "href", PrincipalHref),
            [C + "supported-calendar-component-set"] = new XElement(C + "comp", new XAttribute("name", "VEVENT")),
            [CS + "getctag"] = ctag,
        };
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

    private static string ETagOf(DateTime updatedAt) => $"\"{updatedAt.Ticks:x}\"";

    private static string Serialize(DomainEvent e)
    {
        var cal = new ICalCalendar { ProductId = "-//CalendarIT//EN" };
        cal.Events.Add(ICalEventMapper.ToICalEvent(e));
        return new CalendarSerializer().SerializeToString(cal)!;
    }

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
