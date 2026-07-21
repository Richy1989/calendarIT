using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CalendarIT.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> to build the context when adding or
/// applying migrations, outside the running host. The provider is chosen from the
/// <c>DATABASE_PROVIDER</c> environment variable (default: Sqlite), and each provider
/// targets its dedicated migrations assembly. Connection strings here are placeholders —
/// EF does not need a live database to scaffold migration code.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var provider = Enum.TryParse<DatabaseProvider>(
            Environment.GetEnvironmentVariable("DATABASE_PROVIDER"), ignoreCase: true, out var p)
            ? p
            : DatabaseProvider.Sqlite;

        var builder = new DbContextOptionsBuilder<AppDbContext>();

        switch (provider)
        {
            case DatabaseProvider.Postgres:
                var pgConn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                    ?? "Host=localhost;Database=calendarit;Username=postgres;Password=postgres";
                builder.UseNpgsql(pgConn, o =>
                    o.MigrationsAssembly(DatabaseOptions.PostgresMigrationsAssembly));
                break;

            default:
                builder.UseSqlite("Data Source=calendarit-design.db", o =>
                    o.MigrationsAssembly(DatabaseOptions.SqliteMigrationsAssembly));
                break;
        }

        return new AppDbContext(builder.Options);
    }
}
