using CalendarIT.Application;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Notifications;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarIT.Tests;

/// <summary>
/// Snoozing a browser reminder from its notification: the token it carries, and the reminder
/// coming back — pushed by the job, or returned to a polling browser.
/// </summary>
public sealed class ReminderSnoozeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly FakeMailOutbox _mail = new();
    private readonly FakeWebPushSender _push = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _calendarId;

    public ReminderSnoozeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        foreach (var id in new[] { _userId, _otherUserId })
        {
            _db.Users.Add(new ApplicationUser { Id = id, UserName = $"{id:N}@test", NormalizedUserName = $"{id:N}@TEST" });
        }
        _db.PushSubscriptions.Add(new PushSubscription
        {
            Id = Guid.NewGuid(), UserId = _userId, Endpoint = "https://push.example/a", P256dh = "k", Auth = "a",
        });
        _db.SaveChanges();
        _calendarId = DefaultCalendar.GetOrCreateAsync(_db, _clock, _userId).GetAwaiter().GetResult().Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private ReminderSnoozes Snoozes() => new(_db, _protection, _clock);

    private ReminderDispatchJob Job() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _clock, NullLogger<ReminderDispatchJob>.Instance);

    private static readonly DateTime Start = new(2026, 9, 1, 8, 10, 0, DateTimeKind.Utc);

    /// <summary>An event at 08:10Z with a 10-minute browser reminder: due at 08:00Z, i.e. now.</summary>
    private async Task<Reminder> AddReminderAsync()
    {
        var reminder = new Reminder { Id = Guid.NewGuid(), MinutesBefore = 10, Channel = ReminderChannel.WebPush };
        _db.Events.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(), CalendarId = _calendarId, Uid = $"{Guid.NewGuid():N}@calendarit", Title = "Standup",
            StartUtc = Start, EndUtc = Start.AddMinutes(15), CreatedAt = Start, Reminders = [reminder],
        });
        await _db.SaveChangesAsync();
        return reminder;
    }

    [Fact]
    public async Task TheFirstReminder_CarriesASnoozeToken()
    {
        await AddReminderAsync();

        await Job().RunAsync(_db, _mail, _push, Snoozes());

        Assert.False(string.IsNullOrEmpty(Assert.Single(_push.Sent).Message.SnoozeToken));
    }

    [Fact]
    public async Task ASnoozedReminder_IsPushedAgain_Once_WhenItComesDue()
    {
        var reminder = await AddReminderAsync();
        var token = Snoozes().CreateToken(_userId, reminder.Id, Start);

        Assert.True(await Snoozes().SnoozeAsync(token, 10));

        _clock.Now = _clock.Now.AddMinutes(5);
        await Job().RunAsync(_db, _mail, _push, Snoozes());
        Assert.DoesNotContain(_push.Sent, p => p.Message.Title.StartsWith("Snoozed"));

        _clock.Now = _clock.Now.AddMinutes(5);
        await Job().RunAsync(_db, _mail, _push, Snoozes());
        await Job().RunAsync(_db, _mail, _push, Snoozes());

        var again = Assert.Single(_push.Sent, p => p.Message.Title.StartsWith("Snoozed"));
        Assert.Equal("Snoozed reminder: Standup", again.Message.Title);
        Assert.NotNull(again.Message.SnoozeToken); // and can be snoozed once more
    }

    [Fact]
    public async Task SnoozingAgain_MovesTheTime()
    {
        var reminder = await AddReminderAsync();
        var token = Snoozes().CreateToken(_userId, reminder.Id, Start);

        await Snoozes().SnoozeAsync(token, 5);
        await Snoozes().SnoozeAsync(token, 30);

        var snooze = Assert.Single(_db.ReminderSnoozes.AsNoTracking());
        Assert.Equal(_clock.Now.UtcDateTime.AddMinutes(30), snooze.DueAtUtc);
    }

    [Fact]
    public async Task APollingBrowser_GetsTheSnoozedReminderBack_ToldApart()
    {
        var reminder = await AddReminderAsync();
        await Snoozes().SnoozeAsync(Snoozes().CreateToken(_userId, reminder.Id, Start), 10);
        _clock.Now = _clock.Now.AddMinutes(10);

        var items = await new DueReminderQuery(_db, _clock, Snoozes()).GetDueAsync(_userId, _clock.Now.UtcDateTime.AddMinutes(-1));

        var item = Assert.Single(items);
        Assert.Equal(reminder.Id, item.ReminderId);
        Assert.Equal(_clock.Now, item.SnoozedUntil);
        Assert.NotNull(item.SnoozeToken);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public async Task ABadToken_SnoozesNothing(string token)
    {
        await AddReminderAsync();

        Assert.False(await Snoozes().SnoozeAsync(token, 10));
        Assert.Empty(_db.ReminderSnoozes);
    }

    [Fact]
    public async Task AnExpiredToken_SnoozesNothing()
    {
        var reminder = await AddReminderAsync();
        var token = Snoozes().CreateToken(_userId, reminder.Id, Start);

        _clock.Now = _clock.Now.AddDays(3);

        Assert.False(await Snoozes().SnoozeAsync(token, 10));
    }

    [Fact]
    public async Task AToken_OnlyWorksForItsOwnersReminder()
    {
        var reminder = await AddReminderAsync();

        // A token naming someone else as the owner — what a forged one would have to claim.
        Assert.False(await Snoozes().SnoozeAsync(Snoozes().CreateToken(_otherUserId, reminder.Id, Start), 10));
        Assert.Empty(_db.ReminderSnoozes);
    }

    [Fact]
    public async Task AnOddDuration_IsRejected()
    {
        var reminder = await AddReminderAsync();

        await Assert.ThrowsAsync<InvalidInputException>(() =>
            Snoozes().SnoozeAsync(Snoozes().CreateToken(_userId, reminder.Id, Start), 7));
    }

    [Fact]
    public async Task DeletingTheEvent_DeletesItsSnoozes()
    {
        var reminder = await AddReminderAsync();
        await Snoozes().SnoozeAsync(Snoozes().CreateToken(_userId, reminder.Id, Start), 10);

        _db.Events.Remove(await _db.Events.SingleAsync());
        await _db.SaveChangesAsync();

        Assert.Empty(_db.ReminderSnoozes.AsNoTracking());
    }
}
