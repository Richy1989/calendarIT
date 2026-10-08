using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ICalCalendar = Ical.Net.Calendar;

namespace CalendarIT.Tests;

/// <summary>
/// A calendar's default category (GitHub #2): events without a category of their own show in it —
/// in the grid, the counts, and every export — and keep following it when it changes.
/// </summary>
public sealed class CalendarDefaultCategoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();

    private static readonly DateTimeOffset Day = new(2026, 12, 25, 0, 0, 0, TimeSpan.Zero);

    public CalendarDefaultCategoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "cat@test", NormalizedUserName = "CAT@TEST" });
        _db.Users.Add(new ApplicationUser { Id = _otherUserId, UserName = "other@test", NormalizedUserName = "OTHER@TEST" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private CalendarService Calendars() => new(_db, TimeProvider.System);
    private CategoryService Categories() => new(_db, TimeProvider.System);
    private CalendarIoService Io() => new(_db, TimeProvider.System);
    private EventService Events() =>
        new(_db, TimeProvider.System, new FakeInvitationMailer(), new InternalInvitationDelivery(_db, TimeProvider.System));

    private async Task<CategoryDto> CategoryAsync(string name, string color, Guid? owner = null) =>
        (await Categories().CreateAsync(owner ?? _userId, new SaveCategoryRequest { Name = name, Color = color })).Category!;

    private static string Holidays(params (string Uid, string Summary, string? Category)[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n" +
        string.Concat(events.Select(e =>
            $"BEGIN:VEVENT\r\nUID:{e.Uid}\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;VALUE=DATE:20261225\r\n" +
            $"SUMMARY:{e.Summary}\r\n" + (e.Category is null ? "" : $"CATEGORIES:{e.Category}\r\n") +
            "COLOR:red\r\nEND:VEVENT\r\n")) +
        "END:VCALENDAR\r\n";

    private async Task<List<EventDto>> DecemberAsync() =>
        [.. await Events().GetEventsAsync(_userId, Day.AddDays(-10), Day.AddDays(10))];

    [Fact]
    public async Task ImportingIntoANewCalendarWithACategory_ColorsEveryEventWithoutOne()
    {
        var holiday = await CategoryAsync("Holiday", "#3CB371");
        var work = await CategoryAsync("Work", "#6495ED");

        await Io().ImportAsync(_userId,
            Holidays(("xmas", "Christmas", null), ("boxing", "Boxing Day", null), ("party", "Office party", "Work")),
            newCalendarName: "Holidays", newCalendarCategoryId: holiday.Id);

        var events = await DecemberAsync();
        var xmas = events.Single(e => e.Title == "Christmas");
        // The file's COLOR:red is not snapped to some nearest category: the calendar's default wins.
        Assert.Null(xmas.CategoryId);
        Assert.Equal(holiday.Id, xmas.EffectiveCategoryId);
        Assert.Equal("#3CB371", xmas.Color);
        // An event with a category of its own keeps it.
        Assert.Equal(work.Id, events.Single(e => e.Title == "Office party").EffectiveCategoryId);
    }

    [Fact]
    public async Task ChangingTheCalendarsCategory_RecolorsItsEvents()
    {
        var holiday = await CategoryAsync("Holiday", "#3CB371");
        var red = await CategoryAsync("Red days", "#FF6347");
        await Io().ImportAsync(_userId, Holidays(("xmas", "Christmas", null)), newCalendarName: "Holidays",
            newCalendarCategoryId: holiday.Id);
        var calendarId = (await DecemberAsync()).Single().CalendarId;

        await Calendars().SetDefaultCategoryAsync(_userId, calendarId, red.Id);
        Assert.Equal("#FF6347", (await DecemberAsync()).Single().Color);

        await Calendars().SetDefaultCategoryAsync(_userId, calendarId, null);
        var cleared = (await DecemberAsync()).Single();
        Assert.Null(cleared.EffectiveCategoryId);
    }

    [Fact]
    public async Task CategoryCounts_IncludeEventsThatInheritIt()
    {
        var holiday = await CategoryAsync("Holiday", "#3CB371");
        await Io().ImportAsync(_userId, Holidays(("a", "A", null), ("b", "B", null)), newCalendarName: "Holidays",
            newCalendarCategoryId: holiday.Id);

        var listed = (await Categories().ListAsync(_userId)).Single(c => c.Id == holiday.Id);
        Assert.Equal(2, listed.EventCount);
    }

    [Fact]
    public async Task Export_WritesTheInheritedCategory_AndReadingItBackKeepsItInherited()
    {
        var holiday = await CategoryAsync("Holiday", "#3CB371");
        await Io().ImportAsync(_userId, Holidays(("xmas", "Christmas", null)), newCalendarName: "Holidays",
            newCalendarCategoryId: holiday.Id);

        var exported = await Io().ExportAsync(_userId);
        var ve = ICalCalendar.Load(exported)!.Events.Single();
        Assert.Equal("Holiday", ICalEventMapper.ReadCategoryName(ve));

        // A client echoing that category back (a CalDAV PUT, a re-import) leaves the event inheriting.
        var row = await _db.Events.Include(e => e.Calendar).SingleAsync();
        ICalEventMapper.Apply(ve, row, DateTime.UtcNow, await _db.Categories.ToListAsync(), row.Calendar!.DefaultCategoryId);
        Assert.Null(row.CategoryId);
    }

    [Fact]
    public async Task SomeoneElsesCategory_CannotBeAssigned()
    {
        var theirs = await CategoryAsync("Theirs", "#3CB371", _otherUserId);
        var calendar = await Calendars().CreateAsync(_userId, new SaveCalendarRequest { Name = "Mine" });

        await Assert.ThrowsAsync<InvalidInputException>(() =>
            Calendars().SetDefaultCategoryAsync(_userId, calendar.Id, theirs.Id));
        await Assert.ThrowsAsync<InvalidInputException>(() =>
            Calendars().CreateAsync(_userId, new SaveCalendarRequest { Name = "X", DefaultCategoryId = theirs.Id }));
    }

    [Fact]
    public async Task DeletingTheCategory_LeavesTheCalendarWithoutADefault()
    {
        var holiday = await CategoryAsync("Holiday", "#3CB371");
        var calendar = await Calendars().CreateAsync(_userId, new SaveCalendarRequest { Name = "Holidays", DefaultCategoryId = holiday.Id });
        Assert.Equal(holiday.Id, calendar.DefaultCategoryId);

        await Categories().DeleteAsync(_userId, holiday.Id);
        _db.ChangeTracker.Clear();

        Assert.Null((await Calendars().ListAsync(_userId)).Single(c => c.Id == calendar.Id).DefaultCategoryId);
    }
}
