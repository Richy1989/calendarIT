using calendarITCore.Extensions;
using CalendarIT.Application.Calendars;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>CRUD for the signed-in user's calendars (the containers events live in).</summary>
[ApiController]
[Route("api/calendars")]
[Authorize]
public sealed class CalendarsController(ICalendarService calendars, ICalendarSubscriptionService subscriptions) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CalendarDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<CalendarDto>> List(CancellationToken cancellationToken)
        => await calendars.ListAsync(User.GetUserId(), cancellationToken);

    [HttpPost]
    [ProducesResponseType<CalendarDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveCalendarRequest request, CancellationToken cancellationToken)
    {
        var created = await calendars.CreateAsync(User.GetUserId(), request, cancellationToken);
        return CreatedAtAction(nameof(List), created);
    }

    /// <summary>
    /// Subscribes to an .ics feed (https, http or webcal, on the public internet): a new
    /// read-only calendar, refreshed in the background. The feed is fetched first — a 400 says
    /// why when it can't be.
    /// </summary>
    [HttpPost("subscriptions")]
    [ProducesResponseType<CalendarDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Subscribe(SubscribeCalendarRequest request, CancellationToken cancellationToken)
    {
        var created = await subscriptions.SubscribeAsync(User.GetUserId(), request, cancellationToken);
        return CreatedAtAction(nameof(List), created);
    }

    /// <summary>Fetches a subscribed calendar now. How it went is in <c>subscription.lastError</c>.</summary>
    [HttpPost("{id:guid}/refresh")]
    [ProducesResponseType<CalendarDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken cancellationToken)
    {
        var refreshed = await subscriptions.RefreshAsync(User.GetUserId(), id, cancellationToken);
        return refreshed is null ? NotFound() : Ok(refreshed);
    }

    /// <summary>How often a subscribed calendar is refreshed: 60, 360, 720 or 1440 minutes.</summary>
    [HttpPut("{id:guid}/refresh-interval")]
    [ProducesResponseType<CalendarDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetRefreshInterval(Guid id, SetRefreshRequest request, CancellationToken cancellationToken)
    {
        var updated = await subscriptions.SetRefreshMinutesAsync(User.GetUserId(), id, request.RefreshMinutes, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<CalendarDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rename(Guid id, SaveCalendarRequest request, CancellationToken cancellationToken)
    {
        var updated = await calendars.RenameAsync(User.GetUserId(), id, request, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Sets the category events in this calendar take when they have none of their own
    /// (null clears it).</summary>
    [HttpPut("{id:guid}/default-category")]
    [ProducesResponseType<CalendarDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultCategory(Guid id, SetCalendarCategoryRequest request, CancellationToken cancellationToken)
    {
        var updated = await calendars.SetDefaultCategoryAsync(User.GetUserId(), id, request.CategoryId, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Sets the reminders a new timed event in this calendar starts with (empty clears them).</summary>
    [HttpPut("{id:guid}/default-reminders")]
    [ProducesResponseType<CalendarDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultReminders(Guid id, SetCalendarRemindersRequest request, CancellationToken cancellationToken)
    {
        var updated = await calendars.SetDefaultRemindersAsync(User.GetUserId(), id, request.Reminders, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        return await calendars.DeleteAsync(User.GetUserId(), id, cancellationToken) switch
        {
            DeleteCalendarResult.Deleted => NoContent(),
            DeleteCalendarResult.LastCalendar => Conflict(new { error = "You can't delete your last calendar." }),
            _ => NotFound(),
        };
    }
}
