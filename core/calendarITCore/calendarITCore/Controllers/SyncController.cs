using calendarITCore.Extensions;
using CalendarIT.Application.Sync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>
/// The native app's delta sync (protocol 1): pull what changed since a cursor, push changes made
/// on the device. See <see cref="SyncPullResponse"/> and <see cref="SyncOp"/> for the rules.
/// </summary>
[ApiController]
[Route("api/sync")]
[Authorize]
public sealed class SyncController(ISyncService sync) : ControllerBase
{
    /// <summary>The next page of changes. Leave <paramref name="cursor"/> out for everything; keep
    /// asking with the returned cursor while <c>hasMore</c> is true.</summary>
    [HttpGet]
    [ProducesResponseType<SyncPullResponse>(StatusCodes.Status200OK)]
    public async Task<SyncPullResponse> Pull([FromQuery] string? cursor, [FromQuery] int limit = 500, CancellationToken cancellationToken = default)
        => await sync.PullAsync(User.GetUserId(), string.IsNullOrWhiteSpace(cursor) ? null : cursor, limit, cancellationToken);

    /// <summary>Applies changes in order; each one's result says whether it was taken. Pull afterwards
    /// for the state they led to.</summary>
    [HttpPost]
    [ProducesResponseType<SyncPushResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<SyncPushResponse> Push(SyncPushRequest request, CancellationToken cancellationToken)
        => await sync.PushAsync(User.GetUserId(), request, cancellationToken);
}
