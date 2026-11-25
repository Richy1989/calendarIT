using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Sync;

/// <summary>A device running the app, as the user sees it in their settings.</summary>
public sealed record DeviceDto(
    Guid Id, string Name, string Platform, string? AppVersion, string? PushKind,
    DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);

/// <summary>Registers the app on a device, or updates it (the app does this at every start).</summary>
public sealed class RegisterDeviceRequest
{
    [Required, MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    /// <summary>"android".</summary>
    [Required, MaxLength(16)]
    public string Platform { get; init; } = string.Empty;

    [MaxLength(32)]
    public string? AppVersion { get; init; }

    /// <summary>How to nudge it when something changed; null = it polls.</summary>
    public DevicePushInput? Push { get; init; }
}

/// <summary>Where a device takes its "sync now" pushes.</summary>
public sealed class DevicePushInput
{
    /// <summary>"unifiedpush" (Web Push to <see cref="Endpoint"/>, with the keys) or "fcm" (an FCM token).</summary>
    [Required, MaxLength(16)]
    public string Kind { get; init; } = string.Empty;

    [Required, MaxLength(2048)]
    public string Endpoint { get; init; } = string.Empty;

    [MaxLength(255)]
    public string? P256dh { get; init; }

    [MaxLength(255)]
    public string? Auth { get; init; }
}

public interface IDeviceService
{
    Task<IReadOnlyList<DeviceDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates the device. Throws <see cref="InvalidInputException"/> for push
    /// details that won't do, an id another account uses, or too many devices.</summary>
    Task<DeviceDto> RegisterAsync(Guid userId, Guid deviceId, RegisterDeviceRequest request, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken = default);
}
