namespace CalendarIT.Application.Profile;

/// <summary>The signed-in user's profile. Avatar is a ready-to-use data URL, or null.</summary>
public sealed record ProfileDto(string? Email, string? AvatarDataUrl);
