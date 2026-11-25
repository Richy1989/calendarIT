using calendarITCore.Extensions;
using CalendarIT.Application.Calendars;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>The signed-in user's tasks (to-dos), which sync to phones as VTODOs.</summary>
[ApiController]
[Route("api/tasks")]
[Authorize]
public sealed class TasksController(ITaskService tasks) : ControllerBase
{
    /// <summary>Open tasks; with <paramref name="includeDone"/>, done and cancelled ones too.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<TaskDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<TaskDto>> List([FromQuery] bool includeDone, CancellationToken cancellationToken)
        => await tasks.ListAsync(User.GetUserId(), includeDone, cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<TaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var task = await tasks.GetAsync(User.GetUserId(), id, cancellationToken);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpPost]
    [ProducesResponseType<TaskDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(SaveTaskRequest request, CancellationToken cancellationToken)
    {
        var created = await tasks.CreateAsync(User.GetUserId(), request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<TaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, SaveTaskRequest request, CancellationToken cancellationToken)
    {
        var updated = await tasks.UpdateAsync(User.GetUserId(), id, request, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Ticks the task off — with what is left of its subtasks — or opens it again.</summary>
    [HttpPut("{id:guid}/done")]
    [ProducesResponseType<TaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDone(Guid id, SetTaskDoneRequest request, CancellationToken cancellationToken)
    {
        var updated = await tasks.SetDoneAsync(User.GetUserId(), id, request.Done, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Deletes the task and its subtasks.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => await tasks.DeleteAsync(User.GetUserId(), id, cancellationToken) ? NoContent() : NotFound();
}
