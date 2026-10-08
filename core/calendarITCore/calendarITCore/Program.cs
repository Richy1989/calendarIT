using calendarITCore;
using calendarITCore.Extensions;
using calendarITCore.Logging;
using CalendarIT.CalDav;
using CalendarIT.Infrastructure;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Serilog;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();

// The app always runs behind an operator-supplied reverse proxy (TLS terminated
// upstream). Honour X-Forwarded-* so scheme/host/client IP are correct.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The proxy runs outside our network boundary, so its address can't be pinned here.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    // ...but only the configured number of hops is honoured. Unlimited forwarding let any
    // client invent its own X-Forwarded-For, which would both falsify the logged IP and hand
    // out a free bypass of the per-IP auth rate limit below. Default 1 = the reverse proxy
    // directly in front (nginx in the shipped image); raise it when you chain another one.
    options.ForwardLimit = builder.Configuration.GetValue("FORWARDED_PROXY_HOPS", 1);
});

// Password guessing is throttled per account by Identity lockout; this caps the request rate
// itself, so an attacker can't burn server CPU on password hashing (each attempt is a KDF) or
// spray registrations. Only the auth endpoints opt in, via [EnableRateLimiting("auth")].
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("AUTH_RATE_LIMIT_PER_MINUTE", 20),
            Window = TimeSpan.FromMinutes(1),
        }));
    // "Test my mail account" connects out to whatever host the user entered; a handful a minute
    // is plenty for a person and makes it useless as a scanner.
    options.AddPolicy("mail-test", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Errors leave as RFC 7807 problem responses: a 400 with the reason for input the caller can
// fix, a bare 500 for everything else — never a stack trace.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<InvalidInputExceptionHandler>();

// Persistence + Identity + auth services, and JWT bearer validation.
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);

// CalDAV (phone sync via DAVx⁵ etc.): Basic-auth scheme + /dav endpoints.
builder.Services.AddCalDav();

// Liveness has no checks; readiness gathers checks tagged "ready".
builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

var app = builder.Build();

// The first line of the log says what is running, on what. The host's own start-up lines ("Now
// listening on", "Press Ctrl+C to shut down", …) are silenced in appsettings.json: in a container
// they name a port behind the proxy that nobody opens, and a key press that isn't there.
var database = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
app.Logger.LogInformation("CalendarIT {Version} starting · {Database} · data in {DataRoot}",
    AppVersion.Current,
    database.Provider == DatabaseProvider.Postgres ? "PostgreSQL" : "SQLite",
    Path.GetFullPath(database.AppDataPath));
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (app.Environment.IsDevelopment())
    {
        app.Logger.LogInformation("CalendarIT is ready on {Urls}", string.Join(", ", app.Urls));
    }
    else
    {
        app.Logger.LogInformation("CalendarIT is ready");
    }
});
app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("CalendarIT is shutting down"));

// Apply pending migrations on startup unless explicitly disabled (APPLY_MIGRATIONS=false).
if (app.Configuration.GetValue("APPLY_MIGRATIONS", true))
{
    var migration = await app.Services.MigrateDatabaseAsync();
    if (migration.Created)
    {
        app.Logger.LogInformation("Database created");
    }
    else if (migration.Applied.Count > 0)
    {
        app.Logger.LogInformation("Database upgraded: {Migrations}", string.Join(", ", migration.Applied));
    }
    // One-time data upgrade: per-event colors → categories (no-op once assigned).
    await app.Services.BackfillCategoriesAsync();
}

app.UseForwardedHeaders();        // behind nginx/Traefik — must come first
app.UseRequestLog();              // one short line per request, path only (see UseRequestLog)
app.UseExceptionHandler();        // inside the request log, so it records the final status
app.UseSecurityHeaders();

// Serve the built React SPA when it's packaged in wwwroot (the default image). In the bundle image
// nginx serves it, and in local dev the Vite dev server does — and registering static files with
// no wwwroot only logs a warning on every start that nobody can act on.
var servesSpa = Directory.Exists(app.Environment.WebRootPath);
if (servesSpa)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// NOTE: no UseHttpsRedirection — TLS is terminated by the reverse proxy; app serves HTTP.

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapCalDav();

// Liveness: process is up and serving.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false
});

// Readiness: dependencies required to serve traffic are healthy.
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

// SPA client-side routing: any unmatched, non-API route serves the app shell.
if (servesSpa)
{
    app.MapFallbackToFile("index.html");
}

app.Run();
