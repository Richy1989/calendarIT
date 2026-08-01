namespace CalendarIT.Application.Notifications;

/// <summary>Stores and removes the signed-in user's browser Web Push subscriptions.</summary>
public interface IPushSubscriptionService
{
    /// <summary>The VAPID public key the browser passes as its <c>applicationServerKey</c>.</summary>
    string VapidPublicKey { get; }

    /// <summary>
    /// Records (or refreshes) a browser subscription for the user. Idempotent on the endpoint:
    /// re-subscribing the same browser updates its keys and re-points it at this user.
    /// </summary>
    Task SubscribeAsync(Guid userId, PushSubscriptionInput input, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>Forgets a subscription by endpoint. Returns false if it wasn't the user's.</summary>
    Task<bool> UnsubscribeAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default);
}
