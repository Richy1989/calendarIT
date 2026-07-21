using System.Security.Claims;

namespace calendarITCore.Extensions;

public static class ClaimsPrincipalExtensions
{
    /// <summary>Reads the authenticated user's id from the JWT (sub → NameIdentifier).</summary>
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(id, out var guid)
            ? guid
            : throw new InvalidOperationException("Authenticated user has no valid id claim.");
    }
}
