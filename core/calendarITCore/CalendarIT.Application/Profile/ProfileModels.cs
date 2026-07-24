namespace CalendarIT.Application.Profile;

/// <summary>
/// The signed-in user's profile. Avatar is a ready-to-use data URL, or null.
/// <paramref name="DefaultView"/> is the remembered calendar view (or null).
/// <paramref name="Use24HourClock"/> is the time-format preference (null = use locale default).
/// </summary>
public sealed record ProfileDto(string? Email, string? AvatarDataUrl, string? DefaultView, bool? Use24HourClock);

/// <summary>Payload for remembering the user's chosen calendar view.</summary>
public sealed record UpdateViewRequest(string View);

/// <summary>Payload for the time-format preference: true = 24-hour, false = 12-hour.</summary>
public sealed record UpdateClockRequest(bool Use24Hour);
