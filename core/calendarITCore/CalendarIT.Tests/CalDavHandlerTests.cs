using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using CalendarIT.CalDav;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarIT.Tests;

/// <summary>
/// Drives the CalDAV handlers the way a client like DAVx⁵ does: discovery via PROPFIND,
/// then PUT/GET/REPORT/DELETE of single-event resources.
/// </summary>
public sealed class CalDavHandlerTests : IDisposable
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly CalDavHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly ServiceProvider _services;

    public CalDavHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "dav@test", NormalizedUserName = "DAV@TEST" });
        _db.SaveChanges();
        _handler = new CalDavHandler(_db, TimeProvider.System);
        _services = new ServiceCollection().AddLogging().BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private DefaultHttpContext Context(string? body = null, string depth = "1")
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _userId.ToString()), new Claim(ClaimTypes.Name, "dav@test")], "Test")),
            RequestServices = _services,
        };
        ctx.Request.Headers["Depth"] = depth;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
        }
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<string> ExecuteAsync(IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await new StreamReader(ctx.Response.Body).ReadToEndAsync();
    }

    private async Task<Guid> DiscoverCalendarIdAsync()
    {
        var ctx = Context();
        var body = await ExecuteAsync(await _handler.PropfindHome(ctx), ctx);
        var href = XDocument.Parse(body).Descendants(D + "href")
            .Select(h => h.Value)
            .First(h => h.StartsWith("/dav/calendars/") && h.Length > "/dav/calendars/".Length);
        return Guid.ParseExact(href.TrimEnd('/').Split('/').Last(), "N");
    }

    private const string PutIcs =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//DAVx5//Test//EN\r\nBEGIN:VEVENT\r\n" +
        "UID:phone-1@test\r\nSUMMARY:From the phone\r\nDTSTART:20260901T100000Z\r\nDTEND:20260901T110000Z\r\n" +
        "END:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public async Task Discovery_RootToPrincipalToHome_LinksUp()
    {
        var ctx = Context();
        var root = await ExecuteAsync(await _handler.PropfindRoot(ctx), ctx);
        Assert.Contains("/dav/principal/", root);

        ctx = Context();
        var principal = await ExecuteAsync(await _handler.PropfindPrincipal(ctx), ctx);
        Assert.Contains("/dav/calendars/", principal);
    }

    [Fact]
    public async Task PropfindHome_FreshUser_CreatesAndListsDefaultCalendar()
    {
        var calId = await DiscoverCalendarIdAsync();
        var cal = await _db.Calendars.SingleAsync();
        Assert.Equal(cal.Id, calId);
        Assert.Equal(_userId, cal.OwnerUserId);
    }

    [Fact]
    public async Task PutThenGet_RoundTripsTheEvent()
    {
        var calId = await DiscoverCalendarIdAsync();

        var putCtx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", putCtx), putCtx);
        Assert.Equal(StatusCodes.Status201Created, putCtx.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(putCtx.Response.Headers.ETag));

        var getCtx = Context();
        var ics = await ExecuteAsync(await _handler.GetEvent(calId, "phone-1@test.ics", getCtx), getCtx);
        Assert.Contains("SUMMARY:From the phone", ics);
        Assert.Contains("UID:phone-1@test", ics);
    }

    [Fact]
    public async Task Put_ExistingUid_UpdatesInsteadOfDuplicating()
    {
        var calId = await DiscoverCalendarIdAsync();

        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var updated = PutIcs.Replace("From the phone", "Edited on the phone");
        ctx = Context(updated);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);
        Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);

        var stored = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Equal("Edited on the phone", stored.Title);
    }

    [Fact]
    public async Task Put_WithoutColorProperty_PreservesStoredColor()
    {
        var calId = await DiscoverCalendarIdAsync();

        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);
        var stored = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        stored.Color = "#7B68EE"; // as chosen in the web UI
        await _db.SaveChangesAsync();

        // Phone edit whose ICS carries no COLOR property (the common case) must not wipe it.
        ctx = Context(PutIcs.Replace("From the phone", "Edited on the phone"));
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var updated = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Equal("Edited on the phone", updated.Title);
        Assert.Equal("#7B68EE", updated.Color);
    }

    [Fact]
    public async Task Put_WithColorProperty_UpdatesStoredColor()
    {
        var calId = await DiscoverCalendarIdAsync();

        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);
        var stored = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        stored.Color = "#7B68EE"; // as chosen in the web UI
        await _db.SaveChangesAsync();

        // DAVx⁵ snaps the Android color to the nearest of ALL 147 CSS3 names, so names
        // outside the default swatches (e.g. darkseagreen) must resolve too. (This user
        // has no categories, so the hex lands on the legacy per-event fallback.)
        ctx = Context(PutIcs.Replace("SUMMARY:From the phone", "SUMMARY:From the phone\r\nCOLOR:darkseagreen"));
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var updated = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Equal("#8FBC8F", updated.Color);
    }

    private const string PutIcsWithAlarm =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//DAVx5//Test//EN\r\nBEGIN:VEVENT\r\n" +
        "UID:phone-1@test\r\nSUMMARY:From the phone\r\nDTSTART:20260901T100000Z\r\nDTEND:20260901T110000Z\r\n" +
        "BEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:From the phone\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\n" +
        "END:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public async Task Put_WithValarm_StoresReminder()
    {
        var calId = await DiscoverCalendarIdAsync();

        var ctx = Context(PutIcsWithAlarm);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var stored = await _db.Events.Include(e => e.Reminders).SingleAsync(e => e.Uid == "phone-1@test");
        var reminder = Assert.Single(stored.Reminders);
        Assert.Equal(15, reminder.MinutesBefore);
    }

    [Fact]
    public async Task Put_ValarmWithChannelProp_RoundTripsChannel()
    {
        var calId = await DiscoverCalendarIdAsync();

        var ics = PutIcsWithAlarm.Replace("TRIGGER:-PT15M\r\n", "TRIGGER:-PT15M\r\nX-CALENDARIT-CHANNEL:WebPush\r\n");
        var ctx = Context(ics);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var stored = await _db.Events.Include(e => e.Reminders).SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Equal(ReminderChannel.WebPush, Assert.Single(stored.Reminders).Channel);
    }

    [Fact]
    public async Task Get_EmitsValarmForReminder()
    {
        var calId = await DiscoverCalendarIdAsync();
        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);
        var stored = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        _db.Reminders.Add(new Reminder
        {
            Id = Guid.NewGuid(), EventId = stored.Id, MinutesBefore = 30, Channel = ReminderChannel.WebPush,
        });
        await _db.SaveChangesAsync();

        var getCtx = Context();
        var ics = await ExecuteAsync(await _handler.GetEvent(calId, "phone-1@test.ics", getCtx), getCtx);
        Assert.Contains("BEGIN:VALARM", ics);
        Assert.Contains("TRIGGER:-PT30M", ics);
        Assert.Contains("X-CALENDARIT-CHANNEL:WebPush", ics);
    }

    [Fact]
    public async Task Put_WithoutValarm_PreservesExistingReminders()
    {
        var calId = await DiscoverCalendarIdAsync();

        var ctx = Context(PutIcsWithAlarm);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        // A later edit whose ICS carries no VALARM (a client that doesn't manage alarms) must not
        // wipe the reminder — the same caution the COLOR/EXDATE handling takes.
        ctx = Context(PutIcs.Replace("From the phone", "Edited on the phone"));
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var stored = await _db.Events.Include(e => e.Reminders).SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Single(stored.Reminders);
    }

    [Fact]
    public async Task Put_WithValarms_ReplacesExistingReminders()
    {
        var calId = await DiscoverCalendarIdAsync();

        var ctx = Context(PutIcsWithAlarm); // 15-minute reminder
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        // A phone edit that changes the alarm to 1 hour should replace, not accumulate.
        ctx = Context(PutIcsWithAlarm.Replace("TRIGGER:-PT15M", "TRIGGER:-PT1H"));
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var stored = await _db.Events.Include(e => e.Reminders).SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Equal(60, Assert.Single(stored.Reminders).MinutesBefore);
    }

    [Fact]
    public async Task Put_WithColor_SnapsToNearestCategory()
    {
        var calId = await DiscoverCalendarIdAsync();
        var now = DateTime.UtcNow;
        var work = new Category { Id = Guid.NewGuid(), OwnerUserId = _userId, Name = "Work", Color = "#6495ED", CreatedAt = now, UpdatedAt = now };
        var urgent = new Category { Id = Guid.NewGuid(), OwnerUserId = _userId, Name = "Urgent", Color = "#FF6347", CreatedAt = now, UpdatedAt = now };
        _db.Categories.AddRange(work, urgent);
        await _db.SaveChangesAsync();

        // A phone edit carrying only COLOR (crimson ≈ tomato) lands on the nearest category.
        var ctx = Context(PutIcs.Replace("SUMMARY:From the phone", "SUMMARY:From the phone\r\nCOLOR:crimson"));
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);
        var stored = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Equal(urgent.Id, stored.CategoryId);

        // A CATEGORIES name wins over the color when both are present.
        var body = PutIcs.Replace("SUMMARY:From the phone", "SUMMARY:From the phone\r\nCATEGORIES:Work\r\nCOLOR:crimson");
        ctx = Context(body);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);
        stored = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Equal(work.Id, stored.CategoryId);

        // No COLOR/CATEGORIES at all leaves the assignment untouched.
        ctx = Context(PutIcs.Replace("From the phone", "Edited on the phone"));
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);
        stored = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Equal(work.Id, stored.CategoryId);
    }

    [Fact]
    public async Task GetEvent_CategorizedEvent_EmitsCategoriesAndColor()
    {
        var calId = await DiscoverCalendarIdAsync();
        var now = DateTime.UtcNow;
        var work = new Category { Id = Guid.NewGuid(), OwnerUserId = _userId, Name = "Work", Color = "#6495ED", CreatedAt = now, UpdatedAt = now };
        _db.Categories.Add(work);
        await _db.SaveChangesAsync();

        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);
        var stored = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        stored.CategoryId = work.Id;
        await _db.SaveChangesAsync();

        var getCtx = Context();
        var ics = await ExecuteAsync(await _handler.GetEvent(calId, "phone-1@test.ics", getCtx), getCtx);
        Assert.Contains("CATEGORIES:Work", ics);
        Assert.Contains("COLOR:cornflowerblue", ics);
    }

    [Fact]
    public async Task Put_WithUnresolvableColor_PreservesStoredColor()
    {
        var calId = await DiscoverCalendarIdAsync();

        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);
        var stored = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        stored.Color = "#7B68EE";
        await _db.SaveChangesAsync();

        // A COLOR value we can't map to a hex must not wipe the color chosen in the web UI.
        ctx = Context(PutIcs.Replace("SUMMARY:From the phone", "SUMMARY:From the phone\r\nCOLOR:not-a-css-color"));
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var updated = await _db.Events.SingleAsync(e => e.Uid == "phone-1@test");
        Assert.Equal("#7B68EE", updated.Color);
    }

    [Fact]
    public async Task Report_CalendarQuery_ReturnsCalendarData()
    {
        var calId = await DiscoverCalendarIdAsync();
        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var query = "<?xml version=\"1.0\"?><c:calendar-query xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:d=\"DAV:\">" +
                    "<d:prop><d:getetag/><c:calendar-data/></d:prop></c:calendar-query>";
        ctx = Context(query);
        var body = await ExecuteAsync(await _handler.Report(calId, ctx), ctx);

        var doc = XDocument.Parse(body);
        var data = Assert.Single(doc.Descendants(C + "calendar-data"));
        Assert.Contains("SUMMARY:From the phone", data.Value);
    }

    /// <summary>A calendar-query asking for one window, the way a client polls.</summary>
    private static string TimeRangeQuery(string start, string end) =>
        "<?xml version=\"1.0\"?><c:calendar-query xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:d=\"DAV:\">" +
        "<d:prop><d:getetag/><c:calendar-data/></d:prop>" +
        "<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\">" +
        $"<c:time-range start=\"{start}\" end=\"{end}\"/>" +
        "</c:comp-filter></c:comp-filter></c:filter></c:calendar-query>";

    private async Task<int> QueryCountAsync(Guid calId, string body)
    {
        var ctx = Context(body);
        var xml = await ExecuteAsync(await _handler.Report(calId, ctx), ctx);
        return XDocument.Parse(xml).Descendants(C + "calendar-data").Count();
    }

    [Fact]
    public async Task Report_CalendarQuery_HonoursTheTimeRange()
    {
        var calId = await DiscoverCalendarIdAsync();
        var ctx = Context(PutIcs); // the event is on 2026-09-01
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        // A window containing it, and two that don't.
        Assert.Equal(1, await QueryCountAsync(calId, TimeRangeQuery("20260901T000000Z", "20261001T000000Z")));
        Assert.Equal(0, await QueryCountAsync(calId, TimeRangeQuery("20261001T000000Z", "20261101T000000Z")));
        Assert.Equal(0, await QueryCountAsync(calId, TimeRangeQuery("20260101T000000Z", "20260201T000000Z")));
    }

    [Fact]
    public async Task Report_CalendarQuery_KeepsSeriesThatStartedBeforeTheWindow()
    {
        var calId = await DiscoverCalendarIdAsync();
        var recurring = PutIcs
            .Replace("UID:phone-1@test", "UID:series-1@test")
            .Replace("DTEND:20260901T110000Z", "DTEND:20260901T110000Z\r\nRRULE:FREQ=WEEKLY");
        var ctx = Context(recurring);
        await ExecuteAsync(await _handler.PutEvent(calId, "series-1@test.ics", ctx), ctx);

        // The stored start is before this window, but the RRULE keeps producing occurrences —
        // SQL can't see that, so the series must survive the filter and reach the client.
        Assert.Equal(1, await QueryCountAsync(calId, TimeRangeQuery("20261001T000000Z", "20261101T000000Z")));
    }

    [Fact]
    public async Task Report_CalendarQuery_WithoutAFilter_ReturnsEverything()
    {
        var calId = await DiscoverCalendarIdAsync();
        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var noFilter = "<?xml version=\"1.0\"?><c:calendar-query xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:d=\"DAV:\">" +
                       "<d:prop><d:getetag/><c:calendar-data/></d:prop></c:calendar-query>";
        Assert.Equal(1, await QueryCountAsync(calId, noFilter));
    }

    [Fact]
    public async Task Report_Multiget_ReturnsRequestedAndFlagsMissing()
    {
        var calId = await DiscoverCalendarIdAsync();
        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        var multiget = "<?xml version=\"1.0\"?><c:calendar-multiget xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:d=\"DAV:\">" +
                       $"<d:prop><c:calendar-data/></d:prop>" +
                       $"<d:href>/dav/calendars/{calId:N}/phone-1%40test.ics</d:href>" +
                       $"<d:href>/dav/calendars/{calId:N}/missing.ics</d:href></c:calendar-multiget>";
        ctx = Context(multiget);
        var body = await ExecuteAsync(await _handler.Report(calId, ctx), ctx);

        Assert.Contains("SUMMARY:From the phone", body);
        Assert.Contains("404", body);
    }

    [Fact]
    public async Task Delete_RemovesTheEvent()
    {
        var calId = await DiscoverCalendarIdAsync();
        var ctx = Context(PutIcs);
        await ExecuteAsync(await _handler.PutEvent(calId, "phone-1@test.ics", ctx), ctx);

        ctx = Context();
        await ExecuteAsync(await _handler.DeleteEvent(calId, "phone-1@test.ics", ctx), ctx);
        Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);
        Assert.Empty(await _db.Events.ToListAsync());
    }

    [Fact]
    public async Task ForeignCalendar_IsInvisible()
    {
        var calId = await DiscoverCalendarIdAsync();

        // A different authenticated user must not see or touch this calendar.
        var stranger = Context();
        stranger.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test"));
        var strangerId = Guid.Parse(stranger.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        _db.Users.Add(new ApplicationUser { Id = strangerId, UserName = "other", NormalizedUserName = "OTHER" });
        await _db.SaveChangesAsync();

        await ExecuteAsync(await _handler.PropfindCalendar(calId, stranger), stranger);
        Assert.Equal(StatusCodes.Status404NotFound, stranger.Response.StatusCode);
    }
}
