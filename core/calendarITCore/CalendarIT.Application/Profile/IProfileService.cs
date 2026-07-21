namespace CalendarIT.Application.Profile;

/// <summary>Reads and updates the signed-in user's profile (currently: avatar).</summary>
public interface IProfileService
{
    Task<ProfileDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SetAvatarAsync(Guid userId, byte[] data, string contentType, CancellationToken cancellationToken = default);

    Task ClearAvatarAsync(Guid userId, CancellationToken cancellationToken = default);
}
