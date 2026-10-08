using Serilog;
using Serilog.Events;

namespace calendarITCore.Logging;

/// <summary>
/// Wires up Serilog as the app's logging provider (app code keeps using <c>ILogger&lt;T&gt;</c>),
/// writing to the console in <see cref="ConsoleLogFormatter"/>'s short, coloured lines. Levels and
/// per-source overrides are read from the "Serilog" config section, so verbosity is tunable without
/// a recompile; the format lives in code.
/// </summary>
public static class LoggingServiceExtensions
{
    /// <summary>
    /// Replaces the default logging with Serilog: the console formatter plus <c>LogContext</c>
    /// enrichment. The host flushes Serilog on shutdown, so no manual teardown is needed.
    /// </summary>
    public static WebApplicationBuilder AddSerilogLogging(this WebApplicationBuilder builder)
    {
        var formatter = new ConsoleLogFormatter(ConsoleLogFormatter.ColorFromEnvironment());

        builder.Host.UseSerilog((context, services, config) => config
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            // Logged as a warning whenever Data Protection makes a new key, and expected here: the
            // key ring sits in the data folder beside the database it protects, so encrypting it
            // with a key kept in that same folder would add nothing. A warning nobody should act
            // on only teaches people to skim past the ones they should.
            .Filter.ByExcluding(e =>
                e.MessageTemplate.Text.StartsWith("No XML encryptor configured", StringComparison.Ordinal))
            .WriteTo.Console(formatter));

        return builder;
    }

    /// <summary>Where <see cref="UseRequestLog"/> keeps the path a request arrived with.</summary>
    private const string RequestedPathKey = "calendarit.requested-path";

    /// <summary>
    /// One line per request, laid out by <see cref="ConsoleLogFormatter"/>. Call right after the
    /// forwarded headers, so the client IP is real.
    /// <para>
    /// The line shows the path as requested — the SPA fallback rewrites an unmatched path to
    /// /index.html before the line is written, which would log every deep link (and every unknown
    /// API route) as "/index.html". The path only, never the query string: a URL can carry a token,
    /// as a password-reset link does.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseRequestLog(this IApplicationBuilder app)
    {
        app.Use((context, next) =>
        {
            context.Items[RequestedPathKey] = context.Request.Path.Value;
            return next(context);
        });
        return app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = RequestLevel;
            options.GetMessageTemplateProperties = RequestProperties;
        });
    }

    /// <summary>The request line's values: as Serilog's own, but with the path as requested.</summary>
    public static IEnumerable<LogEventProperty> RequestProperties(HttpContext context, string path, double elapsedMs, int statusCode) =>
    [
        new("RequestMethod", new ScalarValue(context.Request.Method)),
        new("RequestPath", new ScalarValue(context.Items[RequestedPathKey] as string ?? path)),
        new("StatusCode", new ScalarValue(statusCode)),
        new("Elapsed", new ScalarValue(elapsedMs)),
    ];

    /// <summary>
    /// The level of a request's log line: a successful liveness/readiness probe at Debug — Docker's
    /// healthcheck asks every 30 seconds, and a line each time would bury the requests people make.
    /// A failing probe, and everything else, as Serilog would log it (5xx and exceptions as errors).
    /// </summary>
    public static LogEventLevel RequestLevel(HttpContext context, double elapsedMs, Exception? exception)
    {
        if (exception is not null || context.Response.StatusCode >= 500)
        {
            return LogEventLevel.Error;
        }
        var path = context.Request.Path;
        var probe = path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/ready", StringComparison.OrdinalIgnoreCase);
        return probe && context.Response.StatusCode < 400 ? LogEventLevel.Debug : LogEventLevel.Information;
    }
}
