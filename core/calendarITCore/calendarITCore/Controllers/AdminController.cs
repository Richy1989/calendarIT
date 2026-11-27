using calendarITCore.Extensions;
using CalendarIT.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>The server admin's account management: activating (or removing) new sign-ups.</summary>
[ApiController]
[Route("api/admin")]
[Authorize]
public sealed class AdminController(IAccountAdmin admin) : ControllerBase
{
    [HttpGet("pending-accounts")]
    [ProducesResponseType<IReadOnlyList<PendingAccountDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PendingAccountDto>>> Pending(CancellationToken cancellationToken)
        => await admin.ListPendingAsync(User.GetUserId(), cancellationToken) is { } list ? Ok(list) : Forbid();

    [HttpPost("pending-accounts/{id:guid}/activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
        => ToResponse(await admin.ActivateAsync(User.GetUserId(), id, cancellationToken));

    [HttpDelete("pending-accounts/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => ToResponse(await admin.DeleteAsync(User.GetUserId(), id, cancellationToken));

    private IActionResult ToResponse(AccountAdminResult result) => result switch
    {
        AccountAdminResult.Done => NoContent(),
        AccountAdminResult.Forbidden => Forbid(),
        _ => NotFound(),
    };
}
