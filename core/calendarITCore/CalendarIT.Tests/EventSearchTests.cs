using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// Search ranking. The database now narrows to the nearest hits either side of now instead of
/// handing every match back for in-memory sorting, so these pin what that narrowing must still
/// return: upcoming first, past ones when there's room, and series ranked by their next
/// occurrence rather than the date the series began.
/// </summary>
public sealed class EventSearchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EventService _events;
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _calendarId;
    private readonly Guid _otherCalendarId;

    private static DateTime At(int day, int hour) => new(2026, 9, day, hour, 0, 0);

    public EventSearchTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.AddRange(
            new ApplicationUser { Id = _userId, UserName = "a@test", NormalizedUserName = "A@TEST" },
            new ApplicationUser { Id = _otherUserId, UserName = "b@test", NormalizedUserName = "B@TEST" });
        _db.SaveChanges();
        _calendarId = DefaultCalendar.GetOrCreateAsync(_db, _clock, _userId).GetAwaiter().GetResult().Id;
        _otherCalendarId = DefaultCalendar.GetOrCreateAsync(_db, _clock, _otherUserId).GetAwaiter().GetResult().Id;
        _events = new EventService(_db, _clock, new FakeInvitationMailer(),
            new InternalInvitationDelivery(_db, _clock));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private void Add(string title, DateTime startUtc, string? rrule = null, string? location = null, Guid? calendarId = null)
    {
        _db.Events.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId ?? _calendarId,
            Uid = $"{Guid.NewGuid():N}@calendarit",
            Title = title,
            Location = location,
            StartUtc = startUtc,
            EndUtc = startUtc.AddHours(1),
            RRule = rrule,
            TimeZoneId = "UTC",
            CreatedAt = startUtc,
        });
        _db.SaveChanges();
    }

    private async Task<IReadOnlyList<EventSearchResult>> Search(string q, int limit = 8) =>
        await _events.SearchAsync(_userId, q, limit);

    [Fact]
    public async Task RanksTheSoonestUpcomingHitFirst()
    {
        Add("Standup review", At(20, 9));
        Add("Standup review", At(3, 9));   // soonest ahead of now
        Add("Standup review", At(10, 9));

        var hits = await Search("standup");

        Assert.Equal(3, hits.Count);
        Assert.Equal(At(3, 9), hits[0].Start.UtcDateTime);
        Assert.Equal(At(10, 9), hits[1].Start.UtcDateTime);
    }

    [Fact]
    public async Task PutsPastHitsAfterUpcomingOnes()
    {
        Add("Retro", At(20, 9));  // future, further away
        Add("Retro", At(1, 9));   // past (now is 12:00 on the 1st), but only hours ago

        var hits = await Search("retro");

        Assert.Equal(2, hits.Count);
        Assert.Equal(At(20, 9), hits[0].Start.UtcDateTime); // upcoming wins despite being further
        Assert.Equal(At(1, 9), hits[1].Start.UtcDateTime);
    }

    [Fact]
    public async Task FindsPastHitsWhenNothingIsUpcoming()
    {
        Add("Archived planning", At(1, 8));

        var hit = Assert.Single(await Search("planning"));

        Assert.Equal(At(1, 8), hit.Start.UtcDateTime);
    }

    [Fact]
    public async Task RanksASeriesByItsNextOccurrence_NotItsStart()
    {
        // The series began in August; its next occurrence is tomorrow, so it should outrank a
        // one-off later in the month even though its stored start is in the past.
        Add("Gym session", new DateTime(2026, 8, 1, 7, 0, 0), rrule: "FREQ=DAILY");
        Add("Gym session", At(20, 9));

        var hits = await Search("gym");

        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].Recurring);
        Assert.Equal(new DateTime(2026, 9, 2, 7, 0, 0), hits[0].Start.UtcDateTime);
    }

    [Fact]
    public async Task RespectsTheLimit()
    {
        for (var day = 2; day <= 20; day++)
        {
            Add($"Sync {day}", At(day, 9));
        }

        var hits = await Search("sync", limit: 5);

        Assert.Equal(5, hits.Count);
        Assert.Equal(At(2, 9), hits[0].Start.UtcDateTime); // still the soonest, not an arbitrary five
    }

    [Fact]
    public async Task MatchesLocationAndIgnoresCase()
    {
        Add("Lunch", At(3, 12), location: "Kantine Nord");

        Assert.Single(await Search("KANTINE"));
        Assert.Single(await Search("lunch"));
    }

    [Fact]
    public async Task NeverReturnsAnotherUsersEvents()
    {
        Add("Secret offsite", At(3, 9), calendarId: _otherCalendarId);

        Assert.Empty(await Search("offsite"));
    }

    [Fact]
    public async Task BlankQueryReturnsNothing()
    {
        Add("Anything", At(3, 9));

        Assert.Empty(await Search("   "));
    }
}
