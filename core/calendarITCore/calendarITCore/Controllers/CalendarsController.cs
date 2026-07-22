using calendarITCore.Extensions;
using CalendarIT.Application.Calendars;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>CRUD for the signed-in user's calendars (the containers events live in).</summary>
[ApiController]
[Route("api/calendars")]
[Authorize]
public sealed class CalendarsController(ICalendarService calendars) : ControllerBase
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

    [HttpPut("{id:guid}")]
    [ProducesResponseType<CalendarDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rename(Guid id, SaveCalendarRequest request, CancellationToken cancellationToken)
    {
        var updated = await calendars.RenameAsync(User.GetUserId(), id, request, cancellationToken);
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
