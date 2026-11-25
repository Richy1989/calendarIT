namespace CalendarIT.Domain;

/// <summary>
/// A device running the native app, registered by the app itself under an id it chose. It names
/// where to nudge the device when the user's data changed (a content-free "sync now" push), and
/// lets the user see and remove their devices. Kept apart from browser
/// <see cref="PushSubscription"/>s: those receive reminders, an app device alarms by itself.
/// </summary>
public class Device
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The name the user knows it by ("Pixel 9").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"android" for now.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>The app's version, for support.</summary>
    public string? AppVersion { get; set; }

    /// <summary>How the device is reached: "unifiedpush" or "fcm"; null = it polls.</summary>
    public string? PushKind { get; set; }

    /// <summary>UnifiedPush endpoint (a Web Push URL) or FCM registration token.</summary>
    public string? PushEndpoint { get; set; }

    /// <summary>Web Push keys for a UnifiedPush endpoint (RFC 8291), base64url.</summary>
    public string? PushP256dh { get; set; }

    public string? PushAuth { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When it last registered or synced.</summary>
    public DateTime LastSeenAtUtc { get; set; }
}
