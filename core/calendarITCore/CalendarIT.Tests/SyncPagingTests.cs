using System.Text;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using CalendarIT.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CalendarIT.Tests;

/// <summary>
/// Pull pages stay within the requested limit even when thousands of rows share one
/// <c>UpdatedAt</c> (a bulk import), without skipping or repeating a row — on SQLite and, when
/// TEST_POSTGRES_CONNECTION is set, PostgreSQL (whose Guid ordering differs).
/// </summary>
public sealed class SyncPagingTests
{
    public static TheoryData<string> Providers()
    {
        var data = new TheoryData<string> { "sqlite" };
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")))
        {
            data.Add("postgres");
        }
        return data;
    }

    private sealed class Store : IAsyncDisposable
    {
        private readonly SqliteConnection? _sqlite;
        public AppDbContext Db { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public SyncService Sync { get; }

        public Store(string provider)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>();
            if (provider == "postgres")
            {
                var db = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION"))
                {
                    Database = $"calendarit_paging_{Guid.NewGuid():N}",
                };
                options.UseNpgsql(db.ConnectionString);
            }
            else
            {
                _sqlite = new SqliteConnection("DataSource=:memory:");
                _sqlite.Open();
                options.UseSqlite(_sqlite);
            }
            Db = new AppDbContext(options.Options);
            Db.Database.EnsureCreated();
            Db.Users.Add(new ApplicationUser { Id = UserId, UserName = "paging@test", NormalizedUserName = "PAGING@TEST" });
            Db.SaveChanges();
            var clock = TimeProvider.System;
            Sync = new SyncService(Db, clock, new EventService(Db, clock, new FakeInvitationMailer(), new InternalInvitationDelivery(Db, clock)),
                new TaskService(Db, clock), new CalendarService(Db, clock), new CategoryService(Db, clock), NullLogger<SyncService>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            if (_sqlite is null)
            {
                await Db.Database.EnsureDeletedAsync();
            }
            await Db.DisposeAsync();
            _sqlite?.Dispose();
        }
    }

    private static async Task<(List<CalendarEvent> Events, List<TaskItem> Tasks, DateTime Instant)> SeedAsync(Store s, int events, int tasks)
    {
        var calendar = await DefaultCalendar.GetOrCreateAsync(s.Db, TimeProvider.System, s.UserId);
        // µs precision, as Postgres keeps it; everything written at one instant, as an import does.
        var at = new DateTime(DateTime.UtcNow.AddHours(-1).Ticks / 10 * 10, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, events).Select(i => new CalendarEvent
        {
            Id = Guid.NewGuid(), CalendarId = calendar.Id, Uid = $"u{i}", Title = $"E{i}",
            StartUtc = at, CreatedAt = at, UpdatedAt = at,
        }).ToList();
        var todo = Enumerable.Range(0, tasks).Select(i => new TaskItem
        {
            Id = Guid.NewGuid(), CalendarId = calendar.Id, Uid = $"t{i}", Title = $"T{i}", CreatedAt = at, UpdatedAt = at,
        }).ToList();
        s.Db.Events.AddRange(rows);
        s.Db.Tasks.AddRange(todo);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        return (rows, todo, at);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ARunAtOneInstant_IsSplitIntoBoundedPages_EveryRowOnce(string provider)
    {
        await using var s = new Store(provider);
        var (events, tasks, _) = await SeedAsync(s, events: 230, tasks: 75);
        const int limit = SyncService.MinLimit; // 50

        string? cursor = null;
        var seenEvents = new List<Guid>();
        var seenTasks = new List<Guid>();
        for (var pages = 0; ; pages++)
        {
            Assert.True(pages < 50, "paging didn't end");
            var page = await s.Sync.PullAsync(s.UserId, cursor, limit);
            Assert.True(page.Events.Count + page.Tasks.Count <= limit, $"page of {page.Events.Count + page.Tasks.Count} > {limit}");
            seenEvents.AddRange(page.Events.Select(e => e.Id));
            seenTasks.AddRange(page.Tasks.Select(t => t.Id));
            cursor = page.Cursor;
            if (!page.HasMore)
            {
                break;
            }
        }

        Assert.Equal(seenEvents.Count, seenEvents.Distinct().Count());
        Assert.Equal(events.Select(e => e.Id).Order(), seenEvents.Order());
        Assert.Equal(seenTasks.Count, seenTasks.Distinct().Count());
        Assert.Equal(tasks.Select(t => t.Id).Order(), seenTasks.Order());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AnOlderServersCursor_InsideAListing_StillContinuesAfterItsPosition(string provider)
    {
        await using var s = new Store(provider);
        var (events, _, at) = await SeedAsync(s, events: 60, tasks: 0);
        var later = await s.Db.Events.Where(e => e.Id == events[0].Id).SingleAsync();
        later.UpdatedAt = at.AddSeconds(1);
        await s.Db.SaveChangesAsync();

        // The old format (five fields, position without an id): everything at `at` was sent.
        var raw = $"1:-:{DateTime.UtcNow.Ticks}:e:{at.Ticks}";
        var old = Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var page = await s.Sync.PullAsync(s.UserId, old, 500);

        Assert.False(page.Reset);
        Assert.Equal([events[0].Id], page.Events.Select(e => e.Id));
    }
}
