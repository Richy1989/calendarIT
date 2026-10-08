using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using CalendarIT.Application;
using CalendarIT.CalDav;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ICalCalendar = Ical.Net.Calendar;

namespace CalendarIT.Tests;

/// <summary>
/// A series travels as one iCalendar resource — master plus RECURRENCE-ID overrides — through
/// CalDAV, .ics files and email. These pin that every one of those paths keeps the whole series,
/// and that text from outside can't overflow a column or break the write.
/// </summary>
public sealed class SeriesSyncTests : IDisposable
{
    private static readonly XNamespace D = "DAV:";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly CalDavHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly ServiceProvider _services;

    public SeriesSyncTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "sync@test", NormalizedUserName = "SYNC@TEST" });
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

    private DefaultHttpContext Context(string? body = null, string? ifMatch = null)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _userId.ToString()), new Claim(ClaimTypes.Name, "sync@test")], "Test")),
            RequestServices = _services,
        };
        ctx.Request.Headers["Depth"] = "1";
        if (ifMatch is not null)
        {
            ctx.Request.Headers.IfMatch = ifMatch;
        }
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

    private async Task<Guid> CalendarIdAsync()
    {
        var ctx = Context();
        var body = await ExecuteAsync(await _handler.PropfindHome(ctx), ctx);
        var href = XDocument.Parse(body).Descendants(D + "href")
            .Select(h => h.Value)
            .First(h => h.StartsWith("/dav/calendars/") && h.Length > "/dav/calendars/".Length);
        return Guid.ParseExact(href.TrimEnd('/').Split('/').Last(), "N");
    }

    private static string Wrap(params string[] vevents) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";

    private const string Master =
        "BEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20261001T000000Z\r\nDTSTART;TZID=Europe/Berlin:20261001T100000\r\n" +
        "DTEND;TZID=Europe/Berlin:20261001T110000\r\nRRULE:FREQ=DAILY;COUNT=5\r\n" +
        "EXDATE;TZID=Europe/Berlin:20261002T100000\r\nSUMMARY:Daily standup\r\nEND:VEVENT\r\n";

    private const string Override =
        "BEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20261001T000000Z\r\nRECURRENCE-ID;TZID=Europe/Berlin:20261003T100000\r\n" +
        "DTSTART;TZID=Europe/Berlin:20261003T150000\r\nDTEND;TZID=Europe/Berlin:20261003T160000\r\nSUMMARY:Moved standup\r\nEND:VEVENT\r\n";

    private async Task<int> PutAsync(Guid calId, string ics, string resource = "series-1.ics", string? ifMatch = null)
    {
        var ctx = Context(ics, ifMatch);
        await ExecuteAsync(await _handler.PutEvent(calId, resource, ctx), ctx);
        return ctx.Response.StatusCode;
    }

    private EventService Events() =>
        new(_db, TimeProvider.System, new FakeInvitationMailer(), new InternalInvitationDelivery(_db, TimeProvider.System));

    private async Task<List<Application.Calendars.EventDto>> OctoberAsync() =>
        [.. await Events().GetEventsAsync(_userId,
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero))];

    [Fact]
    public async Task Put_OverrideListedFirst_KeepsTheWholeSeries()
    {
        var calId = await CalendarIdAsync();

        Assert.Equal(StatusCodes.Status201Created, await PutAsync(calId, Wrap(Override, Master)));

        var october = await OctoberAsync();
        // 5 occurrences − 1 EXDATE, one of them moved to the afternoon.
        Assert.Equal(4, october.Count);
        Assert.Equal(3, october.Count(e => e.Title == "Daily standup"));
        var moved = Assert.Single(october, e => e.Title == "Moved standup");
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 13, 0, 0, TimeSpan.Zero), moved.Start);
    }

    [Fact]
    public async Task Get_ReturnsTheSeriesWithItsOverride_AndExdateWithoutTheOverriddenInstant()
    {
        var calId = await CalendarIdAsync();
        await PutAsync(calId, Wrap(Master, Override));

        var ctx = Context();
        var ics = await ExecuteAsync(await _handler.GetEvent(calId, "series-1.ics", ctx), ctx);

        var parsed = ICalCalendar.Load(ics)!;
        Assert.Equal(2, parsed.Events.Count);
        var master = parsed.Events.Single(e => e.RecurrenceIdentifier is null);
        var exdates = master.ExceptionDates.GetAllDates().Select(d => d.AsUtc).ToList();
        Assert.Equal([new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc)], exdates);
        Assert.Single(parsed.Events, e => e.RecurrenceIdentifier is not null && e.Summary == "Moved standup");
    }

    [Fact]
    public async Task Put_DroppingAnOverride_BringsTheOccurrenceBack()
    {
        var calId = await CalendarIdAsync();
        await PutAsync(calId, Wrap(Master, Override));

        await PutAsync(calId, Wrap(Master));

        var october = await OctoberAsync();
        Assert.Equal(4, october.Count);
        Assert.All(october, e => Assert.Equal("Daily standup", e.Title));
        Assert.Empty(_db.Events.AsNoTracking().Where(e => e.SeriesMasterId != null));
    }

    [Fact]
    public async Task Propfind_ListsTheSeriesOnce()
    {
        var calId = await CalendarIdAsync();
        await PutAsync(calId, Wrap(Master, Override));

        var ctx = Context();
        var body = await ExecuteAsync(await _handler.PropfindCalendar(calId, ctx), ctx);

        Assert.Single(XDocument.Parse(body).Descendants(D + "href"), h => h.Value.EndsWith("series-1.ics"));
    }

    [Fact]
    public async Task ETag_SurvivesTheDatabaseKeepingOnlyMicroseconds()
    {
        // Postgres stores timestamps at microsecond precision; the tag handed out on PUT must
        // still match after the row is read back truncated, or the next edit gets a 412.
        var calId = await CalendarIdAsync();
        var ctx = Context(Wrap(Master));
        await ExecuteAsync(await _handler.PutEvent(calId, "series-1.ics", ctx), ctx);
        var etag = ctx.Response.Headers.ETag.ToString();

        var row = await _db.Events.SingleAsync(e => e.Uid == "series-1");
        row.UpdatedAt = new DateTime(row.UpdatedAt.Ticks - row.UpdatedAt.Ticks % 10, DateTimeKind.Utc);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(StatusCodes.Status204NoContent,
            await PutAsync(calId, Wrap(Master.Replace("Daily standup", "Renamed")), ifMatch: etag));
    }

    [Fact]
    public async Task Put_OverlongText_IsClippedToTheColumn()
    {
        var calId = await CalendarIdAsync();
        var title = new string('A', 700);
        await PutAsync(calId, Wrap(Master.Replace("SUMMARY:Daily standup", $"SUMMARY:{title}")));

        var stored = await _db.Events.AsNoTracking().SingleAsync(e => e.Uid == "series-1");
        Assert.Equal(500, stored.Title.Length);
    }

    [Fact]
    public async Task Put_OverlongUid_IsRefused()
    {
        var calId = await CalendarIdAsync();
        var uid = new string('u', 300);
        Assert.Equal(StatusCodes.Status400BadRequest,
            await PutAsync(calId, Wrap(Master.Replace("UID:series-1", $"UID:{uid}")), resource: "long.ics"));
    }

    [Fact]
    public async Task Put_SecondlyRule_IsStoredAsAOneOff()
    {
        var calId = await CalendarIdAsync();
        await PutAsync(calId, Wrap(Master.Replace("RRULE:FREQ=DAILY;COUNT=5", "RRULE:FREQ=SECONDLY")));

        var stored = await _db.Events.AsNoTracking().SingleAsync(e => e.Uid == "series-1");
        Assert.Null(stored.RRule);
    }

    [Fact]
    public async Task Put_TooLargeBody_IsRefused()
    {
        var calId = await CalendarIdAsync();
        var huge = Wrap(Master.Replace("SUMMARY:Daily standup", "DESCRIPTION:" + new string('x', 2 * 1024 * 1024)));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, await PutAsync(calId, huge));
    }

    // ---------------------------------------------------------------- .ics import / export

    private CalendarIoService Io() => new(_db, TimeProvider.System);

    [Fact]
    public async Task Import_GarbageFile_IsAnInputError()
    {
        await Assert.ThrowsAsync<InvalidInputException>(() => Io().ImportAsync(_userId, "this is not ics"));
        Assert.Empty(_db.Calendars.Where(c => c.Name == "Should not exist"));
    }

    [Fact]
    public async Task Import_SeriesWithOverride_ImportsOneSeries()
    {
        var result = await Io().ImportAsync(_userId, Wrap(Override, Master));

        Assert.Equal(1, result.Imported);
        Assert.Equal(4, (await OctoberAsync()).Count);
        Assert.Single(_db.Events.AsNoTracking().Where(e => e.SeriesMasterId != null));
    }

    [Fact]
    public async Task Import_OneOverlongEvent_DoesNotFailTheRest()
    {
        var longOne = Master.Replace("UID:series-1", "UID:long").Replace("SUMMARY:Daily standup", "SUMMARY:" + new string('L', 900));
        var result = await Io().ImportAsync(_userId, Wrap(Master, longOne));
        Assert.Equal(2, result.Imported);
    }

    [Fact]
    public async Task Export_RoundTripsTheOverride()
    {
        await Io().ImportAsync(_userId, Wrap(Master, Override));

        var ics = await Io().ExportAsync(_userId);

        var parsed = ICalCalendar.Load(ics)!;
        Assert.Equal(2, parsed.Events.Count(e => e.Uid == "series-1"));
        Assert.Contains(parsed.Events, e => e.RecurrenceIdentifier is not null);
    }

    // ---------------------------------------------------------------- iMIP

    [Fact]
    public async Task IncomingInvitation_WithOverride_LandsAsAWholeSeries()
    {
        var request = ImipRequestParser.TryParse(Wrap(Master, Override).Replace("VERSION:2.0", "VERSION:2.0\r\nMETHOD:REQUEST"))!;
        Assert.False(request.IsInstanceOnly);

        Assert.True(await new IncomingInvitationService(_db, TimeProvider.System).ApplyRequestAsync(_userId, request));

        var october = await OctoberAsync();
        Assert.Equal(4, october.Count);
        Assert.All(october, e => Assert.Equal("NeedsAction", e.InvitationStatus));
    }

    [Fact]
    public async Task IncomingCancel_ForOneInstance_RemovesOnlyThatInstance()
    {
        var service = new IncomingInvitationService(_db, TimeProvider.System);
        var invite = ImipRequestParser.TryParse(Wrap(Master).Replace("VERSION:2.0", "VERSION:2.0\r\nMETHOD:REQUEST"))!;
        await service.ApplyRequestAsync(_userId, invite);

        var cancel = ImipRequestParser.TryParse(Wrap(Override).Replace("VERSION:2.0", "VERSION:2.0\r\nMETHOD:CANCEL"))!;
        Assert.True(cancel.IsInstanceOnly);
        await service.ApplyRequestAsync(_userId, cancel);

        Assert.Equal(3, (await OctoberAsync()).Count);
    }
}
