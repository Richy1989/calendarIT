using calendarITCore.Extensions;
using CalendarIT.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace calendarITCore.Controllers;

/// <summary>Registration, login, refresh-token rotation, and logout endpoints.</summary>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")] // unauthenticated and password-hashing: the one place worth capping
public sealed class AuthController(IAuthService authService, IConfiguration configuration) : ControllerBase
{
    /// <summary>How many sessions a revoke call signed out.</summary>
    public sealed record RevokedSessions(int Count);

    /// <summary>Signs up: 200 with tokens for the first account on the server (it activates
    /// itself), else 202 — the account waits for activation and can't sign in yet.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType<AuthTokens>(StatusCodes.Status200OK)]
    [ProducesResponseType<RegistrationPending>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.RegisterAsync(request, Client(), cancellationToken);
        return ToResponse(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthTokens>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, Client(), cancellationToken);
        // activationRequired: the password was right, the account just isn't activated yet.
        return result.Succeeded
            ? Ok(result.Tokens)
            : Unauthorized(new { errors = result.Errors, activationRequired = result.Activation is not null });
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<AuthTokens>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.RefreshAsync(request, Client(), cancellationToken);
        return result.Succeeded ? Ok(result.Tokens) : Unauthorized(new { errors = result.Errors });
    }

    /// <summary>Ends the session the refresh token belongs to. Anonymous on purpose: holding the
    /// refresh token is the authority to end its session, and a client whose access token has
    /// already expired must still be able to sign out properly.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>The signed-in user's sessions (devices/browsers), the current one first.</summary>
    [HttpGet("sessions")]
    [Authorize]
    [DisableRateLimiting]
    [ProducesResponseType<IReadOnlyList<SessionDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<SessionDto>> Sessions(CancellationToken cancellationToken)
        => await authService.ListSessionsAsync(User.GetUserId(), User.GetSessionId(), cancellationToken);

    /// <summary>Signs one session out — its tokens stop working immediately.</summary>
    [HttpDelete("sessions/{id:guid}")]
    [Authorize]
    [DisableRateLimiting]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken cancellationToken)
        => await authService.RevokeSessionAsync(User.GetUserId(), id, cancellationToken) ? NoContent() : NotFound();

    /// <summary>Signs out every other session, keeping this one.</summary>
    [HttpPost("sessions/revoke-others")]
    [Authorize]
    [DisableRateLimiting]
    [ProducesResponseType<RevokedSessions>(StatusCodes.Status200OK)]
    public async Task<RevokedSessions> RevokeOtherSessions(CancellationToken cancellationToken)
        => new(await authService.RevokeSessionsAsync(User.GetUserId(), User.GetSessionId() ?? Guid.Empty, cancellationToken));

    /// <summary>Signs out everywhere, this session included.</summary>
    [HttpPost("sessions/revoke-all")]
    [Authorize]
    [DisableRateLimiting]
    [ProducesResponseType<RevokedSessions>(StatusCodes.Status200OK)]
    public async Task<RevokedSessions> RevokeAllSessions(CancellationToken cancellationToken)
        => new(await authService.RevokeSessionsAsync(User.GetUserId(), keepSessionId: null, cancellationToken));

    /// <summary>What the sign-in screen needs before anyone is signed in (is sign-up open?).</summary>
    [HttpGet("config")]
    [AllowAnonymous]
    [ProducesResponseType<AuthConfig>(StatusCodes.Status200OK)]
    public ActionResult<AuthConfig> Config() => Ok(authService.GetConfig());

    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.ChangePasswordAsync(User.GetUserId(), request, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(new { errors = result.Errors });
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.RequestPasswordResetAsync(request, PublicOrigin(), cancellationToken);
        // 202 regardless: whether that address has an account is not something this endpoint tells
        // anyone. The SPA shows the same "check your email" either way.
        return Accepted();
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.ResetPasswordAsync(request, cancellationToken);
        return result.Succeeded ? NoContent() : BadRequest(new { errors = result.Errors });
    }

    /// <summary>Confirms the email address from the link in the confirmation mail.</summary>
    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail(
        ConfirmEmailRequest request, [FromServices] IEmailConfirmations confirmations, CancellationToken cancellationToken)
        => await confirmations.ConfirmAsync(request, cancellationToken)
            ? NoContent()
            : BadRequest(new { errors = new[] { "This activation link is no longer valid. Ask for a new one on the sign-in screen." } });

    /// <summary>Mails a new activation link to an account waiting for activation. Anonymous (it
    /// can't sign in yet), so it answers 202 whatever the address — and sends nothing for an
    /// unknown or active one, or when one went out moments ago.</summary>
    [HttpPost("confirm-email/resend")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResendConfirmation(
        ResendActivationRequest request, [FromServices] IEmailConfirmations confirmations, CancellationToken cancellationToken)
    {
        if (!confirmations.Available)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "This server can't send activation emails. Ask its admin to activate your account.");
        }
        await confirmations.SendToAddressAsync(request.Email, cancellationToken);
        return Accepted();
    }

    /// <summary>
    /// The origin to build the reset link from: PUBLIC_BASE_URL, or null when it isn't set.
    ///
    /// Deliberately never derived from the request. Host is a header, and this endpoint is
    /// anonymous, so deriving the link from it let anyone mail a victim a genuine reset token
    /// pointing at a host of their choosing — a one-request account takeover. Nothing about
    /// running behind a proxy makes that header trustworthy: nginx forwards whatever arrived.
    /// </summary>
    private string? PublicOrigin()
    {
        var configured = configuration["PUBLIC_BASE_URL"];
        return string.IsNullOrWhiteSpace(configured) ? null : configured.TrimEnd('/');
    }

    /// <summary>The device the request comes from, recorded on its session for the user to see.</summary>
    private AuthClient Client() =>
        new(Request.Headers.UserAgent.ToString(), HttpContext.Connection.RemoteIpAddress?.ToString());

    private IActionResult ToResponse(AuthResult result) => result switch
    {
        { Succeeded: true, Tokens: { } tokens } => Ok(tokens),
        { Succeeded: true, Activation: { } activation } => Accepted(new RegistrationPending(activation)),
        _ => BadRequest(new { errors = result.Errors }),
    };
}
