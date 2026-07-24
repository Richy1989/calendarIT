using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Notifications;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarIT.Tests;

/// <summary>
/// What the reminder job actually delivers. The job reads a bounded candidate set rather than
/// every reminder in the database, so these pin the edges of that window — a reminder just
/// inside it fires, one outside doesn't, and nothing fires twice.
/// </summary>
public sealed class ReminderDispatchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeUserMailSender _mail = new();
    private readonly Guid _userId = Guid.NewGuid();
    private Guid _calendarId;

    public ReminderDispatchTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser
        {
            Id = _userId,
            UserName = "owner@test",
            NormalizedUserName = "OWNER@TEST",
            Email = "owner@test",
            NormalizedEmail = "OWNER@TEST",
        });
        _db.SaveChanges();
        _calendarId = DefaultCalendar.GetOrCreateAsync(_db, _clock, _userId).GetAwaiter().GetResult().Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>A clock stopped at a known instant, so "now" in the assertions is exact.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private ReminderDispatchJob Job() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _clock, NullLogger<ReminderDispatchJob>.Instance);

    private async Task<CalendarEvent> AddEventAsync(DateTime startUtc, int minutesBefore, string? rrule = null)
    {
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = _calendarId,
            Uid = $"{Guid.NewGuid():N}@calendarit",
            Title = "Dentist",
            StartUtc = startUtc,
            EndUtc = startUtc.AddMinutes(30),
            RRule = rrule,
            TimeZoneId = "UTC",
            CreatedAt = startUtc,
            Reminders = [new Reminder { Id = Guid.NewGuid(), MinutesBefore = minutesBefore, Channel = ReminderChannel.Email }],
        };
        _db.Events.Add(ev);
        await _db.SaveChangesAsync();
        return ev;
    }

    [Fact]
    public async Task SendsAReminderWhoseTriggerFallsInTheWindow()
    {
        // 09:00 start, 60 min lead → triggers at 08:00, which is now.
        await AddEventAsync(new DateTime(2026, 9, 1, 9, 0, 0), minutesBefore: 60);

        await Job().RunAsync(_db, _mail);

        var (userId, message) = Assert.Single(_mail.Sent);
        Assert.Equal(_userId, userId);
        Assert.Contains("Dentist", message.Subject);
        Assert.Equal("owner@test", message.To.Mailboxes.Single().Address);
    }

    [Fact]
    public async Task DoesNotSendBeforeTheTriggerTime()
    {
        // 10:00 start, 60 min lead → triggers at 09:00, an hour after now.
        await AddEventAsync(new DateTime(2026, 9, 1, 10, 0, 0), minutesBefore: 60);

        await Job().RunAsync(_db, _mail);

        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task DoesNotSendForAnEventWhoseTriggerHasLongPassed()
    {
        // Triggered yesterday; the lookback window is 90 seconds, not a day.
        await AddEventAsync(new DateTime(2026, 8, 31, 9, 0, 0), minutesBefore: 60);

        await Job().RunAsync(_db, _mail);

        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task DoesNotSendTheSameReminderTwice()
    {
        await AddEventAsync(new DateTime(2026, 9, 1, 9, 0, 0), minutesBefore: 60);

        await Job().RunAsync(_db, _mail);
        await Job().RunAsync(_db, _mail); // a second tick inside the same lookback window

        Assert.Single(_mail.Sent);
        Assert.Equal(1, await _db.NotificationLogs.CountAsync());
    }

    [Fact]
    public async Task SendsForARecurringOccurrence_NotJustTheSeriesStart()
    {
        // Series started weeks ago; today's occurrence is the one coming due.
        await AddEventAsync(new DateTime(2026, 8, 1, 9, 0, 0), minutesBefore: 60, rrule: "FREQ=DAILY");

        await Job().RunAsync(_db, _mail);

        Assert.Single(_mail.Sent);
    }

    [Fact]
    public async Task ARemindersLongLeadTimeStillFires()
    {
        // A week's notice: the candidate window has to reach out as far as the longest offset,
        // which is what the query's horizon is for.
        await AddEventAsync(new DateTime(2026, 9, 8, 8, 0, 0), minutesBefore: 60 * 24 * 7);

        await Job().RunAsync(_db, _mail);

        Assert.Single(_mail.Sent);
    }

    [Fact]
    public async Task WithoutAMailAccount_NothingIsSentButItIsStillRecorded()
    {
        _mail.HasAccount = false;
        await AddEventAsync(new DateTime(2026, 9, 1, 9, 0, 0), minutesBefore: 60);

        await Job().RunAsync(_db, _mail);

        Assert.Empty(_mail.Sent);
        Assert.Equal(1, await _db.NotificationLogs.CountAsync()); // not retried forever
    }
}
