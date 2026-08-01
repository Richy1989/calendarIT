namespace CalendarIT.Domain;

/// <summary>
/// A browser Web Push subscription for one user on one device/browser. The push service
/// endpoint plus its ECDH public key (<see cref="P256dh"/>) and auth secret are everything
/// needed to send that browser an encrypted push message. One user can have many (one per
/// browser they've enabled notifications on). Rows are self-healing: the dispatcher deletes
/// a subscription the push service reports as gone (404/410).
/// </summary>
public class PushSubscription
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The push service URL to POST encrypted payloads to. Unique per subscription.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The client's P-256 ECDH public key (base64url), from the PushSubscription keys.</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>The client's auth secret (base64url), from the PushSubscription keys.</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>Best-effort label of the browser that subscribed, for the user to recognise it.</summary>
    public string? UserAgent { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
