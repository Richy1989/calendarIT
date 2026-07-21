using calendarITCore.Extensions;
using CalendarIT.Application.Calendars;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>CRUD for the signed-in user's events.</summary>
[ApiController]
[Route("api/events")]
[Authorize]
public sealed class EventsController(IEventService events) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EventDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<EventDto>> List(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
        => await events.GetEventsAsync(User.GetUserId(), from, to, cancellationToken);

    [HttpPost]
    [ProducesResponseType<EventDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveEventRequest request, CancellationToken cancellationToken)
    {
        var created = await events.CreateAsync(User.GetUserId(), request, cancellationToken);
        return CreatedAtAction(nameof(List), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<EventDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, SaveEventRequest request, CancellationToken cancellationToken)
    {
        var updated = await events.UpdateAsync(User.GetUserId(), id, request, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await events.DeleteAsync(User.GetUserId(), id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }
}
