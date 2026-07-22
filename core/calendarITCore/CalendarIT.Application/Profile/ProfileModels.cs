namespace CalendarIT.Application.Profile;

/// <summary>
/// The signed-in user's profile. Avatar is a ready-to-use data URL, or null.
/// <paramref name="DefaultView"/> is the remembered calendar view (or null).
/// </summary>
public sealed record ProfileDto(string? Email, string? AvatarDataUrl, string? DefaultView);

/// <summary>Payload for remembering the user's chosen calendar view.</summary>
public sealed record UpdateViewRequest(string View);
