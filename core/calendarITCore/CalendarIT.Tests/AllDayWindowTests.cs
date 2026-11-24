using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// An all-day event is a date, the same one wherever it is looked at, while the window a client
/// asks for is its own local day. Matched as UTC instants the two only line up east of UTC: in
/// New York the day begins at 04:00Z, after the event's stored midnight, and the event was gone
/// from the day it is on.
/// </summary>
public sealed class AllDayWindowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EventService _events;
    private readonly Guid _userId = Guid.NewGuid();

    public AllDayWindowTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "allday@test", NormalizedUserName = "ALLDAY@TEST" });
        _db.SaveChanges();
        _events = new EventService(_db, TimeProvider.System, new FakeInvitationMailer(),
            new InternalInvitationDelivery(_db, TimeProvider.System));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static DateTimeOffset Day(int day) => new(2026, 10, day, 0, 0, 0, TimeSpan.Zero);

    private Task<EventDto> AddAsync(string title, int firstDay, int lastDay, string? rrule = null) =>
        _events.CreateAsync(_userId, new SaveEventRequest
        {
            Title = title,
            Start = Day(firstDay),
            End = Day(lastDay), // inclusive, as all-day ends are stored
            AllDay = true,
            Recurrence = rrule,
            TimeZone = "America/New_York",
        });

    /// <summary>What a client at <paramref name="offsetHours"/> gets for its October 12th.</summary>
    private async Task<List<string>> TitlesOnThe12thAsync(int offsetHours)
    {
        var from = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.FromHours(offsetHours));
        return (await _events.GetEventsAsync(_userId, from, from.AddDays(1))).Select(e => e.Title).Order().ToList();
    }

    [Theory]
    [InlineData(-10)] // Honolulu
    [InlineData(-4)]  // New York
    [InlineData(0)]
    [InlineData(2)]   // Vienna
    [InlineData(13)]  // Auckland
    public async Task ADay_ShowsItsOwnAllDayEvents_WhereverItIsViewedFrom(int offsetHours)
    {
        await AddAsync("11th", 11, 11);
        await AddAsync("12th", 12, 12);
        await AddAsync("13th", 13, 13);
        await AddAsync("10th to 12th", 10, 12);

        Assert.Equal(["10th to 12th", "12th"], await TitlesOnThe12thAsync(offsetHours));
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(-4)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(13)]
    public async Task ADay_ShowsThatDaysOccurrenceOfAnAllDaySeries(int offsetHours)
    {
        await AddAsync("Daily", 5, 5, "FREQ=DAILY");

        var from = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.FromHours(offsetHours));
        var hit = Assert.Single(await _events.GetEventsAsync(_userId, from, from.AddDays(1)));

        Assert.Equal(Day(12), hit.Start);
    }

    [Fact]
    public async Task TimedEvents_AreStillMatchedByInstant()
    {
        // 23:00 on the 11th in New York is 03:00Z on the 12th: New York's 11th, not its 12th.
        await _events.CreateAsync(_userId, new SaveEventRequest
        {
            Title = "Late call",
            Start = new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 10, 12, 3, 30, 0, TimeSpan.Zero),
            TimeZone = "America/New_York",
        });

        Assert.Empty(await TitlesOnThe12thAsync(-4));
        Assert.Equal(["Late call"], await TitlesOnThe12thAsync(0));
    }
}
