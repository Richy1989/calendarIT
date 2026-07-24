using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// A time-zone id the runtime doesn't know is ordinary input — it arrives from an emailed
/// invitation, an imported .ics, or a CalDAV PUT, none of which we control. These pin down that
/// such an id degrades to a floating time instead of throwing, because a throw here used to
/// mean a calendar that wouldn't load and an inbox scan that never got past the offending
/// message (it was retried, and re-thrown, on every scan).
/// </summary>
public sealed class TimeZoneResilienceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EventService _events;
    private readonly CalendarIoService _io;
    private readonly IncomingInvitationService _incoming;
    private readonly Guid _userId = Guid.NewGuid();

    private static readonly DateTimeOffset Start = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    public TimeZoneResilienceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "tz@test", NormalizedUserName = "TZ@TEST" });
        _db.SaveChanges();
        _events = new EventService(_db, TimeProvider.System, new FakeInvitationMailer(),
            new InternalInvitationDelivery(_db, TimeProvider.System));
        _io = new CalendarIoService(_db, TimeProvider.System);
        _incoming = new IncomingInvitationService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Normalize_KeepsRealZones_AndDropsInventedOnes()
    {
        Assert.Equal("Europe/Berlin", TimeZones.Normalize("Europe/Berlin"));
        Assert.Equal("UTC", TimeZones.Normalize("UTC"));
        Assert.Null(TimeZones.Normalize("Totally/Bogus"));
        Assert.Null(TimeZones.Normalize(""));
        Assert.Null(TimeZones.Normalize(null));
    }

    [Fact]
    public async Task SavingAnUnknownZone_StoresItAsFloating_AndListingStillWorks()
    {
        var created = await _events.CreateAsync(_userId, new SaveEventRequest
        {
            Title = "Poison",
            Start = Start,
            End = Start.AddMinutes(30),
            Recurrence = "FREQ=DAILY;COUNT=3",
            TimeZone = "Totally/Bogus",
        });

        var stored = await _db.Events.AsNoTracking().SingleAsync(e => e.Id == created.Id);
        Assert.Null(stored.TimeZoneId);

        var listed = await _events.GetEventsAsync(_userId, Start.AddDays(-1), Start.AddDays(10));
        Assert.Equal(3, listed.Count);
    }

    [Fact]
    public async Task StoredUnknownZone_DoesNotBreakExpansion()
    {
        // A row written before ids were normalised on save — the guard in the expander is what
        // keeps an existing install's calendar loading.
        var calendar = await DefaultCalendar.GetOrCreateAsync(_db, TimeProvider.System, _userId);
        _db.Events.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = calendar.Id,
            Uid = "legacy@calendarit",
            Title = "Legacy",
            StartUtc = Start.UtcDateTime,
            EndUtc = Start.AddMinutes(30).UtcDateTime,
            TimeZoneId = "Totally/Bogus",
            RRule = "FREQ=DAILY;COUNT=2",
            CreatedAt = Start.UtcDateTime,
        });
        await _db.SaveChangesAsync();

        var listed = await _events.GetEventsAsync(_userId, Start.AddDays(-1), Start.AddDays(10));
        Assert.Equal(2, listed.Count);

        // Export walks the same zone lookup from the other side.
        var ics = await _io.ExportAsync(_userId, null);
        Assert.Contains("Legacy", ics);
    }

    [Fact]
    public async Task InvitationWithInventedZone_IsAccepted_NotRejected()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            METHOD:REQUEST
            BEGIN:VEVENT
            UID:invented-zone@example.com
            DTSTAMP:20260801T090000Z
            DTSTART;TZID=Totally/Bogus:20260803T090000
            DTEND;TZID=Totally/Bogus:20260803T093000
            RRULE:FREQ=DAILY;COUNT=3
            SUMMARY:Lunch
            ORGANIZER:mailto:someone@example.com
            END:VEVENT
            END:VCALENDAR
            """;

        var request = ImipRequestParser.TryParse(ics);
        Assert.NotNull(request);
        Assert.True(await _incoming.ApplyRequestAsync(_userId, request!));

        var stored = await _db.Events.AsNoTracking().SingleAsync(e => e.Uid == "invented-zone@example.com");
        Assert.Null(stored.TimeZoneId);                                 // floating, not the invented id
        Assert.Equal(new DateTime(2026, 8, 3, 9, 0, 0), stored.StartUtc); // wall clock read as UTC

        var listed = await _events.GetEventsAsync(_userId, Start.AddDays(-1), Start.AddDays(10));
        Assert.Equal(3, listed.Count);
    }

    [Fact]
    public async Task ImportWithInventedZone_Succeeds()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:imported-zone@example.com
            DTSTAMP:20260801T090000Z
            DTSTART;TZID=Mars/Olympus:20260803T090000
            DTEND;TZID=Mars/Olympus:20260803T093000
            SUMMARY:Rover sync
            END:VEVENT
            END:VCALENDAR
            """;

        var result = await _io.ImportAsync(_userId, ics, null, null);

        Assert.Equal(1, result.Imported);
        var stored = await _db.Events.AsNoTracking().SingleAsync(e => e.Uid == "imported-zone@example.com");
        Assert.Null(stored.TimeZoneId);
    }
}
