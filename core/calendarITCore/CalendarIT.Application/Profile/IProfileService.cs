namespace CalendarIT.Application.Profile;

/// <summary>Reads and updates the signed-in user's profile (currently: avatar).</summary>
public interface IProfileService
{
    Task<ProfileDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SetAvatarAsync(Guid userId, byte[] data, string contentType, CancellationToken cancellationToken = default);

    Task ClearAvatarAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers the user's chosen calendar view. Returns false if <paramref name="view"/>
    /// isn't a recognized view id (nothing is written).
    /// </summary>
    Task<bool> SetDefaultViewAsync(Guid userId, string view, CancellationToken cancellationToken = default);

    /// <summary>Remembers the user's time-format preference (true = 24-hour, false = 12-hour).</summary>
    Task SetClockFormatAsync(Guid userId, bool use24Hour, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers the day calendar grids start on. <paramref name="weekStart"/> is "sunday",
    /// "monday" (either casing), or null to go back to following the browser locale. Returns
    /// false — writing nothing — for any other value, so a bad request can't quietly clear a
    /// choice the user already made.
    /// </summary>
    Task<bool> SetWeekStartAsync(Guid userId, string? weekStart, CancellationToken cancellationToken = default);
}
