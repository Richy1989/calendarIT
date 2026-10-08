using System.Reflection;

namespace calendarITCore;

/// <summary>The running build's version string, resolved once — the assembly can't change while the
/// process runs.</summary>
public static class AppVersion
{
    /// <summary>
    /// The release tag (injected by the Docker build via <c>/p:Version</c>) plus a <c>+sha</c>
    /// suffix — appended by the SDK for local git builds, or passed in by the release build. A full
    /// 40-char sha is clipped to 7 for display. E.g. <c>0.4.0+ab12cd3</c>, or <c>unknown</c> when
    /// the attribute is missing.
    /// </summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var info = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return "unknown";
        }

        var plus = info.IndexOf('+');
        if (plus < 0 || info.Length - plus - 1 <= 7)
        {
            return info;
        }
        return info[..(plus + 1 + 7)];
    }
}
