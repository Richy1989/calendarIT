using CalendarIT.Application.Auth;
using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Application.Mail;
using CalendarIT.Application.Profile;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Notifications;
using CalendarIT.Infrastructure.Persistence;
using CalendarIT.Infrastructure.Profile;
using Microsoft.AspNetCore.DataProtection;
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

        // Data Protection encrypts per-user mailbox passwords at rest. Keys persist under
        // APPDATA_PATH (next to the SQLite db) so ciphertexts survive container restarts.
        var keysDir = Path.Combine(dbOptions.AppDataPath, "dataprotection-keys");
        Directory.CreateDirectory(keysDir);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
            .SetApplicationName("CalendarIT");

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
        services.AddScoped<IInternalInvitationDelivery, InternalInvitationDelivery>();
        services.AddScoped<ICalendarService, CalendarService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ICalendarIoService, CalendarIoService>();
        services.AddScoped<IProfileService, ProfileService>();

        // Mail account, invitations, and reply processing. The concrete MailAccountService is
        // also registered so senders can reach the decrypted password (internal API, never leaves
        // the server). All outgoing mail — invitations and reminders — goes through each user's
        // own connected account; there is no global SMTP relay to configure.
        services.AddScoped<MailAccountService>();
        services.AddScoped<IMailAccountService>(sp => sp.GetRequiredService<MailAccountService>());
        services.AddScoped<IInvitationMailer, InvitationMailer>();
        services.AddScoped<IInvitationReplyService, InvitationReplyService>();
        services.AddScoped<IUserMailSender, UserMailSender>();

        AddBackgroundJobs(services);

        return services;
    }

    private static void AddBackgroundJobs(IServiceCollection services)
    {
        // Quartz: both jobs tick once a minute. The reminder job fires due reminders; the inbox
        // job scans each account no more often than its own ScanIntervalMinutes.
        services.AddQuartz(q =>
        {
            var reminderKey = new JobKey("reminder-dispatch");
            q.AddJob<ReminderDispatchJob>(o => o.WithIdentity(reminderKey));
            q.AddTrigger(t => t
                .ForJob(reminderKey)
                .WithIdentity("reminder-dispatch-trigger")
                .WithCronSchedule("0 * * * * ?")); // every minute

            var inboxKey = new JobKey("invitation-inbox");
            q.AddJob<InvitationInboxJob>(o => o.WithIdentity(inboxKey));
            q.AddTrigger(t => t
                .ForJob(inboxKey)
                .WithIdentity("invitation-inbox-trigger")
                .WithCronSchedule("0 * * * * ?")); // every minute; per-account interval gates the work
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
