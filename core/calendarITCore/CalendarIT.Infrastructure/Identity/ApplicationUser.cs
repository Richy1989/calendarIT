using Microsoft.AspNetCore.Identity;

namespace CalendarIT.Infrastructure.Identity;

/// <summary>
/// The application's user account. Uses <see cref="Guid"/> keys so ids are opaque and
/// safe to expose. Calendar-specific profile fields (e.g. default time zone) are added
/// in later phases.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Raw bytes of the user's profile picture (null = none).</summary>
    public byte[]? AvatarData { get; set; }

    /// <summary>MIME type of <see cref="AvatarData"/> (e.g. image/png).</summary>
    public string? AvatarContentType { get; set; }

    /// <summary>
    /// The calendar view the user last chose (FullCalendar id, e.g. "timeGridWeek"), restored
    /// on next login. Null = never set; the client falls back to the month default.
    /// </summary>
    public string? DefaultCalendarView { get; set; }

    /// <summary>
    /// Whether times are shown in 24-hour ("13:00") rather than 12-hour ("1:00 PM") format.
    /// Null = never set; the client falls back to the browser locale's convention.
    /// </summary>
    public bool? Use24HourClock { get; set; }
}
