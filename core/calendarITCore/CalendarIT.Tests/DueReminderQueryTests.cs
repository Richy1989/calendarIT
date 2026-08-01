using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Notifications;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

public sealed class DueReminderQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly Guid _userId = Guid.NewGuid();
    private Guid _calendarId;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public DueReminderQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser
        {
            Id = _userId, UserName = "owner@test", NormalizedUserName = "OWNER@TEST",
            Email = "owner@test", NormalizedEmail = "OWNER@TEST",
        });
        _db.SaveChanges();
        _calendarId = DefaultCalendar.GetOrCreateAsync(_db, _clock, _userId).GetAwaiter().GetResult().Id;
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private async Task AddReminderAsync(DateTime startUtc, int minutesBefore,
        ReminderChannel channel = ReminderChannel.WebPush, Guid? calendarId = null, string? rrule = null)
    {
        _db.Events.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(), CalendarId = calendarId ?? _calendarId,
            Uid = $"{Guid.NewGuid():N}@calendarit", Title = "Dentist",
            StartUtc = startUtc, EndUtc = startUtc.AddMinutes(30), RRule = rrule, TimeZoneId = "UTC",
            CreatedAt = startUtc,
            Reminders = [new Reminder { Id = Guid.NewGuid(), MinutesBefore = minutesBefore, Channel = channel }],
        });
        await _db.SaveChangesAsync();
    }

    private DueReminderQuery Query() => new(_db, _clock);

    [Fact]
    public async Task ReturnsAWebPushReminderTriggeringInsideTheWindow()
    {
        // now = 08:00; start 08:30, lead 30 → trigger 08:00, inside (07:55, 08:00].
        await AddReminderAsync(new DateTime(2026, 9, 1, 8, 30, 0), 30);
        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 9, 1, 7, 55, 0, DateTimeKind.Utc), default);
        var item = Assert.Single(items);
        Assert.Equal("Dentist", item.Title);
    }

    [Fact]
    public async Task ExcludesEmailChannelReminders()
    {
        await AddReminderAsync(new DateTime(2026, 9, 1, 8, 30, 0), 30, channel: ReminderChannel.Email);
        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 9, 1, 7, 55, 0, DateTimeKind.Utc), default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ExcludesRemindersTriggeringInTheFuture()
    {
        // start 09:30, lead 30 → trigger 09:00, after now.
        await AddReminderAsync(new DateTime(2026, 9, 1, 9, 30, 0), 30);
        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 9, 1, 7, 55, 0, DateTimeKind.Utc), default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ClampsSinceToOneHourAgo()
    {
        // A trigger 90 min ago must NOT come back even if the client asks since yesterday.
        await AddReminderAsync(new DateTime(2026, 9, 1, 7, 0, 0), 30); // trigger 06:30, 90 min before now
        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 8, 31, 8, 0, 0, DateTimeKind.Utc), default);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ScopesToTheRequestingUser()
    {
        var otherUser = Guid.NewGuid();
        _db.Users.Add(new ApplicationUser
        {
            Id = otherUser, UserName = "other@test", NormalizedUserName = "OTHER@TEST",
            Email = "other@test", NormalizedEmail = "OTHER@TEST",
        });
        await _db.SaveChangesAsync();
        var otherCalendar = await DefaultCalendar.GetOrCreateAsync(_db, _clock, otherUser);
        await AddReminderAsync(new DateTime(2026, 9, 1, 8, 30, 0), 30, calendarId: otherCalendar.Id);

        var items = await Query().GetDueAsync(_userId, new DateTime(2026, 9, 1, 7, 55, 0, DateTimeKind.Utc), default);
        Assert.Empty(items);
    }
}
