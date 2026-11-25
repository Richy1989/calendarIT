using CalendarIT.Application.Notifications;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using CalendarIT.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>App devices are nudged ("sync") when their user's data changes — whoever changed it.</summary>
public sealed class DeviceNotifierTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DeviceNotifier _notifier = new();
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherId = Guid.NewGuid();
    private readonly Guid _calendarId = Guid.NewGuid();

    public DeviceNotifierTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection)
            .AddInterceptors(new SyncChangeInterceptor(_notifier)).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "u@test", NormalizedUserName = "U@TEST" });
        _db.Users.Add(new ApplicationUser { Id = _otherId, UserName = "o@test", NormalizedUserName = "O@TEST" });
        _db.Calendars.Add(new Calendar { Id = _calendarId, OwnerUserId = _userId, Name = "P", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        Drain(); // the setup's own changes
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private List<Guid> Drain()
    {
        var seen = new List<Guid>();
        while (_notifier.Reader.TryRead(out var id))
        {
            seen.Add(id);
        }
        return seen;
    }

    [Fact]
    public async Task ChangingAnEvent_NamesItsCalendarsOwner()
    {
        _db.Events.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(), CalendarId = _calendarId, Uid = "x", Title = "Lunch", StartUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        Assert.Equal([_userId], Drain().Distinct());
    }

    [Fact]
    public void ASaveWithoutCalendarData_NamesNoOne()
    {
        _db.Devices.Add(new Device { Id = Guid.NewGuid(), UserId = _userId, Name = "p", Platform = "android", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow });
        _db.SaveChanges();
        Assert.Empty(Drain());
    }

    [Fact]
    public async Task OnlyUnifiedPushDevices_OfThoseUsers_AreNudged_AndAGoneEndpointIsForgotten()
    {
        Device D(Guid user, string? kind, string? endpoint) => new()
        {
            Id = Guid.NewGuid(), UserId = user, Name = "d", Platform = "android", PushKind = kind, PushEndpoint = endpoint,
            PushP256dh = "k", PushAuth = "a", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        };
        _db.Devices.AddRange(
            D(_userId, "unifiedpush", "https://ntfy.example/up1"),
            D(_userId, "unifiedpush", "https://ntfy.example/gone"),
            D(_userId, null, null),
            D(_otherId, "unifiedpush", "https://ntfy.example/other"));
        _db.SaveChanges();
        var sender = new FakeWebPushSender();
        sender.ExpiredEndpoints.Add("https://ntfy.example/gone");

        await DeviceNotifierService.SendAsync(_db, sender, [_userId], CancellationToken.None);

        var sent = Assert.Single(sender.RawSent);
        Assert.Equal("https://ntfy.example/up1", sent.Target.Endpoint);
        Assert.Equal(DeviceNotifierService.SyncPayload, sent.Payload);
        Assert.Equal(1, _db.Devices.AsNoTracking().Count(d => d.UserId == _userId && d.PushKind == null && d.PushP256dh == null)); // the gone one cleared (the third never had push)
        Assert.Equal(2, _db.Devices.AsNoTracking().Count(d => d.PushKind == "unifiedpush"));
    }
}
