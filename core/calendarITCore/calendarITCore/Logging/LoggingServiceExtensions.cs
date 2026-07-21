using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace calendarITCore.Logging;

/// <summary>
/// Serilog wiring: Serilog is only the logging provider — app code keeps using
/// <c>ILogger&lt;T&gt;</c>. Console-only, themed, with levels read from the "Serilog"
/// section of configuration (appsettings.json) so verbosity is tunable without a recompile.
/// </summary>
public static class LoggingServiceExtensions
{
    // ANSI escape (ESC, U+001B) that introduces an SGR color sequence.
    private static readonly string Esc = ((char)27).ToString();

    public static WebApplicationBuilder AddSerilogLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, config) => config
            .ReadFrom.Configuration(context.Configuration) // levels from the "Serilog" section
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                theme: ColorfulTheme,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

        return builder; // host flushes Serilog on shutdown — no manual teardown
    }

    // High-contrast 256-color theme. 38;5;<n> = foreground, 48;5;<n> = background, <n> = 0–255 xterm color.
    // Prefer a ready-made look? Swap for AnsiConsoleTheme.Literate or .Sixteen in the sink above.
    private static readonly AnsiConsoleTheme ColorfulTheme = new(
        new Dictionary<ConsoleThemeStyle, string>
        {
            [ConsoleThemeStyle.Text]             = $"{Esc}[38;5;253m",
            [ConsoleThemeStyle.SecondaryText]    = $"{Esc}[38;5;244m",
            [ConsoleThemeStyle.TertiaryText]     = $"{Esc}[38;5;240m",
            [ConsoleThemeStyle.Invalid]          = $"{Esc}[38;5;232;48;5;208m",
            [ConsoleThemeStyle.Null]             = $"{Esc}[38;5;141m",
            [ConsoleThemeStyle.Name]             = $"{Esc}[38;5;81m",
            [ConsoleThemeStyle.String]           = $"{Esc}[38;5;114m",
            [ConsoleThemeStyle.Number]           = $"{Esc}[38;5;208m",
            [ConsoleThemeStyle.Boolean]          = $"{Esc}[38;5;75m",
            [ConsoleThemeStyle.Scalar]           = $"{Esc}[38;5;79m",
            [ConsoleThemeStyle.LevelVerbose]     = $"{Esc}[38;5;244m",
            [ConsoleThemeStyle.LevelDebug]       = $"{Esc}[38;5;39m",
            [ConsoleThemeStyle.LevelInformation] = $"{Esc}[38;5;48m",
            [ConsoleThemeStyle.LevelWarning]     = $"{Esc}[1m{Esc}[38;5;220m",
            [ConsoleThemeStyle.LevelError]       = $"{Esc}[1m{Esc}[38;5;231;48;5;160m",
            [ConsoleThemeStyle.LevelFatal]       = $"{Esc}[1m{Esc}[38;5;231;48;5;196m",
        });
}
