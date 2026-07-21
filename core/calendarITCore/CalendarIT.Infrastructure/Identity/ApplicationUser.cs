using Microsoft.AspNetCore.Identity;

namespace CalendarIT.Infrastructure.Identity;

/// <summary>
/// The application's user account. Uses <see cref="Guid"/> keys so ids are opaque and
/// safe to expose. Calendar-specific profile fields (e.g. default time zone) are added
/// in later phases.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
}
