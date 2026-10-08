using calendarITCore.Logging;
using Microsoft.AspNetCore.Http;
using Serilog.Events;
using Serilog.Parsing;

namespace CalendarIT.Tests;

/// <summary>
/// The console log line. It goes to a terminal more often than not — Unraid's log view is one —
/// so what matters most is that nothing from outside reaches it raw: a request path or a logged
/// value carrying escape sequences would otherwise recolour, move or overwrite the log. After that,
/// that a request reads as one short line, and that NO_COLOR leaves plain text.
/// </summary>
public sealed class ConsoleLogFormatterTests
{
    private const char Esc = '\u001b';

    // Ordinal throughout: a culture-aware comparison ignores control characters, ESC among them,
    // so it would find "[2J" in the harmless "\x1b[2J" and call it the escape sequence.
    private const StringComparison Exact = StringComparison.Ordinal;

    private static string Format(LogEvent logEvent, bool color = false)
    {
        var output = new StringWriter();
        new ConsoleLogFormatter(color).Format(logEvent, output);
        return output.ToString();
    }

    private static LogEvent Event(
        string template,
        LogEventLevel level = LogEventLevel.Information,
        Exception? exception = null,
        params (string Name, object? Value)[] properties) =>
        new(
            new DateTimeOffset(2026, 10, 4, 18, 26, 3, TimeSpan.Zero),
            level,
            exception,
            new MessageTemplateParser().Parse(template),
            properties.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value))));

    /// <summary>The request-logging middleware's event; it logs a 5xx as an error.</summary>
    private static LogEvent Request(string method, string path, int status, double elapsedMs) => Event(
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms",
        status >= 500 ? LogEventLevel.Error : LogEventLevel.Information,
        properties: [("RequestMethod", method), ("RequestPath", path), ("StatusCode", status), ("Elapsed", elapsedMs)]);

    [Fact]
    public void A_request_is_one_short_line_in_columns()
    {
        var line = Format(Request("POST", "/api/events", 201, 262.4));

        Assert.Equal("18:26:03 INF  POST   /api/events" + new string(' ', 29) + " 201  262 ms" + Environment.NewLine, line);
    }

    [Fact]
    public void A_guid_in_a_path_is_cut_to_its_first_eight_characters()
    {
        var line = Format(Request(
            "PUT", "/api/events/5e531dac-1c2d-4a5b-9f00-0123456789ab/occurrence", 200, 26));

        Assert.Contains("/api/events/5e531dac/occurrence ", line, Exact);
    }

    [Fact]
    public void A_bare_guid_in_a_caldav_path_is_cut_too()
    {
        // CalDAV hrefs carry calendar ids as 32 hex digits, and the UIDs this app mints are the same.
        var line = Format(Request(
            "GET", "/dav/calendars/5e531dac1c2d4a5b9f000123456789ab/6a43096e4a2947a6aca5f35fac2b01b9@calendarit.ics", 200, 3));

        Assert.Contains("/dav/calendars/5e531dac/6a43096e@calendarit.ics ", line, Exact);
    }

    [Fact]
    public void A_longer_run_of_hex_is_not_mistaken_for_a_guid()
    {
        var line = Format(Request("GET", "/assets/0123456789abcdef0123456789abcdef01.js", 404, 1));

        Assert.Contains("/assets/0123456789abcdef0123456789abcdef01.js", line, Exact);
    }

    [Theory]
    [InlineData(4.2, "4 ms")]
    [InlineData(1450, "1.5 s")]
    [InlineData(720_000, "12 min")]
    public void Durations_read_in_the_unit_that_fits(double elapsedMs, string shown)
    {
        Assert.EndsWith(shown + Environment.NewLine, Format(Request("POST", "/api/events/import", 200, elapsedMs)), Exact);
    }

    [Fact]
    public void An_escape_sequence_in_a_request_path_is_shown_not_obeyed()
    {
        var line = Format(Request("GET", "/api/ci\u001b[2J\u009b31m", 404, 1), color: true);

        Assert.Contains(@"/api/ci\x1b[2J\x9b31m", line, Exact);
        // The formatter's own colour codes are the only escapes left: each one is a 38;5 or 1;… SGR
        // or a reset, never what the path asked for.
        Assert.DoesNotContain($"{Esc}[2J", line, Exact);
        Assert.DoesNotContain('\u009b', line);
    }

    [Fact]
    public void An_escape_sequence_in_a_logged_value_is_shown_not_obeyed()
    {
        var line = Format(Event("Sent email {Subject} to {To}", properties:
            [("Subject", "Reset\u001b]0;pwned\u0007"), ("To", "a@example.com")]), color: true);

        Assert.Contains(@"Reset\x1b]0;pwned\x07", line, Exact);
        Assert.DoesNotContain($"{Esc}]0;", line, Exact);
    }

    [Fact]
    public void Without_colour_there_is_no_escape_at_all()
    {
        var line = Format(Request("DELETE", "/api/calendars/1", 500, 3000));

        Assert.DoesNotContain(Esc, line);
        Assert.StartsWith("18:26:03 ERR  DELETE /api/calendars/1", line, Exact);
    }

    [Fact]
    public void A_string_value_is_written_bare_and_others_as_rendered()
    {
        var line = Format(Event("Maintenance: removed {Count} old {What} in {Folder}", properties:
            [("Count", 3), ("What", "refresh tokens"), ("Folder", "/data")]));

        Assert.Equal("18:26:03 INF  Maintenance: removed 3 old refresh tokens in /data" + Environment.NewLine, line);
    }

    [Fact]
    public void A_message_over_several_lines_continues_under_its_first()
    {
        var line = Format(Event("Email delivery is not configured.\nTo: {To}", LogEventLevel.Warning,
            properties: [("To", "a@example.com")]));

        Assert.Equal(
            "18:26:03 WRN  Email delivery is not configured." + Environment.NewLine +
            "              To: a@example.com" + Environment.NewLine,
            line);
    }

    [Fact]
    public void An_exception_follows_its_line_indented()
    {
        Exception thrown;
        try { throw new InvalidOperationException("dispatch failed"); }
        catch (InvalidOperationException e) { thrown = e; }

        var lines = Format(Event("Reminder dispatch tick failed", LogEventLevel.Error, thrown))
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("18:26:03 ERR  Reminder dispatch tick failed", lines[0]);
        Assert.StartsWith("              System.InvalidOperationException: dispatch failed", lines[1], Exact);
        Assert.All(lines.Skip(1), l => Assert.StartsWith(new string(' ', 14), l, Exact));
    }

    [Theory]
    [InlineData("/health", 200, LogEventLevel.Debug)]
    [InlineData("/ready", 200, LogEventLevel.Debug)]
    [InlineData("/ready", 503, LogEventLevel.Error)]
    [InlineData("/api/events", 200, LogEventLevel.Information)]
    [InlineData("/api/events", 500, LogEventLevel.Error)]
    public void Healthcheck_probes_stay_out_of_the_log_unless_they_fail(string path, int status, LogEventLevel expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.StatusCode = status;

        Assert.Equal(expected, LoggingServiceExtensions.RequestLevel(context, 1, null));
    }

    [Fact]
    public void A_request_line_shows_the_path_as_requested_not_as_rewritten()
    {
        // UseRequestLog records the path on the way in; the SPA fallback then rewrites it.
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Items["calendarit.requested-path"] = "/calendar/2026-10";
        context.Request.Path = "/index.html";

        var properties = LoggingServiceExtensions.RequestProperties(context, "/index.html", 3, 200)
            .ToDictionary(p => p.Name, p => ((ScalarValue)p.Value).Value);

        Assert.Equal("/calendar/2026-10", properties["RequestPath"]);
        Assert.Equal("GET", properties["RequestMethod"]);
        Assert.Equal(200, properties["StatusCode"]);
    }
}
