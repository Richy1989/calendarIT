using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using CalendarIT.Application.Calendars;
using CalendarIT.CalDav;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarIT.Tests;

/// <summary>
/// Tasks over CalDAV, as phones' task apps sync them: VTODO resources next to the events of a
/// calendar, subtasks linked by RELATED-TO in whatever order they arrive.
/// </summary>
public sealed class CalDavTaskTests : IDisposable
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly CalDavHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly ServiceProvider _services;
    private readonly Guid _calId;

    public CalDavTaskTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "dav@test", NormalizedUserName = "DAV@TEST" });
        _db.SaveChanges();
        _handler = new CalDavHandler(_db, TimeProvider.System);
        _services = new ServiceCollection().AddLogging().BuildServiceProvider();
        _calId = DefaultCalendar.GetOrCreateAsync(_db, TimeProvider.System, _userId).GetAwaiter().GetResult().Id;
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private DefaultHttpContext Context(string? body = null)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test")),
            RequestServices = _services,
        };
        ctx.Request.Headers["Depth"] = "1";
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
        }
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<string> RunAsync(IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await new StreamReader(ctx.Response.Body).ReadToEndAsync();
    }

    private static string Todo(string uid, string summary, string extra = "") =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//jtx//Test//EN\r\nBEGIN:VTODO\r\n" +
        $"UID:{uid}\r\nDTSTAMP:20261101T000000Z\r\nSUMMARY:{summary}\r\n{extra}END:VTODO\r\nEND:VCALENDAR\r\n";

    private async Task<int> PutAsync(string uid, string ics)
    {
        var ctx = Context(ics);
        await RunAsync(await _handler.PutEvent(_calId, $"{uid}.ics", ctx), ctx);
        return ctx.Response.StatusCode;
    }

    private async Task<string> GetAsync(string uid)
    {
        var ctx = Context();
        return await RunAsync(await _handler.GetEvent(_calId, $"{uid}.ics", ctx), ctx);
    }

    private async Task<XDocument> ReportAsync(string body)
    {
        var ctx = Context(body);
        return XDocument.Parse(await RunAsync(await _handler.Report(_calId, ctx), ctx));
    }

    [Fact]
    public async Task APhonesTask_IsStored_AndReadBack()
    {
        var status = await PutAsync("t1@phone", Todo("t1@phone", "Buy milk",
            "DUE;TZID=Europe/Vienna:20261203T180000\r\nPRIORITY:1\r\nDESCRIPTION:2 litres\r\nSTATUS:IN-PROCESS\r\n"));

        Assert.Equal(StatusCodes.Status201Created, status);
        var task = _db.Tasks.AsNoTracking().Single();
        Assert.Equal("Buy milk", task.Title);
        Assert.Equal(new DateTime(2026, 12, 3, 17, 0, 0, DateTimeKind.Utc), task.DueUtc);
        Assert.Equal("Europe/Vienna", task.TimeZoneId);
        Assert.Equal(TaskItemStatus.InProcess, task.Status);
        Assert.Equal(1, task.Priority);

        var ics = await GetAsync("t1@phone");
        Assert.Contains("BEGIN:VTODO", ics);
        Assert.Contains("DUE;TZID=Europe/Vienna:20261203T180000", ics);
        Assert.Contains("STATUS:IN-PROCESS", ics);
        Assert.Contains("PRIORITY:1", ics);
    }

    [Fact]
    public async Task Completing_OnThePhone_IsCompletingHere()
    {
        await PutAsync("t1@phone", Todo("t1@phone", "Buy milk"));

        await PutAsync("t1@phone", Todo("t1@phone", "Buy milk", "STATUS:COMPLETED\r\nCOMPLETED:20261202T101500Z\r\n"));

        var task = _db.Tasks.AsNoTracking().Single();
        Assert.Equal(TaskItemStatus.Completed, task.Status);
        Assert.Equal(new DateTime(2026, 12, 2, 10, 15, 0, DateTimeKind.Utc), task.CompletedAtUtc);
    }

    [Fact]
    public async Task ASubtaskArrivingBeforeItsParent_IsLinkedWhenTheParentComes()
    {
        await PutAsync("child@phone", Todo("child@phone", "Pack", "RELATED-TO:parent@phone\r\n"));
        Assert.Null(_db.Tasks.AsNoTracking().Single().ParentId);

        await PutAsync("parent@phone", Todo("parent@phone", "Move house"));

        var rows = _db.Tasks.AsNoTracking().ToDictionary(t => t.Uid);
        Assert.Equal(rows["parent@phone"].Id, rows["child@phone"].ParentId);
    }

    [Fact]
    public async Task ADeeperTree_IsFlattenedOntoItsTopTask()
    {
        await PutAsync("a@phone", Todo("a@phone", "Move house"));
        await PutAsync("b@phone", Todo("b@phone", "Pack", "RELATED-TO:a@phone\r\n"));
        await PutAsync("c@phone", Todo("c@phone", "Pack books", "RELATED-TO:b@phone\r\n"));

        var rows = _db.Tasks.AsNoTracking().ToDictionary(t => t.Uid);
        Assert.Equal(rows["a@phone"].Id, rows["c@phone"].ParentId);
        Assert.Equal("b@phone", rows["c@phone"].RelatedToUid); // what the phone said is kept
    }

    [Fact]
    public async Task AWebSubtask_ReachesThePhone_WithItsRelatedTo()
    {
        var tasks = new TaskService(_db, TimeProvider.System);
        var parent = await tasks.CreateAsync(_userId, new SaveTaskRequest { Title = "Move house", Due = new DateTimeOffset(2026, 12, 3, 0, 0, 0, TimeSpan.Zero), DueAllDay = true });
        var child = await tasks.CreateAsync(_userId, new SaveTaskRequest { Title = "Pack", ParentId = parent.Id });
        var parentUid = _db.Tasks.AsNoTracking().Single(t => t.Id == parent.Id).Uid;
        var childUid = _db.Tasks.AsNoTracking().Single(t => t.Id == child.Id).Uid;

        Assert.Contains($"RELATED-TO:{parentUid}", await GetAsync(childUid));
        Assert.Contains("DUE;VALUE=DATE:20261203", await GetAsync(parentUid));
    }

    [Fact]
    public async Task Queries_TellTasksAndEventsApart()
    {
        await PutAsync("t1@phone", Todo("t1@phone", "Buy milk"));
        await PutAsync("e1@phone", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:e1@phone\r\nDTSTAMP:20261101T000000Z\r\n" +
            "SUMMARY:Meeting\r\nDTSTART:20261203T090000Z\r\nDTEND:20261203T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        static string Query(string comp) =>
            "<?xml version=\"1.0\"?><c:calendar-query xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:d=\"DAV:\">" +
            "<d:prop><d:getetag/><c:calendar-data/></d:prop><c:filter><c:comp-filter name=\"VCALENDAR\">" +
            $"<c:comp-filter name=\"{comp}\"/></c:comp-filter></c:filter></c:calendar-query>";

        var todos = await ReportAsync(Query("VTODO"));
        Assert.Equal(["t1%40phone.ics"], todos.Descendants(D + "href").Select(h => h.Value.Split('/').Last()));
        var events = await ReportAsync(Query("VEVENT"));
        Assert.Equal(["e1%40phone.ics"], events.Descendants(D + "href").Select(h => h.Value.Split('/').Last()));

        var multiget = await ReportAsync(
            "<?xml version=\"1.0\"?><c:calendar-multiget xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:d=\"DAV:\">" +
            $"<d:prop><c:calendar-data/></d:prop><d:href>/dav/calendars/{_calId:N}/t1%40phone.ics</d:href></c:calendar-multiget>");
        Assert.Contains("BEGIN:VTODO", multiget.Descendants(C + "calendar-data").Single().Value);
    }

    [Fact]
    public async Task ATaskAndAnEvent_CantShareAName()
    {
        await PutAsync("x@phone", Todo("x@phone", "Task"));

        var status = await PutAsync("x@phone", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:x@phone\r\nDTSTAMP:20261101T000000Z\r\n" +
            "SUMMARY:Event\r\nDTSTART:20261203T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        Assert.Equal(StatusCodes.Status409Conflict, status);
    }

    [Fact]
    public async Task IncrementalSync_ReportsChangedAndDeletedTasks()
    {
        await PutAsync("a@phone", Todo("a@phone", "A"));
        await PutAsync("b@phone", Todo("b@phone", "B"));
        var first = await ReportAsync("<?xml version=\"1.0\"?><d:sync-collection xmlns:d=\"DAV:\"><d:sync-token/><d:prop><d:getetag/></d:prop></d:sync-collection>");
        var token = first.Root!.Element(D + "sync-token")!.Value;
        Assert.Equal(2, first.Descendants(D + "response").Count());
        foreach (var t in _db.Tasks) t.UpdatedAt = t.UpdatedAt.AddMinutes(-10);
        await _db.SaveChangesAsync();

        await PutAsync("a@phone", Todo("a@phone", "A changed"));
        var ctx = Context();
        await RunAsync(await _handler.DeleteEvent(_calId, "b@phone.ics", ctx), ctx);
        Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);
        foreach (var d in _db.DeletedResources) d.DeletedAtUtc = DateTime.UtcNow; // (kept recent)

        var next = await ReportAsync($"<?xml version=\"1.0\"?><d:sync-collection xmlns:d=\"DAV:\"><d:sync-token>{token}</d:sync-token><d:prop><d:getetag/></d:prop></d:sync-collection>");
        var responses = next.Descendants(D + "response").Select(r => r.ToString()).ToList();
        Assert.Contains(responses, r => r.Contains("a%40phone.ics") && r.Contains("200 OK"));
        Assert.Contains(responses, r => r.Contains("b%40phone.ics") && r.Contains("404"));
    }

    [Fact]
    public async Task TheCalendar_SaysItHoldsTasks()
    {
        var ctx = Context("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:supported-calendar-component-set/></d:prop></d:propfind>");
        ctx.Request.Headers["Depth"] = "0";
        var xml = await RunAsync(await _handler.PropfindCalendar(_calId, ctx), ctx);

        Assert.Contains("VTODO", xml);
        Assert.Contains("VEVENT", xml);
    }
}
