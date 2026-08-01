using System.Net;
using System.Text.Json;
using CalendarIT.Application.Notifications;
using Microsoft.Extensions.Logging;
using WebPush;
using DomainPushSubscription = CalendarIT.Domain.PushSubscription;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Sends VAPID-signed Web Push messages via the <c>WebPush</c> library. Stateless and thread-safe
/// (one reusable <see cref="WebPushClient"/>), so it's registered as a singleton. Reports a gone
/// subscription (HTTP 404/410) back to the caller as <see cref="PushSendResult.Expired"/> so the
/// dispatcher can prune it.
/// </summary>
public sealed class WebPushSender(VapidKeyStore vapid, ILogger<WebPushSender> logger) : IWebPushSender
{
    private readonly WebPushClient _client = new();

    public bool IsConfigured => vapid.IsConfigured;

    public async Task<PushSendResult> SendAsync(
        DomainPushSubscription subscription, PushMessage message, CancellationToken cancellationToken = default)
    {
        if (!vapid.IsConfigured)
        {
            return PushSendResult.Failed;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title = message.Title,
            body = message.Body,
            url = message.Url,
            tag = message.Tag,
        });

        var sub = new WebPush.PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
        var details = new VapidDetails(vapid.Subject, vapid.PublicKey, vapid.PrivateKey);

        try
        {
            await _client.SendNotificationAsync(sub, payload, details, cancellationToken);
            return PushSendResult.Sent;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            logger.LogInformation("Push subscription is gone ({Status}); pruning {Endpoint}", ex.StatusCode, subscription.Endpoint);
            return PushSendResult.Expired;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web Push send failed for {Endpoint}", subscription.Endpoint);
            return PushSendResult.Failed;
        }
    }
}
