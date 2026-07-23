using calendarITCore.Extensions;
using CalendarIT.Application.Calendars;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>CRUD for the signed-in user's categories (named colors events take theirs from).</summary>
[ApiController]
[Route("api/categories")]
[Authorize]
public sealed class CategoriesController(ICategoryService categories) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<CategoryDto>> List(CancellationToken cancellationToken)
        => await categories.ListAsync(User.GetUserId(), cancellationToken);

    [HttpPost]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var outcome = await categories.CreateAsync(User.GetUserId(), request, cancellationToken);
        return outcome.Status == CategorySaveStatus.Saved
            ? CreatedAtAction(nameof(List), outcome.Category)
            : Conflict(new { error = "A category with that name already exists." });
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var outcome = await categories.UpdateAsync(User.GetUserId(), id, request, cancellationToken);
        return outcome.Status switch
        {
            CategorySaveStatus.Saved => Ok(outcome.Category),
            CategorySaveStatus.DuplicateName => Conflict(new { error = "A category with that name already exists." }),
            _ => NotFound(),
        };
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => await categories.DeleteAsync(User.GetUserId(), id, cancellationToken) ? NoContent() : NotFound();
}
