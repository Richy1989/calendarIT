using calendarITCore.Extensions;
using calendarITCore.Logging;
using CalendarIT.Infrastructure;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();

// The app always runs behind an operator-supplied reverse proxy (TLS terminated
// upstream). Honour X-Forwarded-* so scheme/host/client IP are correct.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Proxy runs outside our network boundary; trust it explicitly in deployment config.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Persistence + Identity + auth services, and JWT bearer validation.
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);

// Liveness has no checks; readiness gathers checks tagged "ready".
builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

var app = builder.Build();

// Apply pending migrations on startup unless explicitly disabled (APPLY_MIGRATIONS=false).
if (app.Configuration.GetValue("APPLY_MIGRATIONS", true))
{
    await app.Services.MigrateDatabaseAsync();
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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

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
