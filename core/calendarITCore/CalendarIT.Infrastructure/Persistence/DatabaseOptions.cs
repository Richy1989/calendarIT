namespace CalendarIT.Infrastructure.Persistence;

/// <summary>Supported EF Core database providers.</summary>
public enum DatabaseProvider
{
    /// <summary>File-based fallback, used for dev and small single-node deploys.</summary>
    Sqlite,

    /// <summary>Primary production database.</summary>
    Postgres
}

/// <summary>
/// Database configuration, populated from environment variables:
/// <c>DATABASE_PROVIDER</c>, <c>POSTGRES_CONNECTION</c>, <c>APPDATA_PATH</c>.
/// </summary>
public sealed class DatabaseOptions
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>Npgsql connection string; required when <see cref="Provider"/> is Postgres.</summary>
    public string? PostgresConnection { get; set; }

    /// <summary>Writable directory for the SQLite file and other app data (container volume).</summary>
    public string AppDataPath { get; set; } = "appdata";

    /// <summary>SQLite database file name, created under <see cref="AppDataPath"/>.</summary>
    public string SqliteFileName { get; set; } = "calendarit.db";

    /// <summary>Migrations assembly names per provider (kept in dedicated projects).</summary>
    public const string PostgresMigrationsAssembly = "CalendarIT.Migrations.Postgres";
    public const string SqliteMigrationsAssembly = "CalendarIT.Migrations.Sqlite";
}
