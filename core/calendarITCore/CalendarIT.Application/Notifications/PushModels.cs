using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Notifications;

/// <summary>
/// A browser Push API subscription as the client hands it over (the JSON shape of
/// <c>PushSubscription.toJSON()</c>), sent to <c>POST /api/push/subscribe</c>.
/// </summary>
public sealed class PushSubscriptionInput
{
    [Required, MaxLength(2000)]
    public string Endpoint { get; init; } = string.Empty;

    [Required]
    public PushSubscriptionKeys Keys { get; init; } = new();
}

/// <summary>The ECDH public key and auth secret of a browser push subscription (base64url).</summary>
public sealed class PushSubscriptionKeys
{
    [Required, MaxLength(255)]
    public string P256dh { get; init; } = string.Empty;

    [Required, MaxLength(255)]
    public string Auth { get; init; } = string.Empty;
}

/// <summary>Body of <c>POST /api/push/unsubscribe</c>: the endpoint to forget.</summary>
public sealed class PushUnsubscribeInput
{
    [Required, MaxLength(2000)]
    public string Endpoint { get; init; } = string.Empty;
}

/// <summary>The VAPID application-server public key the browser needs to subscribe.</summary>
public sealed record VapidPublicKeyDto(string PublicKey);
