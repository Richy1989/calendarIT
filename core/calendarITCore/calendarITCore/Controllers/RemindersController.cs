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
public sealed class RemindersController(IDueReminderQuery query) : ControllerBase
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
}
