namespace CalendarIT.Infrastructure.Auth;

/// <summary>Account-policy settings for the instance, read from environment variables.</summary>
public sealed class AuthOptions
{
    /// <summary>
    /// Closes public sign-up (<c>DISABLE_REGISTRATION=true</c>). The instance is reachable by
    /// anyone who knows its address — that is what makes phone sync work — so on a personal or
    /// family server this should usually be turned on once everyone has an account.
    /// </summary>
    public bool DisableRegistration { get; init; }

    /// <summary>
    /// The address users type (<c>PUBLIC_BASE_URL</c>), the only origin links in account mail are
    /// built from — never the request's Host, which is a header anyone can set.
    /// </summary>
    public string? PublicBaseUrl { get; init; }
}
