using CalendarIT.Application.Notifications;
using CalendarIT.Domain;

namespace CalendarIT.Tests;

/// <summary>Records every push send, and can pretend a subscription is gone (to test pruning).</summary>
public sealed class FakeWebPushSender : IWebPushSender
{
    public List<(PushSubscription Subscription, PushMessage Message)> Sent { get; } = [];

    /// <summary>When false, the dispatcher treats WebPush as unconfigured and skips.</summary>
    public bool IsConfigured { get; set; } = true;

    /// <summary>Endpoints the fake push service reports as gone (410) — the caller should prune them.</summary>
    public HashSet<string> ExpiredEndpoints { get; } = [];

    public Task<PushSendResult> SendAsync(
        PushSubscription subscription, PushMessage message, CancellationToken cancellationToken = default)
    {
        if (ExpiredEndpoints.Contains(subscription.Endpoint))
        {
            return Task.FromResult(PushSendResult.Expired);
        }
        Sent.Add((subscription, message));
        return Task.FromResult(PushSendResult.Sent);
    }
}
