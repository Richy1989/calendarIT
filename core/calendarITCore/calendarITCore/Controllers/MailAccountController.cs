using calendarITCore.Extensions;
using CalendarIT.Application.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
    [EnableRateLimiting("mail-test")] // each call opens real connections to a host the user named
    [ProducesResponseType<MailTestResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
        => Ok(await mailAccounts.TestAsync(User.GetUserId(), cancellationToken));

    /// <summary>Recent outgoing mail (invitations, reminders, replies) and how sending went.</summary>
    [HttpGet("outbox")]
    [ProducesResponseType<IReadOnlyList<OutboxItemDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<OutboxItemDto>> Outbox(CancellationToken cancellationToken)
        => await mailAccounts.ListOutboxAsync(User.GetUserId(), cancellationToken);

    [HttpPost("outbox/{id:guid}/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryOutbox(Guid id, CancellationToken cancellationToken)
        => await mailAccounts.RetryOutboxAsync(User.GetUserId(), id, cancellationToken) ? NoContent() : NotFound();

    [HttpDelete("outbox/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DiscardOutbox(Guid id, CancellationToken cancellationToken)
        => await mailAccounts.DiscardOutboxAsync(User.GetUserId(), id, cancellationToken) ? NoContent() : NotFound();
}
