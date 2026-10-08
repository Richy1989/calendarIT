using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarIT.Infrastructure.Persistence;

/// <summary>Applies pending EF Core migrations on startup when enabled.</summary>
public static class DatabaseMigrator
{
    /// <summary>
    /// Creates a scope, resolves <see cref="AppDbContext"/>, and applies any pending migrations.
    /// Returns what it did, so start-up can say what an upgrade changed instead of announcing
    /// "migrations applied" on every start.
    /// </summary>
    public static async Task<MigrationResult> MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var created = !(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);
        return new MigrationResult(pending, created);
    }
}

/// <summary>The migrations applied at start-up (none on an up-to-date database), and whether the
/// database was new — every migration then, which is a creation rather than an upgrade.</summary>
public sealed record MigrationResult(IReadOnlyList<string> Applied, bool Created);
