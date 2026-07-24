using calendarITCore.Extensions;
using calendarITCore.Logging;
using CalendarIT.CalDav;
using CalendarIT.Infrastructure;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
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
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

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

// Apply pending migrations on startup unless explicitly disabled (APPLY_MIGRATIONS=false).
if (app.Configuration.GetValue("APPLY_MIGRATIONS", true))
{
    await app.Services.MigrateDatabaseAsync();
    // One-time data upgrade: per-event colors → categories (no-op once assigned).
    await app.Services.BackfillCategoriesAsync();
    app.Logger.LogInformation("Database migrations applied");
}

app.UseForwardedHeaders();        // behind nginx/Traefik — must come first
app.UseSerilogRequestLogging();   // one clean summary line per HTTP request, with the real client IP

// Serve the built React SPA (present in wwwroot when packaged in the container).
// In local dev the SPA runs on the Vite dev server instead, so these are no-ops.
app.UseDefaultFiles();
app.UseStaticFiles();

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
app.MapFallbackToFile("index.html");

app.Run();
