using calendarITCore.Extensions;
using CalendarIT.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>
/// Reminder occurrences that have just come due for the signed-in user, for browsers using the
/// local-notification fallback (no push service). Read-only; the reminder job still owns delivery
/// for push- and email-channel reminders.
/// </summary>
[ApiController]
[Route("api/reminders")]
[Authorize]
public sealed class RemindersController(IDueReminderQuery query, IReminderSnoozes snoozes) : ControllerBase
{
    /// <summary>
    /// Browser-channel reminders whose trigger fell in (sinceUtc, now]. sinceUtc is clamped
    /// server-side to no earlier than one hour ago; omit it to default to one hour ago.
    /// </summary>
    [HttpGet("due")]
    [ProducesResponseType<DueRemindersResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DueRemindersResponse>> Due(
        [FromQuery] DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
    {
        var since = sinceUtc?.UtcDateTime ?? DateTime.MinValue; // service clamps to now - 1h
        var items = await query.GetDueAsync(User.GetUserId(), since, cancellationToken);
        return Ok(new DueRemindersResponse(items));
    }

    /// <summary>
    /// Snoozes the reminder a notification shows, for 5, 10, 15, 30 or 60 minutes. Called by the
    /// notification's Snooze button from the service worker, which has no sign-in: the token
    /// the notification carries is the authorization, and it covers that reminder only.
    /// </summary>
    [HttpPost("snooze")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Snooze(SnoozeReminderRequest request, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(request.Token) || request.Token.Length > 2048
            ? NotFound()
            : await snoozes.SnoozeAsync(request.Token, request.Minutes, cancellationToken) ? NoContent() : NotFound();
}
