using System.Text.Json;
using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Application.Sync;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using CalendarIT.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarIT.Tests;

/// <summary>The app's delta sync: what a pull hands out, and how pushed changes are taken.</summary>
public sealed class SyncServiceTests : IDisposable
{
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly MutableClock _clock = new(DateTimeOffset.UtcNow);
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly EventService _events;
    private readonly TaskService _tasks;
    private readonly CalendarService _calendars;
    private readonly SyncService _sync;

    private static readonly DateTimeOffset Monday = new(2026, 8, 3, 7, 0, 0, TimeSpan.Zero);

    public SyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "sync@test", NormalizedUserName = "SYNC@TEST" });
        _db.Users.Add(new ApplicationUser { Id = _otherUserId, UserName = "other@test", NormalizedUserName = "OTHER@TEST" });
        _db.SaveChanges();
        _events = new EventService(_db, _clock, new FakeInvitationMailer(), new InternalInvitationDelivery(_db, _clock));
        _tasks = new TaskService(_db, _clock);
        _calendars = new CalendarService(_db, _clock);
        _sync = new SyncService(_db, _clock, _events, _tasks, _calendars, new CategoryService(_db, _clock),
            NullLogger<SyncService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SaveEventRequest Event(string title, string? rule = null, DateTimeOffset? start = null, Guid? calendarId = null) => new()
    {
        Title = title,
        Start = start ?? Monday,
        End = (start ?? Monday).AddHours(1),
        Recurrence = rule,
        TimeZone = "Europe/Berlin",
        CalendarId = calendarId,
        Reminders = [new ReminderInput { MinutesBefore = 10, Channel = "WebPush" }],
    };

    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, Json);

    private static SyncOp Op(string op, Guid id, object? data = null, string? baseVersion = null, DateTimeOffset? occurrence = null) => new()
    {
        OpId = Guid.NewGuid().ToString("N"),
        Op = op,
        Id = id,
        Data = data is null ? null : Data(data),
        BaseVersion = baseVersion,
        Occurrence = occurrence,
    };

    private async Task<SyncOpResult> PushOne(SyncOp op)
    {
        var response = await _sync.PushAsync(_userId, new SyncPushRequest { Ops = [op] });
        return Assert.Single(response.Results);
    }

    /// <summary>Every page of one listing, from <paramref name="cursor"/> on.</summary>
    private async Task<(List<SyncPullResponse> Pages, string Cursor)> PullAll(string? cursor, int limit = 500)
    {
        var pages = new List<SyncPullResponse>();
        while (true)
        {
            var page = await _sync.PullAsync(_userId, cursor, limit);
            pages.Add(page);
            cursor = page.Cursor;
            if (!page.HasMore)
            {
                return (pages, cursor);
            }
        }
    }

    private async Task<string> VersionOf(Guid eventId)
    {
        var (pages, _) = await PullAll(null);
        return pages.SelectMany(p => p.Events).Single(e => e.Id == eventId).Version;
    }

    [Fact]
    public async Task AFirstPull_HandsOutEverything_AsStored()
    {
        var series = await _events.CreateAsync(_userId, Event("Standup", "FREQ=WEEKLY"));
        await _events.UpsertOccurrenceAsync(_userId, series.Id, Monday.AddDays(7), Event("Standup (moved)", start: Monday.AddDays(7).AddHours(2)));
        await _events.DeleteAsync(_userId, series.Id, Monday.AddDays(14));
        var task = await _tasks.CreateAsync(_userId, new SaveTaskRequest { Title = "Buy milk", Priority = "high" });

        var page = await _sync.PullAsync(_userId, cursor: null, limit: 500);

        Assert.Equal(SyncService.Protocol, page.Protocol);
        Assert.False(page.Reset);
        Assert.False(page.HasMore);
        Assert.Single(page.Calendars);
        var dto = Assert.Single(page.Events); // the series once, not its occurrences
        Assert.Equal("FREQ=WEEKLY", dto.Recurrence);
        Assert.Equal("Europe/Berlin", dto.TimeZone);
        Assert.Equal([Monday.AddDays(7), Monday.AddDays(14)], dto.ExDates); // overridden + deleted
        var edited = Assert.Single(dto.Overrides);
        Assert.Equal(Monday.AddDays(7), edited.RecurrenceId);
        Assert.Equal("Standup (moved)", edited.Title);
        Assert.Equal(10, Assert.Single(dto.Reminders).MinutesBefore);
        var t = Assert.Single(page.Tasks);
        Assert.Equal(task.Id, t.Id);
        Assert.Equal("high", t.Priority);
        Assert.Empty(page.Deleted.Events);
    }

    [Fact]
    public async Task ANextPull_HandsOutOnlyWhatChanged_AndWhatWentAway()
    {
        _clock.Now = DateTimeOffset.UtcNow.AddHours(-1);
        var kept = await _events.CreateAsync(_userId, Event("Kept"));
        var edited = await _events.CreateAsync(_userId, Event("Edited"));
        var deleted = await _events.CreateAsync(_userId, Event("Deleted"));
        var task = await _tasks.CreateAsync(_userId, new SaveTaskRequest { Title = "Done soon" });
        var second = await _calendars.CreateAsync(_userId, new SaveCalendarRequest { Name = "Work" });
        _clock.Now = DateTimeOffset.UtcNow;
        var (_, cursor) = await PullAll(null);

        await _events.UpdateAsync(_userId, edited.Id, Event("Edited!"));
        await _events.UpdateAsync(_userId, kept.Id, Event("Kept", calendarId: second.Id)); // moved: a change
        await _events.DeleteAsync(_userId, deleted.Id, occurrence: null);
        await _tasks.DeleteAsync(_userId, task.Id);

        var page = await _sync.PullAsync(_userId, cursor, 500);

        Assert.Equal(["Edited!", "Kept"], page.Events.Select(e => e.Title).Order());
        Assert.Equal(second.Id, page.Events.Single(e => e.Title == "Kept").CalendarId);
        Assert.Equal([deleted.Id], page.Deleted.Events);
        Assert.Equal([task.Id], page.Deleted.Tasks);
        Assert.Equal(2, page.Calendars.Count); // calendars always come whole
    }

    [Fact]
    public async Task Paging_HandsOutEveryRowOnce_EvenAcrossARunWrittenAtOneInstant()
    {
        var calendar = (await _calendars.ListAsync(_userId)).Single().Id;
        var at = DateTime.UtcNow.AddHours(-2);
        var rows = new List<CalendarEvent>();
        for (var i = 0; i < 230; i++)
        {
            // 100 rows at distinct instants, then a run of 130 sharing one (as an import writes them).
            var updated = i < 100 ? at.AddSeconds(i) : at.AddSeconds(500);
            rows.Add(new CalendarEvent
            {
                Id = Guid.NewGuid(), CalendarId = calendar, Uid = $"u{i}", Title = $"E{i}",
                StartUtc = Monday.UtcDateTime, CreatedAt = updated, UpdatedAt = updated,
            });
        }
        _db.Events.AddRange(rows);
        _db.SaveChanges();

        var (pages, _) = await PullAll(null, limit: 60);

        var seen = pages.SelectMany(p => p.Events).Select(e => e.Id).ToList();
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(rows.Select(r => r.Id).Order(), seen.Order());
        Assert.True(pages.Count >= 3);
    }

    [Fact]
    public async Task ACursorThatIsTooOldOrNotOurs_StartsOver()
    {
        await _events.CreateAsync(_userId, Event("Something"));

        var garbage = await _sync.PullAsync(_userId, "not-a-cursor", 500);
        Assert.True(garbage.Reset);
        Assert.Single(garbage.Events);

        var (_, cursor) = await PullAll(null);
        _clock.Now = _clock.Now.AddDays(61);
        var old = await _sync.PullAsync(_userId, cursor, 500);
        Assert.True(old.Reset);
        Assert.Single(old.Events);
    }

    [Fact]
    public async Task ACreate_TakesTheClientsId_AndSentAgain_IsAConflict()
    {
        var id = Guid.NewGuid();
        var op = Op(SyncOps.EventCreate, id, Event("From the phone"));

        Assert.Equal(SyncOpStatus.Ok, (await PushOne(op)).Status);
        Assert.Equal("From the phone", (await _events.GetByIdAsync(_userId, id))!.Title);
        Assert.Equal(SyncOpStatus.Conflict, (await PushOne(op)).Status);
        Assert.Single(_db.Events.AsNoTracking());
    }

    [Fact]
    public async Task AnIdSomeoneElseHas_IsRefused_WithoutSayingWhose()
    {
        var theirs = await _events.CreateAsync(_otherUserId, Event("Theirs"));

        var result = await PushOne(Op(SyncOps.EventCreate, theirs.Id, Event("Mine")));

        Assert.Equal(SyncOpStatus.Invalid, result.Status);
        Assert.Equal(ClientIds.InUseMessage, result.Error);
        Assert.Equal("Theirs", _db.Events.AsNoTracking().Single().Title);
    }

    [Fact]
    public async Task AnUpdate_OnAnOldVersion_IsAConflict_AndChangesNothing()
    {
        var created = await _events.CreateAsync(_userId, Event("Original"));
        var version = await VersionOf(created.Id);
        _clock.Now = _clock.Now.AddMinutes(1);
        await _events.UpdateAsync(_userId, created.Id, Event("Changed on the web"));

        var stale = await PushOne(Op(SyncOps.EventUpdate, created.Id, Event("Changed on the phone"), baseVersion: version));
        Assert.Equal(SyncOpStatus.Conflict, stale.Status);
        Assert.Equal("Changed on the web", (await _events.GetByIdAsync(_userId, created.Id))!.Title);

        var current = await PushOne(Op(SyncOps.EventUpdate, created.Id, Event("Changed on the phone"), baseVersion: await VersionOf(created.Id)));
        Assert.Equal(SyncOpStatus.Ok, current.Status);
        Assert.Equal("Changed on the phone", (await _events.GetByIdAsync(_userId, created.Id))!.Title);
    }

    [Fact]
    public async Task ChangesQueuedOffline_GoThroughTogether_OnTheBaseTheyWereMadeOn()
    {
        var series = await _events.CreateAsync(_userId, Event("Standup", "FREQ=WEEKLY"));
        var version = await VersionOf(series.Id);
        _clock.Now = _clock.Now.AddMinutes(1);
        var created = Guid.NewGuid();

        var response = await _sync.PushAsync(_userId, new SyncPushRequest
        {
            Ops =
            [
                Op(SyncOps.EventOccurrenceDelete, series.Id, baseVersion: version, occurrence: Monday.AddDays(7)),
                Op(SyncOps.EventOccurrenceDelete, series.Id, baseVersion: version, occurrence: Monday.AddDays(14)),
                Op(SyncOps.EventCreate, created, Event("New")),
                Op(SyncOps.EventUpdate, created, Event("New, renamed")), // created above: no base yet
                Op(SyncOps.EventUpdate, series.Id, Event("Standup", "FREQ=WEEKLY"), baseVersion: "12345"), // a real conflict
            ],
        });

        Assert.Equal(
            [SyncOpStatus.Ok, SyncOpStatus.Ok, SyncOpStatus.Ok, SyncOpStatus.Ok, SyncOpStatus.Conflict],
            response.Results.Select(r => r.Status));
        var (pages, _) = await PullAll(null);
        var now = pages.SelectMany(p => p.Events).Single(e => e.Id == series.Id);
        Assert.Equal(now.Version, response.Results[1].Versions![series.Id]); // the next base for queued changes
        Assert.Equal([Monday.AddDays(7), Monday.AddDays(14)], now.ExDates);
        Assert.Equal("New, renamed", pages.SelectMany(p => p.Events).Single(e => e.Id == created).Title);
    }

    [Fact]
    public async Task AChangeToAnExistingEvent_NeedsItsVersion()
    {
        var created = await _events.CreateAsync(_userId, Event("Original"));

        var result = await PushOne(Op(SyncOps.EventUpdate, created.Id, Event("Changed")));

        Assert.Equal(SyncOpStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task OccurrenceChanges_MoveTheSeriesVersion()
    {
        var series = await _events.CreateAsync(_userId, Event("Standup", "FREQ=WEEKLY"));
        var version = await VersionOf(series.Id);
        _clock.Now = _clock.Now.AddMinutes(1);

        var upsert = await PushOne(Op(SyncOps.EventOccurrenceUpsert, series.Id,
            Event("Standup (later)", start: Monday.AddDays(7).AddHours(1)), version, occurrence: Monday.AddDays(7)));
        Assert.Equal(SyncOpStatus.Ok, upsert.Status);

        // The phone that still holds the old version can't delete an occurrence on top of it.
        var stale = await PushOne(Op(SyncOps.EventOccurrenceDelete, series.Id, baseVersion: version, occurrence: Monday.AddDays(14)));
        Assert.Equal(SyncOpStatus.Conflict, stale.Status);

        var (pages, _) = await PullAll(null);
        var dto = pages.SelectMany(p => p.Events).Single();
        Assert.NotEqual(version, dto.Version);
        Assert.Equal("Standup (later)", Assert.Single(dto.Overrides).Title);
    }

    [Fact]
    public async Task ThisAndFollowing_GivesTheNewSeriesTheClientsId()
    {
        var series = await _events.CreateAsync(_userId, Event("Standup", "FREQ=WEEKLY"));
        var newId = Guid.NewGuid();
        var data = new
        {
            id = newId, title = "Standup v2", start = Monday.AddDays(21), end = Monday.AddDays(21).AddHours(1),
            recurrence = "FREQ=WEEKLY", timeZone = "Europe/Berlin",
        };

        var result = await PushOne(Op(SyncOps.EventFollowingUpdate, series.Id, data, await VersionOf(series.Id), Monday.AddDays(21)));

        Assert.Equal(SyncOpStatus.Ok, result.Status);
        Assert.Equal("Standup v2", (await _events.GetByIdAsync(_userId, newId))!.Title);
    }

    [Fact]
    public async Task ASubscribedCalendar_IsForbidden()
    {
        var feed = new Calendar
        {
            Id = Guid.NewGuid(), OwnerUserId = _userId, Name = "Holidays", SourceUrl = "https://example.com/h.ics",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Calendars.Add(feed);
        _db.SaveChanges();

        var result = await PushOne(Op(SyncOps.EventCreate, Guid.NewGuid(), Event("Mine", calendarId: feed.Id)));

        Assert.Equal(SyncOpStatus.Forbidden, result.Status);
        Assert.True((await _sync.PullAsync(_userId, null, 500)).Calendars.Single(c => c.Id == feed.Id).ReadOnly);
    }

    [Fact]
    public async Task OneBadChange_DoesNotStopTheRest()
    {
        var good = Guid.NewGuid();
        var response = await _sync.PushAsync(_userId, new SyncPushRequest
        {
            Ops =
            [
                Op(SyncOps.EventCreate, Guid.NewGuid(), new { title = "", start = Monday }), // no title
                Op("event.explode", Guid.NewGuid()),
                Op(SyncOps.TaskDelete, Guid.NewGuid(), baseVersion: "1"),
                Op(SyncOps.TaskCreate, good, new { title = "Still made" }),
            ],
        });

        Assert.Equal(
            [SyncOpStatus.Invalid, SyncOpStatus.Invalid, SyncOpStatus.NotFound, SyncOpStatus.Ok],
            response.Results.Select(r => r.Status));
        Assert.Equal("Still made", (await _tasks.GetAsync(_userId, good))!.Title);
    }

    [Fact]
    public async Task Tasks_SyncWithVersions_AndSubtasksGoWithTheirParent()
    {
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        await PushOne(Op(SyncOps.TaskCreate, parent, new { title = "Trip" }));
        await PushOne(Op(SyncOps.TaskCreate, child, new { title = "Pack", parentId = parent }));
        var (pages, cursor) = await PullAll(null);
        var version = pages.SelectMany(p => p.Tasks).Single(t => t.Id == parent).Version;

        Assert.Equal(SyncOpStatus.Ok, (await PushOne(Op(SyncOps.TaskDone, parent, new { done = true }, version))).Status);
        var after = await _sync.PullAsync(_userId, cursor, 500);
        Assert.All(after.Tasks, t => Assert.Equal("Completed", t.Status));

        var newest = after.Tasks.Single(t => t.Id == parent).Version;
        Assert.Equal(SyncOpStatus.Ok, (await PushOne(Op(SyncOps.TaskDelete, parent, baseVersion: newest))).Status);
        var gone = await _sync.PullAsync(_userId, after.Cursor, 500);
        Assert.Equal(new[] { child, parent }.Order(), gone.Deleted.Tasks.Order());
    }

    [Fact]
    public async Task CalendarsAndCategories_ChangeWithoutVersions()
    {
        var calendar = Guid.NewGuid();
        var category = Guid.NewGuid();
        await PushOne(Op(SyncOps.CategoryCreate, category, new { name = "Work", color = "#336699" }));
        await PushOne(Op(SyncOps.CalendarCreate, calendar, new { name = "Job" }));

        var update = await PushOne(Op(SyncOps.CalendarUpdate, calendar, new
        {
            name = "Office", defaultCategoryId = category, defaultReminders = new[] { new { minutesBefore = 30, channel = "WebPush" } },
        }));

        Assert.Equal(SyncOpStatus.Ok, update.Status);
        var dto = (await _sync.PullAsync(_userId, null, 500)).Calendars.Single(c => c.Id == calendar);
        Assert.Equal("Office", dto.Name);
        Assert.Equal(category, dto.DefaultCategoryId);
        Assert.Equal(30, Assert.Single(dto.DefaultReminders).MinutesBefore);
        Assert.Equal(SyncOpStatus.Invalid,
            (await PushOne(Op(SyncOps.CategoryCreate, Guid.NewGuid(), new { name = "work", color = "#000000" }))).Status);
    }

    [Fact]
    public async Task Devices_RegisterUnderTheirOwnId_AndOnlyReachablePushEndpointsAreKept()
    {
        var devices = new DeviceService(_db, _clock);
        var id = Guid.NewGuid();

        await devices.RegisterAsync(_userId, id, new RegisterDeviceRequest
        {
            Name = "Pixel", Platform = "Android",
            Push = new DevicePushInput { Kind = "unifiedpush", Endpoint = "https://ntfy.example.com/up123", P256dh = "k", Auth = "a" },
        });
        var listed = Assert.Single(await devices.ListAsync(_userId));
        Assert.Equal("android", listed.Platform);
        Assert.Equal("unifiedpush", listed.PushKind);

        await Assert.ThrowsAsync<InvalidInputException>(() => devices.RegisterAsync(_userId, Guid.NewGuid(), new RegisterDeviceRequest
        {
            Name = "Evil", Platform = "android",
            Push = new DevicePushInput { Kind = "unifiedpush", Endpoint = "https://127.0.0.1/x", P256dh = "k", Auth = "a" },
        }));
        await Assert.ThrowsAsync<InvalidInputException>(() =>
            devices.RegisterAsync(_otherUserId, id, new RegisterDeviceRequest { Name = "Mine?", Platform = "android" }));
        Assert.True(await devices.RemoveAsync(_userId, id));
        Assert.Empty(await devices.ListAsync(_userId));
    }
}
