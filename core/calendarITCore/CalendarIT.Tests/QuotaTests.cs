using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// Per-account storage limits: with open sign-up anyone has an account, and none may fill the
/// disk. A write over the limit is refused with a reason and leaves nothing half-done behind.
/// </summary>
public sealed class QuotaTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly QuotaOptions _quotas = new() { Events = 3, Tasks = 2, Calendars = 2, Categories = 2 };

    public QuotaTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "q@test", NormalizedUserName = "Q@TEST" });
        _db.SaveChanges();
        DefaultCalendar.GetOrCreateAsync(_db, TimeProvider.System, _userId).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private EventService Events() =>
        new(_db, TimeProvider.System, new FakeInvitationMailer(), new InternalInvitationDelivery(_db, TimeProvider.System), _quotas);

    private static SaveEventRequest Event(string title) => new()
    {
        Title = title, Start = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero), End = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
    };

    private static string Ics(int events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" +
        string.Concat(Enumerable.Range(0, events).Select(i =>
            $"BEGIN:VEVENT\r\nUID:import-{i}@test\r\nDTSTAMP:20260901T080000Z\r\nDTSTART:20260901T090000Z\r\nDTEND:20260901T100000Z\r\nSUMMARY:Imported {i}\r\nEND:VEVENT\r\n")) +
        "END:VCALENDAR\r\n";

    [Fact]
    public async Task Events_OverTheLimit_AreRefused()
    {
        var events = Events();
        for (var i = 0; i < 3; i++)
        {
            await events.CreateAsync(_userId, Event($"E{i}"));
        }

        var refused = await Assert.ThrowsAsync<QuotaExceededException>(() => events.CreateAsync(_userId, Event("one too many")));
        Assert.Contains("limit", refused.Message);
        Assert.IsAssignableFrom<InvalidInputException>(refused); // a 400 the user can act on
        Assert.Equal(3, await _db.Events.CountAsync());
    }

    [Fact]
    public async Task AnImportOverTheLimit_IsRefusedWhole_AndCreatesNoCalendar()
    {
        var io = new CalendarIoService(_db, TimeProvider.System, _quotas);

        await Assert.ThrowsAsync<QuotaExceededException>(() => io.ImportAsync(_userId, Ics(4), newCalendarName: "Imported"));

        Assert.Equal(0, await _db.Events.CountAsync());
        Assert.Equal(1, await _db.Calendars.CountAsync()); // only the default one
        Assert.Equal(3, (await io.ImportAsync(_userId, Ics(3))).Imported);
    }

    [Fact]
    public async Task Calendars_Tasks_AndCategories_HaveLimitsToo()
    {
        var calendars = new CalendarService(_db, TimeProvider.System, _quotas);
        await calendars.CreateAsync(_userId, new SaveCalendarRequest { Name = "Second" });
        await Assert.ThrowsAsync<QuotaExceededException>(() => calendars.CreateAsync(_userId, new SaveCalendarRequest { Name = "Third" }));

        var tasks = new TaskService(_db, TimeProvider.System, _quotas);
        await tasks.CreateAsync(_userId, new SaveTaskRequest { Title = "One" });
        await tasks.CreateAsync(_userId, new SaveTaskRequest { Title = "Two" });
        await Assert.ThrowsAsync<QuotaExceededException>(() => tasks.CreateAsync(_userId, new SaveTaskRequest { Title = "Three" }));

        var categories = new CategoryService(_db, TimeProvider.System, _quotas);
        await categories.CreateAsync(_userId, new SaveCategoryRequest { Name = "A", Color = "#112233" });
        await categories.CreateAsync(_userId, new SaveCategoryRequest { Name = "B", Color = "#112233" });
        await Assert.ThrowsAsync<QuotaExceededException>(() =>
            categories.CreateAsync(_userId, new SaveCategoryRequest { Name = "C", Color = "#112233" }));
    }

    [Fact]
    public async Task InvitationsReceived_DontCountAgainstTheGuest()
    {
        var calendarId = await _db.Calendars.Select(c => c.Id).SingleAsync();
        for (var i = 0; i < 5; i++)
        {
            _db.Events.Add(new CalendarIT.Domain.CalendarEvent
            {
                Id = Guid.NewGuid(), CalendarId = calendarId, Uid = $"copy-{i}", Title = "Someone else's meeting",
                StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow, SourceOrganizerUserId = Guid.NewGuid(),
            });
        }
        await _db.SaveChangesAsync();

        await Events().CreateAsync(_userId, Event("Still room"));
    }
}
