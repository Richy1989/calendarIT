using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using CalendarIT.Infrastructure.Auth;
using CalendarIT.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalendarIT.CalDav;

/// <summary>
/// HTTP Basic authentication for the CalDAV endpoints, validated against the same
/// Identity user store as the web login. CalDAV clients (DAVx⁵, Thunderbird, iOS)
/// speak Basic, not JWT — the operator's reverse proxy must terminate TLS so the
/// credentials never travel in the clear.
/// </summary>
public sealed class CalDavBasicAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    UserManager<ApplicationUser> userManager,
    CalDavCredentialCache credentials,
    CalDavFailureThrottle throttle)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CalDavBasic";

    private const string ThrottledKey = "caldav-throttled";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var address = Context.Connection.RemoteIpAddress?.ToString();
        var result = await AuthenticateBasicAsync(header);
        if (!result.Succeeded && result.Failure is not null)
        {
            throttle.RecordFailure(address);
        }
        return result;
    }

    private async Task<AuthenticateResult> AuthenticateBasicAsync(string header)
    {

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        var sep = decoded.IndexOf(':');
        if (sep < 0)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        var email = decoded[..sep];
        var password = decoded[(sep + 1)..];

        // A client polls constantly with the same credential; re-deriving the password hash each
        // time is the single most expensive thing this endpoint does. A recent success short-
        // circuits the whole check — including the user lookup.
        if (credentials.TryGet(email, password, out var cached))
        {
            return Success(cached!.UserId, cached.UserName);
        }

        // Past the cache, every attempt costs a password hash: an address that has failed too
        // often is turned away before that work is done.
        if (throttle.IsBlocked(Context.Connection.RemoteIpAddress?.ToString()))
        {
            Context.Items[ThrottledKey] = true;
            return AuthenticateResult.Fail("Too many failed logins.");
        }

        var user = await userManager.FindByEmailAsync(email) ?? await userManager.FindByNameAsync(email);
        if (user is null)
        {
            PasswordTiming.Equalize(userManager.PasswordHasher, password); // no faster than a real account
            return AuthenticateResult.Fail("Invalid credentials.");
        }

        // Same lockout counter as the web login — /dav is reachable from the internet by design
        // (that is the point of phone sync), so it can't be the one unthrottled way in. The
        // threshold is deliberately generous: a client left holding an old password after a
        // change will retry on its own schedule, and shouldn't lock the owner out of the web UI
        // for long.
        if (await userManager.IsLockedOutAsync(user))
        {
            PasswordTiming.Equalize(userManager.PasswordHasher, password);
            return AuthenticateResult.Fail("Invalid credentials.");
        }

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            await userManager.AccessFailedAsync(user);
            return AuthenticateResult.Fail("Invalid credentials.");
        }

        if (await userManager.GetAccessFailedCountAsync(user) > 0)
        {
            await userManager.ResetAccessFailedCountAsync(user);
        }

        var userName = user.UserName ?? email;
        credentials.Store(email, password, new CalDavCredentialCache.CachedPrincipal(user.Id, userName));
        return Success(user.Id, userName);
    }

    private AuthenticateResult Success(Guid userId, string userName)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, userName)],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(ThrottledKey))
        {
            // Not a credentials prompt: the client should back off, not ask again.
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = ((int)CalDavFailureThrottle.Window.TotalSeconds).ToString();
            return Task.CompletedTask;
        }
        // The realm prompt is what CalDAV clients show in their login dialog.
        Response.Headers.WWWAuthenticate = "Basic realm=\"CalendarIT\", charset=\"UTF-8\"";
        return base.HandleChallengeAsync(properties);
    }
}
