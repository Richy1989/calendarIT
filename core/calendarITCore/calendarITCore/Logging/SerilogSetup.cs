using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.SystemConsole.Themes;

namespace calendarITCore.Logging;

/// <summary>
/// Central Serilog wiring. Two output modes, selected by the <c>LOG_FORMAT</c> setting:
/// <list type="bullet">
///   <item><c>console</c> — colorful, human-readable ANSI output for local dev.</item>
///   <item><c>json</c> — compact structured JSON to stdout for containers/production.</item>
/// </list>
/// The global minimum level comes from <c>LOG_LEVEL</c>, with per-namespace overrides
/// from <c>LOG_LEVEL__&lt;Namespace&gt;</c>. Colour is auto-disabled when stdout is not a TTY.
/// </summary>
public static class SerilogSetup
{
    /// <summary>Human-readable dev template with source context and structured props.</summary>
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj} " +
        "{Properties:j}{NewLine}{Exception}";

    /// <summary>
    /// Configures a Serilog logger from application configuration/environment.
    /// Used both for the early bootstrap logger and the final host logger.
    /// </summary>
    public static LoggerConfiguration Configure(
        LoggerConfiguration logger,
        IConfiguration configuration,
        bool isDevelopment)
    {
        var format = ResolveFormat(configuration, isDevelopment);
        var minimumLevel = ResolveLevel(configuration["LOG_LEVEL"], LogEventLevel.Information);

        logger
            .MinimumLevel.Is(minimumLevel)
            // Framework noise defaults; overridable via LOG_LEVEL__<Namespace>.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext();

        ApplyNamespaceOverrides(logger, configuration);

        if (format == "json")
        {
            logger.WriteTo.Console(new CompactJsonFormatter());
        }
        else
        {
            logger.WriteTo.Console(
                theme: AnsiConsoleTheme.Code,
                outputTemplate: ConsoleTemplate,
                applyThemeToRedirectedOutput: false); // no colour when piped/CI
        }

        return logger;
    }

    private static string ResolveFormat(IConfiguration configuration, bool isDevelopment)
    {
        var format = configuration["LOG_FORMAT"];
        if (!string.IsNullOrWhiteSpace(format))
        {
            return format.Trim().ToLowerInvariant();
        }

        // Default: colourful console in dev, structured JSON everywhere else.
        return isDevelopment ? "console" : "json";
    }

    private static LogEventLevel ResolveLevel(string? value, LogEventLevel fallback) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level) ? level : fallback;

    /// <summary>Applies LOG_LEVEL__&lt;Namespace&gt; overrides (double underscore = env-var friendly).</summary>
    private static void ApplyNamespaceOverrides(LoggerConfiguration logger, IConfiguration configuration)
    {
        const string prefix = "LOG_LEVEL__";
        foreach (var kvp in configuration.AsEnumerable())
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(kvp.Value))
            {
                var ns = kvp.Key[prefix.Length..];
                if (!string.IsNullOrWhiteSpace(ns) &&
                    Enum.TryParse<LogEventLevel>(kvp.Value, ignoreCase: true, out var level))
                {
                    logger.MinimumLevel.Override(ns, level);
                }
            }
        }
    }
}
