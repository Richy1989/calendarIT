using CalendarIT.Domain;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Notifications;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarIT.Tests;

/// <summary>The nightly prune removes what is dead and nothing that is still in use.</summary>
public sealed class MaintenanceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 3, 17, 0, TimeSpan.Zero);

    public MaintenanceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "m@test", NormalizedUserName = "M@TEST" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private RefreshToken Token(string hash, DateTimeOffset expires) => new()
    {
        Id = Guid.NewGuid(), UserId = _userId, TokenHash = hash, CreatedAt = expires.AddDays(-14), ExpiresAt = expires,
        SessionId = Guid.NewGuid(), SessionStartedAt = expires.AddDays(-14),
    };

    private OutboxMessage Mail(OutboxStatus status, DateTime created) => new()
    {
        Id = Guid.NewGuid(), UserId = _userId, Status = status, Kind = OutboxKind.Invitation,
        Recipient = "x@example.com", Subject = "s", CreatedAtUtc = created, NextAttemptAtUtc = created,
    };

    [Fact]
    public async Task PrunesTheDead_KeepsTheLive()
    {
        var now = Now.UtcDateTime;
        _db.RefreshTokens.AddRange(Token("expired", Now.AddDays(-3)), Token("live", Now.AddDays(10)));
        _db.NotificationLogs.AddRange(
            new NotificationLog { Id = Guid.NewGuid(), ReminderId = Guid.NewGuid(), OccurrenceStartUtc = now.AddDays(-90), SentAtUtc = now.AddDays(-90) },
            new NotificationLog { Id = Guid.NewGuid(), ReminderId = Guid.NewGuid(), OccurrenceStartUtc = now.AddDays(-1), SentAtUtc = now.AddDays(-1) });
        _db.OutboxMessages.AddRange(
            Mail(OutboxStatus.Sent, now.AddDays(-30)),
            Mail(OutboxStatus.Sent, now.AddDays(-1)),
            Mail(OutboxStatus.Failed, now.AddDays(-60)),
            Mail(OutboxStatus.Pending, now.AddDays(-60)));
        await _db.SaveChangesAsync();

        await new MaintenanceJob(
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new FixedClock(), NullLogger<MaintenanceJob>.Instance)
            .RunAsync(_db);

        Assert.Equal("live", Assert.Single(_db.RefreshTokens.AsNoTracking()).TokenHash);
        Assert.Single(_db.NotificationLogs.AsNoTracking());
        Assert.Equal(2, await _db.OutboxMessages.CountAsync()); // the recent sent one + the pending one
    }
}
