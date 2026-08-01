using CalendarIT.Domain;

namespace CalendarIT.Application.Notifications;

/// <summary>The notification a service worker should show, carried in the encrypted push payload.</summary>
public sealed record PushMessage(string Title, string Body, string? Url = null, string? Tag = null);

/// <summary>Outcome of a single push send, so the caller can prune dead subscriptions.</summary>
public enum PushSendResult
{
    /// <summary>The push service accepted it.</summary>
    Sent,

    /// <summary>The push service reported the subscription gone (404/410) — delete the row.</summary>
    Expired,

    /// <summary>A transient/other failure — keep the row and let a later reminder retry.</summary>
    Failed
}

/// <summary>Sends one encrypted Web Push message to one browser subscription (VAPID-signed).</summary>
public interface IWebPushSender
{
    /// <summary>
    /// True only when VAPID keys are configured. When false, <see cref="SendAsync"/> is a no-op —
    /// the dispatcher logs and skips rather than throwing.
    /// </summary>
    bool IsConfigured { get; }

    Task<PushSendResult> SendAsync(PushSubscription subscription, PushMessage message, CancellationToken cancellationToken = default);
}
