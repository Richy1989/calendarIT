using calendarITCore.Extensions;
using CalendarIT.Application.Sync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>The devices the signed-in user runs the app on.</summary>
[ApiController]
[Route("api/devices")]
[Authorize]
public sealed class DevicesController(IDeviceService devices) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DeviceDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<DeviceDto>> List(CancellationToken cancellationToken)
        => await devices.ListAsync(User.GetUserId(), cancellationToken);

    /// <summary>Registers the device under the id the app chose, or updates it.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<DeviceDto> Register(Guid id, RegisterDeviceRequest request, CancellationToken cancellationToken)
        => await devices.RegisterAsync(User.GetUserId(), id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
        => await devices.RemoveAsync(User.GetUserId(), id, cancellationToken) ? NoContent() : NotFound();
}
