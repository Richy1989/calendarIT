using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// Editing a series versus the occurrences excluded from it. Deleting one occurrence stores an
/// EXDATE; the question these cover is which later edits are allowed to discard it.
/// </summary>
public sealed class RecurrenceEditingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EventService _events;
    private readonly Guid _userId = Guid.NewGuid();

    private static readonly DateTimeOffset Start = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    public RecurrenceEditingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "rec@test", NormalizedUserName = "REC@TEST" });
        _db.SaveChanges();
        _events = new EventService(_db, TimeProvider.System, new FakeInvitationMailer(),
            new InternalInvitationDelivery(_db, TimeProvider.System));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SaveEventRequest Daily(
        string title, DateTimeOffset? start = null, string rrule = "FREQ=DAILY;COUNT=5", string? zone = "UTC") =>
        new()
        {
            Title = title,
            Start = start ?? Start,
            End = (start ?? Start).AddMinutes(30),
            Recurrence = rrule,
            TimeZone = zone,
        };

    private async Task<int> OccurrenceCountAsync() =>
        (await _events.GetEventsAsync(_userId, Start.AddDays(-1), Start.AddDays(30))).Count;

    [Fact]
    public async Task EditingSeriesDetails_KeepsDeletedOccurrenceDeleted()
    {
        var created = await _events.CreateAsync(_userId, Daily("Standup"));
        await _events.DeleteAsync(_userId, created.Id, Start.AddDays(1));
        Assert.Equal(4, await OccurrenceCountAsync());

        // A rename doesn't move the series, so the exclusion still refers to a real occurrence.
        await _events.UpdateAsync(_userId, created.Id, Daily("Standup (renamed)"));

        Assert.Equal(4, await OccurrenceCountAsync());
    }

    [Fact]
    public async Task ChangingTheRule_DropsExclusionsThatNoLongerApply()
    {
        var created = await _events.CreateAsync(_userId, Daily("Standup"));
        await _events.DeleteAsync(_userId, created.Id, Start.AddDays(1));

        // A different rule generates different instants; keeping the old EXDATE would exclude
        // whatever happened to land on it.
        await _events.UpdateAsync(_userId, created.Id, Daily("Standup", rrule: "FREQ=WEEKLY;COUNT=3"));

        var master = await _db.Events.AsNoTracking().SingleAsync(e => e.Id == created.Id);
        Assert.Null(master.ExDates);
        Assert.Equal(3, await OccurrenceCountAsync());
    }

    [Fact]
    public async Task MovingTheSeriesStart_DropsExclusions()
    {
        var created = await _events.CreateAsync(_userId, Daily("Standup"));
        await _events.DeleteAsync(_userId, created.Id, Start.AddDays(1));

        await _events.UpdateAsync(_userId, created.Id, Daily("Standup", start: Start.AddHours(2)));

        var master = await _db.Events.AsNoTracking().SingleAsync(e => e.Id == created.Id);
        Assert.Null(master.ExDates);
        Assert.Equal(5, await OccurrenceCountAsync());
    }

    [Fact]
    public async Task EditingWithoutNamingAZone_KeepsTheSeriesZone()
    {
        var created = await _events.CreateAsync(_userId, Daily("Standup", zone: "America/New_York"));
        await _events.DeleteAsync(_userId, created.Id, Start.AddDays(1));

        // An edit from a browser somewhere else must not pull the series into that zone.
        var updated = await _events.UpdateAsync(_userId, created.Id, Daily("Standup (renamed)", zone: null));

        Assert.Equal("America/New_York", updated!.TimeZone);
        Assert.Equal(4, await OccurrenceCountAsync());
    }

    [Fact]
    public async Task SendingTheSameZoneBack_KeepsExclusions()
    {
        var created = await _events.CreateAsync(_userId, Daily("Standup", zone: "America/New_York"));
        await _events.DeleteAsync(_userId, created.Id, Start.AddDays(1));

        await _events.UpdateAsync(_userId, created.Id, Daily("Standup (renamed)", zone: created.TimeZone));

        Assert.Equal(4, await OccurrenceCountAsync());
    }

    [Theory]
    [InlineData(null)]                     // "keep the zone"
    [InlineData("europe/berlin")]          // the stored spelling, sent back as the API returned it
    [InlineData("W. Europe Standard Time")] // the same zone by another name
    public async Task AZoneStoredInAnotherSpelling_IsNotAMove(string? sent)
    {
        var created = await _events.CreateAsync(_userId, Daily("Standup", zone: "Europe/Berlin"));
        await _events.DeleteAsync(_userId, created.Id, Start.AddDays(1));
        // A row written before ids were canonicalized.
        await _db.Events.Where(e => e.Id == created.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.TimeZoneId, "europe/berlin"));
        _db.ChangeTracker.Clear();

        await _events.UpdateAsync(_userId, created.Id, Daily("Standup (renamed)", zone: sent));

        Assert.Equal(4, await OccurrenceCountAsync());
    }

    [Fact]
    public async Task MovingTheSeriesToAnotherZone_DropsExclusions()
    {
        var created = await _events.CreateAsync(_userId, Daily("Standup", zone: "America/New_York"));
        await _events.DeleteAsync(_userId, created.Id, Start.AddDays(1));

        // Another zone changes its clocks on other days, so the series produces other instants
        // and the stored exclusions stop naming real occurrences.
        await _events.UpdateAsync(_userId, created.Id, Daily("Standup", zone: "Europe/Vienna"));

        var master = await _db.Events.AsNoTracking().SingleAsync(e => e.Id == created.Id);
        Assert.Equal("Europe/Vienna", master.TimeZoneId);
        Assert.Null(master.ExDates);
    }

    [Fact]
    public async Task ExclusionsSurviveRepeatedDetailEdits()
    {
        var created = await _events.CreateAsync(_userId, Daily("Standup"));
        await _events.DeleteAsync(_userId, created.Id, Start.AddDays(1));
        await _events.DeleteAsync(_userId, created.Id, Start.AddDays(3));
        Assert.Equal(3, await OccurrenceCountAsync());

        for (var i = 0; i < 3; i++)
        {
            await _events.UpdateAsync(_userId, created.Id, Daily($"Standup {i}"));
        }

        Assert.Equal(3, await OccurrenceCountAsync());
    }
}
