using calendarITCore.Extensions;
using CalendarIT.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>
/// Browser Web Push subscriptions for the signed-in user. The client fetches the VAPID public
/// key, subscribes its service worker, and registers the resulting subscription here; the
/// reminder job then delivers browser notifications to it. One subscription per browser.
/// </summary>
[ApiController]
[Route("api/push")]
[Authorize]
public sealed class PushController(IPushSubscriptionService subscriptions) : ControllerBase
{
    /// <summary>The VAPID application-server public key the browser needs to subscribe.</summary>
    [HttpGet("public-key")]
    [ProducesResponseType<VapidPublicKeyDto>(StatusCodes.Status200OK)]
    public ActionResult<VapidPublicKeyDto> PublicKey()
        => Ok(new VapidPublicKeyDto(subscriptions.VapidPublicKey));

    /// <summary>Register (or refresh) this browser's push subscription for the current user.</summary>
    [HttpPost("subscribe")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Subscribe(PushSubscriptionInput input, CancellationToken cancellationToken)
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        await subscriptions.SubscribeAsync(User.GetUserId(), input, userAgent, cancellationToken);
        return NoContent();
    }

    /// <summary>Forget this browser's subscription (e.g. the user turned notifications off).</summary>
    [HttpPost("unsubscribe")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unsubscribe(PushUnsubscribeInput input, CancellationToken cancellationToken)
        => await subscriptions.UnsubscribeAsync(User.GetUserId(), input.Endpoint, cancellationToken)
            ? NoContent()
            : NotFound();
}
