using calendarITCore.Extensions;
using CalendarIT.Application.Profile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calendarITCore.Controllers;

/// <summary>The signed-in user's profile: read it, and upload / remove the avatar.</summary>
[ApiController]
[Route("api/profile")]
[Authorize]
public sealed class ProfileController(IProfileService profile) : ControllerBase
{
    private const int MaxAvatarBytes = 3 * 1024 * 1024; // 3 MB
    private static readonly HashSet<string> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp", "image/gif" };

    [HttpGet]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var dto = await profile.GetAsync(User.GetUserId(), cancellationToken);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost("avatar")]
    [RequestSizeLimit(MaxAvatarBytes)]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> UploadAvatar(CancellationToken cancellationToken)
    {
        var contentType = Request.ContentType?.Split(';')[0].Trim() ?? string.Empty;
        if (!AllowedTypes.Contains(contentType))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                new { error = "Use a PNG, JPEG, WebP, or GIF image." });
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            return BadRequest(new { error = "Empty image." });
        }

        await profile.SetAvatarAsync(User.GetUserId(), bytes, contentType, cancellationToken);
        return Ok(await profile.GetAsync(User.GetUserId(), cancellationToken));
    }

    [HttpDelete("avatar")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveAvatar(CancellationToken cancellationToken)
    {
        await profile.ClearAvatarAsync(User.GetUserId(), cancellationToken);
        return NoContent();
    }
}
