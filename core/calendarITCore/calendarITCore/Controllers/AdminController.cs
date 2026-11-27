using calendarITCore.Extensions;
using CalendarIT.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>The server admin's user management (Settings → Users).</summary>
[ApiController]
[Route("api/admin/users")]
[Authorize]
public sealed class AdminController(IAccountAdmin admin) : ControllerBase
{
    /// <summary>A password-reset link the admin hands over.</summary>
    public sealed record ResetLink(string Link);

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UserAccountDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<UserAccountDto>>> List(CancellationToken cancellationToken)
        => await admin.ListUsersAsync(User.GetUserId(), cancellationToken) is { } list ? Ok(list) : Forbid();

    [HttpPost("{id:guid}/activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
        => ToResponse(await admin.ActivateAsync(User.GetUserId(), id, cancellationToken));

    [HttpPost("{id:guid}/disable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken cancellationToken)
        => ToResponse(await admin.SetDisabledAsync(User.GetUserId(), id, disabled: true, cancellationToken));

    [HttpPost("{id:guid}/enable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(Guid id, CancellationToken cancellationToken)
        => ToResponse(await admin.SetDisabledAsync(User.GetUserId(), id, disabled: false, cancellationToken));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => ToResponse(await admin.DeleteAsync(User.GetUserId(), id, cancellationToken));

    [HttpPut("{id:guid}/admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MakeAdmin(Guid id, CancellationToken cancellationToken)
        => ToResponse(await admin.SetAdminAsync(User.GetUserId(), id, admin: true, cancellationToken));

    [HttpDelete("{id:guid}/admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveAdmin(Guid id, CancellationToken cancellationToken)
        => ToResponse(await admin.SetAdminAsync(User.GetUserId(), id, admin: false, cancellationToken));

    [HttpPost("{id:guid}/reset-link")]
    [ProducesResponseType<ResetLink>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateResetLink(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await admin.CreateResetLinkAsync(User.GetUserId(), id, cancellationToken);
        return outcome is { Result: AccountAdminResult.Done, Link: { } link } ? Ok(new ResetLink(link)) : ToResponse(outcome);
    }

    private IActionResult ToResponse(AccountAdminOutcome outcome) => outcome.Result switch
    {
        AccountAdminResult.Done => NoContent(),
        AccountAdminResult.Forbidden => Forbid(),
        AccountAdminResult.Refused => Problem(statusCode: StatusCodes.Status409Conflict, detail: outcome.Message),
        _ => NotFound(),
    };
}
