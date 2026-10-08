using System.ComponentModel.DataAnnotations;
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
    public async Task<IActionResult> Export(
        [FromQuery] string? calendars,
        CancellationToken cancellationToken)
    {
        // `calendars` is a comma-separated list of calendar ids; empty/absent = export everything.
        var calendarIds = (calendars ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
        var ics = await calendarIo.ExportAsync(
            User.GetUserId(), calendarIds.Count > 0 ? calendarIds : null, cancellationToken);
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar", "calendarit.ics");
    }

    [HttpGet("{id:guid}/export.ics")]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportEvent(Guid id, CancellationToken cancellationToken)
    {
        var ics = await calendarIo.ExportEventAsync(User.GetUserId(), id, cancellationToken);
        return ics is null
            ? NotFound()
            : File(Encoding.UTF8.GetBytes(ics), "text/calendar", "event.ics");
    }

    /// <summary>Largest .ics file an import accepts — years of a busy calendar fit well inside.</summary>
    private const int MaxImportBytes = 10 * 1024 * 1024;

    /// <summary>Widest window one range query may ask for. The grid asks for at most six weeks
    /// and the list view for three months at a time; the cap is what keeps a hand-made request
    /// from having every series expanded across centuries.</summary>
    private static readonly TimeSpan MaxListWindow = TimeSpan.FromDays(400);

    [HttpPost("import")]
    [RequestSizeLimit(MaxImportBytes)]
    [ProducesResponseType<ImportResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Import(
        [FromQuery] Guid? calendarId,
        [FromQuery, MaxLength(200)] string? newCalendarName,
        [FromQuery] Guid? newCalendarCategoryId,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var ics = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(ics))
        {
            return BadRequest(new { error = "Empty request body." });
        }
        var result = await calendarIo.ImportAsync(
            User.GetUserId(), ics, calendarId, newCalendarName, cancellationToken, newCalendarCategoryId);
        return Ok(result);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EventDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery, Required] DateTimeOffset? from,
        [FromQuery, Required] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        // A range is required: without one, recurring series can't be expanded at all (they used
        // to be silently left out), and an unbounded one has every series expanded without end.
        if (from is null || to is null || to <= from || to - from > MaxListWindow)
        {
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["range"] = [$"Pass both 'from' and 'to', with 'to' after 'from' and no more than {MaxListWindow.TotalDays:0} days apart."],
            }));
        }
        return Ok(await events.GetEventsAsync(User.GetUserId(), from, to, cancellationToken));
    }

    [HttpGet("search")]
    [ProducesResponseType<IReadOnlyList<EventSearchResult>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<EventSearchResult>> Search(
        [FromQuery] string? q,
        [FromQuery] int limit = 8,
        CancellationToken cancellationToken = default)
        => await events.SearchAsync(User.GetUserId(), q ?? string.Empty, limit, cancellationToken);

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

    /// <summary>
    /// Edits one occurrence of a series — the one whose original start is <paramref name="occurrence"/> —
    /// leaving the rest of the series as it is.
    /// </summary>
    [HttpPut("{id:guid}/occurrence")]
    [ProducesResponseType<EventDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateOccurrence(
        Guid id, [FromQuery, Required] DateTimeOffset occurrence, SaveEventRequest request, CancellationToken cancellationToken)
    {
        var updated = await events.UpsertOccurrenceAsync(User.GetUserId(), id, occurrence, request, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Puts one occurrence back to what the series says — undoing an edit or a delete.</summary>
    [HttpPost("{id:guid}/occurrence/reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetOccurrence(
        Guid id, [FromQuery, Required] DateTimeOffset occurrence, CancellationToken cancellationToken)
        => await events.ResetOccurrenceAsync(User.GetUserId(), id, occurrence, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/rsvp")]
    [ProducesResponseType<EventDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rsvp(Guid id, RsvpRequest request, CancellationToken cancellationToken)
    {
        var updated = await events.RespondToInvitationAsync(User.GetUserId(), id, request.Status, cancellationToken);
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
