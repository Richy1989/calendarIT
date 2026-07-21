using CalendarIT.Application.Auth;
using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Notifications;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;

namespace CalendarIT.Infrastructure;

/// <summary>
/// Registers the persistence + auth infrastructure. All configuration is read from flat
/// environment-variable keys so the container is 12-factor friendly.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var dbOptions = ReadDatabaseOptions(configuration);
        services.AddSingleton(Options.Create(dbOptions));
        services.AddSingleton(Options.Create(ReadJwtOptions(configuration)));
        services.AddSingleton(TimeProvider.System);

        services.AddDbContext<AppDbContext>(builder => ConfigureProvider(builder, dbOptions));

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();

        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IEventService, EventService>();
        services.AddScoped<ICalendarIoService, CalendarIoService>();

        AddReminders(services, configuration);

        return services;
    }

    private static void AddReminders(IServiceCollection services, IConfiguration configuration)
    {
        var smtp = new SmtpOptions
        {
            Host = configuration["SMTP_HOST"],
            Port = int.TryParse(configuration["SMTP_PORT"], out var port) ? port : 587,
            User = configuration["SMTP_USER"],
            Password = configuration["SMTP_PASSWORD"],
            From = configuration["SMTP_FROM"] ?? "calendarit@localhost",
            UseSsl = bool.TryParse(configuration["SMTP_USE_SSL"], out var ssl) && ssl,
        };
        services.AddSingleton(Options.Create(smtp));

        // Real SMTP when configured; otherwise log emails so reminders still run in dev.
        if (smtp.IsConfigured)
        {
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddScoped<IEmailSender, LogEmailSender>();
        }

        // Quartz: dispatch due reminders once a minute.
        services.AddQuartz(q =>
        {
            var jobKey = new JobKey("reminder-dispatch");
            q.AddJob<ReminderDispatchJob>(o => o.WithIdentity(jobKey));
            q.AddTrigger(t => t
                .ForJob(jobKey)
                .WithIdentity("reminder-dispatch-trigger")
                .WithCronSchedule("0 * * * * ?")); // every minute
        });
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
    }

    private static void ConfigureProvider(DbContextOptionsBuilder builder, DatabaseOptions options)
    {
        switch (options.Provider)
        {
            case DatabaseProvider.Postgres:
                if (string.IsNullOrWhiteSpace(options.PostgresConnection))
                {
                    throw new InvalidOperationException(
                        "POSTGRES_CONNECTION must be set when DATABASE_PROVIDER=Postgres.");
                }
                builder.UseNpgsql(options.PostgresConnection, o =>
                    o.MigrationsAssembly(DatabaseOptions.PostgresMigrationsAssembly));
                break;

            case DatabaseProvider.Sqlite:
            default:
                Directory.CreateDirectory(options.AppDataPath);
                var dbPath = Path.Combine(options.AppDataPath, options.SqliteFileName);
                builder.UseSqlite($"Data Source={dbPath}", o =>
                    o.MigrationsAssembly(DatabaseOptions.SqliteMigrationsAssembly));
                break;
        }
    }

    private static DatabaseOptions ReadDatabaseOptions(IConfiguration configuration)
    {
        var provider = Enum.TryParse<DatabaseProvider>(
            configuration["DATABASE_PROVIDER"], ignoreCase: true, out var p) ? p : DatabaseProvider.Sqlite;

        return new DatabaseOptions
        {
            Provider = provider,
            PostgresConnection = configuration["POSTGRES_CONNECTION"],
            AppDataPath = configuration["APPDATA_PATH"] ?? "appdata"
        };
    }

    private static JwtOptions ReadJwtOptions(IConfiguration configuration)
    {
        var signingKey = configuration["JWT_SIGNING_KEY"];
        if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
        {
            throw new InvalidOperationException(
                "JWT_SIGNING_KEY must be set and at least 32 characters long.");
        }

        return new JwtOptions
        {
            SigningKey = signingKey,
            Issuer = configuration["JWT_ISSUER"] ?? "calendarit",
            Audience = configuration["JWT_AUDIENCE"] ?? "calendarit",
            AccessTokenMinutes = int.TryParse(configuration["JWT_ACCESS_TOKEN_MINUTES"], out var m) ? m : 15,
            RefreshTokenDays = int.TryParse(configuration["JWT_REFRESH_TOKEN_DAYS"], out var d) ? d : 14
        };
    }
}
