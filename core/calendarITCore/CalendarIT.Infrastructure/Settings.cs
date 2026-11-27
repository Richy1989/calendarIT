using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CalendarIT.Infrastructure;

/// <summary>
/// Typed reads of the operator's settings (environment variables). An empty value means "not set"
/// — Compose files, <c>.env</c> templates and Unraid all pass <c>SETTING=</c> for an option left
/// blank, and <c>IConfiguration.GetValue</c> throws on "" for a number or a true/false, which
/// stopped the server at start-up. A value that is set but not readable still stops it, with a
/// message naming the setting.
/// </summary>
public static class Settings
{
    public static T Setting<T>(this IConfiguration configuration, string key, T fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        try
        {
            return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFromString(null, CultureInfo.InvariantCulture, raw.Trim())!;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException
                                       || ex.InnerException is FormatException or OverflowException)
        {
            throw new InvalidOperationException(
                $"The setting {key}='{raw}' isn't a valid {(typeof(T) == typeof(bool) ? "true/false" : "number")}.", ex);
        }
    }

    /// <summary>A text setting, or <paramref name="fallback"/> when unset or empty.</summary>
    public static string Text(this IConfiguration configuration, string key, string fallback) =>
        configuration[key] is { } raw && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : fallback;
}
