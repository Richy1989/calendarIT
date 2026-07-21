using System.Text;
using calendarITCore.Extensions;
using CalendarIT.Application.Calendars;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>CRUD for the signed-in user's events, plus iCal import/export.</summary>
[ApiController]
[Route("api/events")]
[Authorize]
public sealed class EventsController(IEventService events, ICalendarIoService calendarIo) : ControllerBase
{
    [HttpGet("export.ics")]
    [Produces("text/calendar")]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var ics = await calendarIo.ExportAsync(User.GetUserId(), cancellationToken);
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar", "calendarit.ics");
    }

    [HttpPost("import")]
    [ProducesResponseType<ImportResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Import(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var ics = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(ics))
        {
            return BadRequest(new { error = "Empty request body." });
        }
        var result = await calendarIo.ImportAsync(User.GetUserId(), ics, cancellationToken);
        return Ok(result);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EventDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<EventDto>> List(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
        => await events.GetEventsAsync(User.GetUserId(), from, to, cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<EventDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var found = await events.GetByIdAsync(User.GetUserId(), id, cancellationToken);
        return found is null ? NotFound() : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<EventDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveEventRequest request, CancellationToken cancellationToken)
    {
        var created = await events.CreateAsync(User.GetUserId(), request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
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
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] DateTimeOffset? occurrence,
        CancellationToken cancellationToken)
    {
        var deleted = await events.DeleteAsync(User.GetUserId(), id, occurrence, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }
}
