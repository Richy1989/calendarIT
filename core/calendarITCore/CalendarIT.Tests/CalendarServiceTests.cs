using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

public sealed class CalendarServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly CalendarService _service;
    private readonly EventService _events;
    private readonly Guid _userId = Guid.NewGuid();

    public CalendarServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "cal@test", NormalizedUserName = "CAL@TEST" });
        _db.SaveChanges();
        _service = new CalendarService(_db, TimeProvider.System);
        _events = new EventService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task List_FreshUser_CreatesDefaultCalendar()
    {
        var calendars = await _service.ListAsync(_userId);
        var only = Assert.Single(calendars);
        Assert.Equal("Personal", only.Name);
    }

    [Fact]
    public async Task CreateRenameDelete_RoundTrip()
    {
        await _service.ListAsync(_userId); // bootstraps "Personal"
        var work = await _service.CreateAsync(_userId, new SaveCalendarRequest { Name = "Work" });

        var renamed = await _service.RenameAsync(_userId, work.Id, new SaveCalendarRequest { Name = "Office" });
        Assert.Equal("Office", renamed!.Name);

        Assert.Equal(DeleteCalendarResult.Deleted, await _service.DeleteAsync(_userId, work.Id));
        Assert.Single(await _service.ListAsync(_userId));
    }

    [Fact]
    public async Task Delete_LastCalendar_IsRefused()
    {
        var only = Assert.Single(await _service.ListAsync(_userId));
        Assert.Equal(DeleteCalendarResult.LastCalendar, await _service.DeleteAsync(_userId, only.Id));
    }

    [Fact]
    public async Task Delete_CascadesEvents()
    {
        await _service.ListAsync(_userId);
        var work = await _service.CreateAsync(_userId, new SaveCalendarRequest { Name = "Work" });
        await _events.CreateAsync(_userId, new SaveEventRequest
        {
            Title = "Standup",
            Start = DateTimeOffset.UtcNow,
            CalendarId = work.Id,
        });

        Assert.Equal(DeleteCalendarResult.Deleted, await _service.DeleteAsync(_userId, work.Id));
        Assert.Empty(await _db.Events.ToListAsync());
    }

    [Fact]
    public async Task EventCreate_TargetsRequestedCalendar_AndUpdateMovesIt()
    {
        var personal = Assert.Single(await _service.ListAsync(_userId));
        var work = await _service.CreateAsync(_userId, new SaveCalendarRequest { Name = "Work" });

        var created = await _events.CreateAsync(_userId, new SaveEventRequest
        {
            Title = "Standup",
            Start = DateTimeOffset.UtcNow,
            CalendarId = work.Id,
        });
        Assert.Equal(work.Id, created.CalendarId);

        // Update without CalendarId leaves the event where it is…
        var kept = await _events.UpdateAsync(_userId, created.Id, new SaveEventRequest
        {
            Title = "Standup (moved time)",
            Start = DateTimeOffset.UtcNow.AddHours(1),
        });
        Assert.Equal(work.Id, kept!.CalendarId);

        // …and a provided one moves it.
        var moved = await _events.UpdateAsync(_userId, created.Id, new SaveEventRequest
        {
            Title = "Standup",
            Start = DateTimeOffset.UtcNow,
            CalendarId = personal.Id,
        });
        Assert.Equal(personal.Id, moved!.CalendarId);
    }

    [Fact]
    public async Task EventCreate_ForeignCalendarId_FallsBackToDefault()
    {
        var personal = Assert.Single(await _service.ListAsync(_userId));
        var created = await _events.CreateAsync(_userId, new SaveEventRequest
        {
            Title = "Sneaky",
            Start = DateTimeOffset.UtcNow,
            CalendarId = Guid.NewGuid(), // not one of the user's calendars
        });
        Assert.Equal(personal.Id, created.CalendarId);
    }
}
