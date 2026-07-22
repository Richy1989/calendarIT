using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Ical.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// iCalendar's all-day DTEND is exclusive, but the app stores the inclusive last day
/// (the convention the UI uses: a one-day event has end == start). These tests pin the
/// conversion both ways so a one-day import stays one day and round-trips unchanged.
/// </summary>
public sealed class CalendarIoServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly CalendarIoService _service;
    private readonly Guid _userId = Guid.NewGuid();

    public CalendarIoServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "test", NormalizedUserName = "TEST" });
        _db.SaveChanges();
        _service = new CalendarIoService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static string Ics(string body) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n{body}\r\nEND:VCALENDAR\r\n";

    [Fact]
    public async Task Import_OneDayAllDayEvent_StoresInclusiveEndEqualToStart()
    {
        var ics = Ics("BEGIN:VEVENT\r\nUID:one-day@test\r\nSUMMARY:One day\r\nDTSTART;VALUE=DATE:20260801\r\nDTEND;VALUE=DATE:20260802\r\nEND:VEVENT");

        var result = await _service.ImportAsync(_userId, ics);

        Assert.Equal(1, result.Imported);
        var stored = await _db.Events.SingleAsync(e => e.Uid == "one-day@test");
        Assert.True(stored.IsAllDay);
        Assert.Equal(new DateTime(2026, 8, 1), stored.StartUtc);
        Assert.Equal(new DateTime(2026, 8, 1), stored.EndUtc);
    }

    [Fact]
    public async Task Import_ThreeDayAllDayEvent_StoresInclusiveLastDay()
    {
        var ics = Ics("BEGIN:VEVENT\r\nUID:three-day@test\r\nSUMMARY:Three days\r\nDTSTART;VALUE=DATE:20260801\r\nDTEND;VALUE=DATE:20260804\r\nEND:VEVENT");

        await _service.ImportAsync(_userId, ics);

        var stored = await _db.Events.SingleAsync(e => e.Uid == "three-day@test");
        Assert.Equal(new DateTime(2026, 8, 3), stored.EndUtc);
    }

    [Fact]
    public async Task Import_AllDayEventWithoutDtend_KeepsNullEnd()
    {
        var ics = Ics("BEGIN:VEVENT\r\nUID:no-end@test\r\nSUMMARY:No end\r\nDTSTART;VALUE=DATE:20260801\r\nEND:VEVENT");

        await _service.ImportAsync(_userId, ics);

        var stored = await _db.Events.SingleAsync(e => e.Uid == "no-end@test");
        Assert.True(stored.IsAllDay);
        Assert.Null(stored.EndUtc);
    }

    [Fact]
    public async Task Import_BrokenZeroLengthDtend_ClampsToStart()
    {
        // Some producers emit DTEND == DTSTART for a one-day event (off-spec but seen in the wild).
        var ics = Ics("BEGIN:VEVENT\r\nUID:zero-len@test\r\nSUMMARY:Zero length\r\nDTSTART;VALUE=DATE:20260801\r\nDTEND;VALUE=DATE:20260801\r\nEND:VEVENT");

        await _service.ImportAsync(_userId, ics);

        var stored = await _db.Events.SingleAsync(e => e.Uid == "zero-len@test");
        Assert.Equal(stored.StartUtc, stored.EndUtc);
    }

    [Fact]
    public async Task Export_OneDayAllDayEvent_WritesExclusiveDtend()
    {
        var ics = Ics("BEGIN:VEVENT\r\nUID:round-trip@test\r\nSUMMARY:Round trip\r\nDTSTART;VALUE=DATE:20260801\r\nDTEND;VALUE=DATE:20260802\r\nEND:VEVENT");
        await _service.ImportAsync(_userId, ics);

        var exported = Calendar.Load(await _service.ExportAsync(_userId))!;

        var ve = Assert.Single(exported.Events);
        Assert.False(ve.Start!.HasTime);
        Assert.Equal(new DateOnly(2026, 8, 1), DateOnly.FromDateTime(ve.Start.Value));
        Assert.Equal(new DateOnly(2026, 8, 2), DateOnly.FromDateTime(ve.End!.Value)); // exclusive again
    }

    [Fact]
    public async Task ImportExport_TimedEvent_RoundTripsUnchanged()
    {
        var ics = Ics("BEGIN:VEVENT\r\nUID:timed@test\r\nSUMMARY:Timed\r\nDTSTART:20260801T090000Z\r\nDTEND:20260801T103000Z\r\nEND:VEVENT");

        await _service.ImportAsync(_userId, ics);

        var stored = await _db.Events.SingleAsync(e => e.Uid == "timed@test");
        Assert.False(stored.IsAllDay);
        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), stored.StartUtc);
        Assert.Equal(new DateTime(2026, 8, 1, 10, 30, 0), stored.EndUtc);

        var exported = Calendar.Load(await _service.ExportAsync(_userId))!;
        var ve = Assert.Single(exported.Events);
        Assert.Equal(stored.StartUtc, ve.Start!.AsUtc);
        Assert.Equal(stored.EndUtc, ve.End!.AsUtc);
    }
}
