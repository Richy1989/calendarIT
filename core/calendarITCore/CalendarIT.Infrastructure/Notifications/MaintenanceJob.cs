using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Prunes the bookkeeping tables that otherwise only ever grow: refresh tokens (one row per
/// refresh, every fifteen minutes, per device), the reminder dispatch log (one row per reminder
/// sent), and the outbox's record of mail already handled. Runs nightly; each step is
/// independent, so one failing doesn't skip the others.
/// </summary>
[DisallowConcurrentExecution]
public sealed class MaintenanceJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<MaintenanceJob> logger) : IJob
{
    public static readonly JobKey Key = new("maintenance");

    /// <summary>How long dispatch records are kept. Only the recent past matters for dedup — a
    /// reminder can't come due again for an occurrence days behind us.</summary>
    private static readonly TimeSpan KeepNotificationLogs = TimeSpan.FromDays(60);

    private static readonly TimeSpan KeepSentMail = TimeSpan.FromDays(14);
    private static readonly TimeSpan KeepFailedMail = TimeSpan.FromDays(30);

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await RunAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), context.CancellationToken);
    }

    public async Task RunAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var nowUtc = now.UtcDateTime;

        await StepAsync("refresh tokens", async () =>
        {
            // An expired token is dead whatever its state, revoked or not; reuse detection only
            // needs revoked tokens while they could still be presented. ExpiresAt is a
            // DateTimeOffset, which SQLite can't compare in SQL — so the candidates are read and
            // filtered here, then deleted by id.
            var expired = (await db.RefreshTokens.AsNoTracking()
                    .Select(t => new { t.Id, t.ExpiresAt })
                    .ToListAsync(cancellationToken))
                .Where(t => t.ExpiresAt < now.AddDays(-1))
                .Select(t => t.Id)
                .ToList();
            var deleted = 0;
            foreach (var chunk in expired.Chunk(500))
            {
                deleted += await db.RefreshTokens.Where(t => chunk.Contains(t.Id)).ExecuteDeleteAsync(cancellationToken);
            }
            return deleted;
        });

        await StepAsync("reminder dispatch log", () =>
            db.NotificationLogs
                .Where(n => n.OccurrenceStartUtc < nowUtc - KeepNotificationLogs)
                .ExecuteDeleteAsync(cancellationToken));

        await StepAsync("sent mail records", () =>
            db.OutboxMessages
                .Where(m => m.Status == OutboxStatus.Sent && m.CreatedAtUtc < nowUtc - KeepSentMail)
                .ExecuteDeleteAsync(cancellationToken));

        await StepAsync("failed mail", () =>
            db.OutboxMessages
                .Where(m => m.Status == OutboxStatus.Failed && m.CreatedAtUtc < nowUtc - KeepFailedMail)
                .ExecuteDeleteAsync(cancellationToken));
    }

    private async Task StepAsync(string what, Func<Task<int>> step)
    {
        try
        {
            var removed = await step();
            if (removed > 0)
            {
                logger.LogInformation("Maintenance: removed {Count} old {What}", removed, what);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Maintenance: pruning {What} failed", what);
        }
    }
}
