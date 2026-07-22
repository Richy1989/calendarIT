using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using CalendarIT.Infrastructure.Identity;
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
    UserManager<ApplicationUser> userManager)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CalDavBasic";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

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

        var user = await userManager.FindByEmailAsync(email) ?? await userManager.FindByNameAsync(email);
        if (user is null || !await userManager.CheckPasswordAsync(user, password))
        {
            return AuthenticateResult.Fail("Invalid credentials.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.UserName ?? email)],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // The realm prompt is what CalDAV clients show in their login dialog.
        Response.Headers.WWWAuthenticate = "Basic realm=\"CalendarIT\", charset=\"UTF-8\"";
        return base.HandleChallengeAsync(properties);
    }
}
