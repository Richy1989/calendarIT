using calendarITCore.Extensions;
using CalendarIT.Application.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>
/// The signed-in user's personal email account — the identity used to send appointment
/// invitations (and, later, to receive them). The password is write-only: it is stored
/// encrypted and never returned.
/// </summary>
[ApiController]
[Route("api/mail-account")]
[Authorize]
public sealed class MailAccountController(IMailAccountService mailAccounts) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<MailAccountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var account = await mailAccounts.GetAsync(User.GetUserId(), cancellationToken);
        return account is null ? NotFound() : Ok(account);
    }

    [HttpPut]
    [ProducesResponseType<MailAccountDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(SaveMailAccountRequest request, CancellationToken cancellationToken)
        => Ok(await mailAccounts.SaveAsync(User.GetUserId(), request, cancellationToken));

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken)
        => await mailAccounts.DeleteAsync(User.GetUserId(), cancellationToken) ? NoContent() : NotFound();

    [HttpPost("test")]
    [ProducesResponseType<MailTestResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
        => Ok(await mailAccounts.TestAsync(User.GetUserId(), cancellationToken));
}
