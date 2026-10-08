using CalendarIT.Application.Auth;
using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Application.Mail;
using CalendarIT.Application.Notifications;
using CalendarIT.Application.Profile;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Notifications;
using CalendarIT.Infrastructure.Persistence;
using CalendarIT.Infrastructure.Profile;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

        // VAPID keys for browser Web Push — read from env, else generated once under AppDataPath.
        // Built eagerly (does file IO once) so both the sender and the subscribe endpoint share it.
        services.AddSingleton(new VapidKeyStore(configuration, dbOptions));

        services.AddSingleton(Options.Create(ReadJwtOptions(configuration)));
        services.AddSingleton(Options.Create(new AuthOptions
        {
            DisableRegistration = configuration.GetValue("DISABLE_REGISTRATION", false),
        }));
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
                // Brute-force brake for both the web login and CalDAV Basic (AuthService and
                // CalDavBasicAuthHandler drive the counter themselves — this API has no
                // SignInManager, which is what would normally apply it). Generous enough that a
                // phone still holding an old password doesn't lock its owner out for long.
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 10;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            // Password-reset tokens. Registered explicitly rather than via AddDefaultTokenProviders,
            // which lives in the full ASP.NET Core Identity assembly this project doesn't reference.
            // The token is Data-Protection-signed and bound to the user's SecurityStamp, so it dies
            // the moment the password changes — a link can't be replayed after it has been used.
            .AddTokenProvider<DataProtectorTokenProvider<ApplicationUser>>(TokenOptions.DefaultProvider);

        // A reset link is a bearer credential sitting in an inbox; a day is longer than anyone
        // needs to click it.
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));

        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<SessionValidator>();
        services.AddSingleton<CredentialEpochs>();
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
        services.AddScoped<IIncomingInvitationService, IncomingInvitationService>();
        services.AddScoped<IPasswordResetMailer, PasswordResetMailer>();

        // Outgoing mail is queued (IMailOutbox) and sent by OutboxDispatchJob — never inline.
        // MAIL_HOST_POLICY bounds where users' mail servers may point: "private" (default: public
        // internet + LAN, never this host or link-local), "public", or "any".
        services.AddSingleton(new MailConnectionOptions
        {
            HostScope = Enum.TryParse<Net.OutboundHostScope>(configuration["MAIL_HOST_POLICY"], ignoreCase: true, out var scope)
                ? scope
                : Net.OutboundHostScope.Private,
        });
        services.AddScoped<IMailOutbox, MailOutbox>();
        services.AddSingleton<IOutboxSignal>(sp => new QuartzOutboxSignal(
            sp.GetService<ISchedulerFactory>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<QuartzOutboxSignal>>()));
        services.AddMemoryCache();

        // Browser Web Push: a reusable, thread-safe sender (singleton) and a per-request store.
        services.AddSingleton<IWebPushSender, WebPushSender>();
        services.AddScoped<IPushSubscriptionService, PushSubscriptionService>();

        // Read-only per-user due-reminder query for the local-notification fallback poller.
        services.AddScoped<IDueReminderQuery, DueReminderQuery>();

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

            // The outbox ticks every 30 seconds, and is also triggered right after mail is queued.
            q.AddJob<OutboxDispatchJob>(o => o.WithIdentity(OutboxDispatchJob.Key));
            q.AddTrigger(t => t
                .ForJob(OutboxDispatchJob.Key)
                .WithIdentity("outbox-dispatch-trigger")
                .WithCronSchedule("0/30 * * * * ?"));

            // Nightly housekeeping of tables that only grow (tokens, dispatch log, sent mail).
            q.AddJob<MaintenanceJob>(o => o.WithIdentity(MaintenanceJob.Key));
            q.AddTrigger(t => t
                .ForJob(MaintenanceJob.Key)
                .WithIdentity("maintenance-trigger")
                .WithCronSchedule("0 17 3 * * ?")); // 03:17 daily

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
                builder.UseNpgsql(options.PostgresConnection, o => o
                    .MigrationsAssembly(DatabaseOptions.PostgresMigrationsAssembly)
                    // Events load several collections at once (reminders, guests, overrides); one
                    // joined query multiplies their rows into each other.
                    .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
                break;

            case DatabaseProvider.Sqlite:
            default:
                Directory.CreateDirectory(options.AppDataPath);
                var dbPath = Path.Combine(options.AppDataPath, options.SqliteFileName);
                builder.UseSqlite($"Data Source={dbPath}", o => o
                    .MigrationsAssembly(DatabaseOptions.SqliteMigrationsAssembly)
                    .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
                // SQLite can't alter a table, so EF rebuilds one inside a migration with foreign keys
                // switched off for the copy — a PRAGMA that can't run in a transaction, which EF
                // warns about every time it runs one. It's how every such migration has to work, and
                // nobody reading the log can do anything about it.
                builder.ConfigureWarnings(w => w.Ignore(RelationalEventId.NonTransactionalMigrationOperationWarning));
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
