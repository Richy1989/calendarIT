using System.Net;
using System.Net.Sockets;
using CalendarIT.Infrastructure.Net;
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
    /// <summary>
    /// A push endpoint is a URL the browser hands over — i.e. user input. Real ones belong to
    /// public push services, so every connection is checked against the public-only policy on the
    /// address actually dialled (DNS included): an "endpoint" pointing into the server's own
    /// network is refused instead of being POSTed to once per reminder.
    /// </summary>
    private readonly WebPushClient _client = new(new HttpClient(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = await OutboundHostPolicy.ConnectAsync(
                context.DnsEndPoint.Host, context.DnsEndPoint.Port, OutboundHostScope.Public, TimeSpan.FromSeconds(10), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        },
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    });

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
