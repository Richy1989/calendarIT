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
}
