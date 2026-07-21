using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarIT.Infrastructure.Persistence;

/// <summary>Applies pending EF Core migrations on startup when enabled.</summary>
public static class DatabaseMigrator
{
    /// <summary>
    /// Creates a scope, resolves <see cref="AppDbContext"/>, and applies any pending
    /// migrations. For SQLite, ensures the containing app-data directory exists first.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
