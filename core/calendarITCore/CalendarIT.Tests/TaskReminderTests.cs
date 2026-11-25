using CalendarIT.Application;
using CalendarIT.Application.Calendars;
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

/// <summary>Reminders on tasks: when they fire, once, and only while the task is open.</summary>
public sealed class TaskReminderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeMailOutbox _mail = new();
    private readonly FakeWebPushSender _push = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _calendarId;

    public TaskReminderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser
        {
            Id = _userId, UserName = "task@test", NormalizedUserName = "TASK@TEST", Email = "task@test", NormalizedEmail = "TASK@TEST",
        });
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

    private ReminderDispatchJob Job() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _clock, NullLogger<ReminderDispatchJob>.Instance);

    private TaskService Tasks() => new(_db, _clock);

    private static ReminderInput R(int minutes, string channel = "Email") => new() { MinutesBefore = minutes, Channel = channel };

    [Fact]
    public async Task ATimedTask_RemindsBeforeItIsDue_Once()
    {
        await Tasks().CreateAsync(_userId, new SaveTaskRequest
        {
            Title = "Send the report", Due = new DateTimeOffset(2026, 9, 1, 8, 15, 0, TimeSpan.Zero), TimeZone = "Europe/Vienna",
            Reminders = [R(15)],
        });

        await Job().RunAsync(_db, _mail, _push);
        await Job().RunAsync(_db, _mail, _push);

        var mail = Assert.Single(_mail.Sent).Message;
        Assert.Equal("Task due: Send the report", mail.Subject);
        Assert.Contains("is due at Tuesday, 1 September 2026, 10:15 (Europe/Vienna)", mail.TextBody);
    }

    [Fact]
    public async Task ADateOnlyTask_IsDueAtNineInItsCalendarsZone()
    {
        _db.Calendars.Single(c => c.Id == _calendarId).TimeZoneId = "Europe/Vienna";
        await _db.SaveChangesAsync();
        // 09:00 in Vienna on 1 September is 07:00Z; reminded an hour early that is 06:00Z.
        await Tasks().CreateAsync(_userId, new SaveTaskRequest
        {
            Title = "Water the plants", Due = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), DueAllDay = true,
            Reminders = [R(60, "WebPush")],
        });

        _clock.Now = new DateTimeOffset(2026, 9, 1, 5, 59, 0, TimeSpan.Zero);
        await Job().RunAsync(_db, _mail, _push);
        Assert.Empty(_push.Sent);

        _clock.Now = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
        await Job().RunAsync(_db, _mail, _push);
        var pushed = Assert.Single(_push.Sent).Message;
        Assert.Equal("Task: Water the plants", pushed.Title);
        Assert.Contains("is due on Tuesday, 1 September 2026", pushed.Body);
    }

    [Fact]
    public async Task AFinishedTask_DoesntRemind()
    {
        var task = await Tasks().CreateAsync(_userId, new SaveTaskRequest
        {
            Title = "Done already", Due = new DateTimeOffset(2026, 9, 1, 8, 15, 0, TimeSpan.Zero), Reminders = [R(15)],
        });
        await Tasks().SetDoneAsync(_userId, task.Id, true);

        await Job().RunAsync(_db, _mail, _push);

        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task MovingTheDueDate_RemindsAgain_ForTheNewOne()
    {
        var task = await Tasks().CreateAsync(_userId, new SaveTaskRequest
        {
            Title = "Call back", Due = new DateTimeOffset(2026, 9, 1, 8, 15, 0, TimeSpan.Zero), Reminders = [R(15)],
        });
        await Job().RunAsync(_db, _mail, _push);

        await Tasks().UpdateAsync(_userId, task.Id, new SaveTaskRequest
        {
            Title = "Call back", Due = new DateTimeOffset(2026, 9, 1, 9, 15, 0, TimeSpan.Zero),
        });
        _clock.Now = _clock.Now.AddHours(1);
        await Job().RunAsync(_db, _mail, _push);

        Assert.Equal(2, _mail.Sent.Count);
    }

    [Fact]
    public async Task Reminders_AreSaved_KeptWhenNotSent_AndReplaced()
    {
        var task = await Tasks().CreateAsync(_userId, new SaveTaskRequest
        {
            Title = "x", Due = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero), Reminders = [R(60, "WebPush"), R(15)],
        });
        Assert.Equal([new ReminderDto(15, "Email"), new ReminderDto(60, "WebPush")], task.Reminders);

        var kept = await Tasks().UpdateAsync(_userId, task.Id, new SaveTaskRequest { Title = "y", Due = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero) });
        Assert.Equal(2, kept!.Reminders!.Count);

        var replaced = await Tasks().UpdateAsync(_userId, task.Id, new SaveTaskRequest { Title = "y", Reminders = [R(5)] });
        Assert.Equal([new ReminderDto(5, "Email")], replaced!.Reminders);
        Assert.Single(_db.TaskReminders.AsNoTracking());

        await Assert.ThrowsAsync<InvalidInputException>(() =>
            Tasks().UpdateAsync(_userId, task.Id, new SaveTaskRequest { Title = "y", Reminders = [R(5, "Pigeon")] }));
    }

    [Fact]
    public async Task APollingBrowser_GetsTaskReminders_Too()
    {
        await Tasks().CreateAsync(_userId, new SaveTaskRequest
        {
            Title = "Pick up parcel", Due = new DateTimeOffset(2026, 9, 1, 8, 10, 0, TimeSpan.Zero), Reminders = [R(10, "WebPush")],
        });

        var items = await new DueReminderQuery(_db, _clock).GetDueAsync(_userId, _clock.Now.UtcDateTime.AddMinutes(-1));

        Assert.Equal("Task: Pick up parcel", Assert.Single(items).Title);
    }
}
