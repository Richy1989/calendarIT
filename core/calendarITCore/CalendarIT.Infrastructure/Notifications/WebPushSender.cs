using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CalendarIT.Infrastructure.Net;
using System.Text;
using System.Text.Json;
using CalendarIT.Application.Notifications;
using Microsoft.Extensions.Logging;
using WebPush;
using DomainPushSubscription = CalendarIT.Domain.PushSubscription;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Sends VAPID-signed Web Push messages, encrypted as RFC 8291 <c>aes128gcm</c>
/// (<see cref="WebPushEncryption"/>; the <c>WebPush</c> library is used for the VAPID token and keys
/// only — it encrypts as the older <c>aesgcm</c>, which UnifiedPush can't read). Stateless and
/// thread-safe (one reusable <see cref="HttpClient"/>), so it's registered as a singleton. Reports a gone
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
    private readonly HttpClient _http = new(new SocketsHttpHandler
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
    };

    /// <summary>How long a push service keeps an undelivered message: a day (a reminder or nudge older than that is stale).</summary>
    private const int TimeToLiveSeconds = 24 * 60 * 60;

    public bool IsConfigured => vapid.IsConfigured;

    public Task<PushSendResult> SendAsync(
        DomainPushSubscription subscription, PushMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            title = message.Title,
            body = message.Body,
            url = message.Url,
            tag = message.Tag,
            snoozeToken = message.SnoozeToken,
        });
        return SendAsync(new PushTarget(subscription.Endpoint, subscription.P256dh, subscription.Auth), payload, cancellationToken);
    }

    public async Task<PushSendResult> SendAsync(PushTarget target, string payload, CancellationToken cancellationToken = default)
    {
        if (!vapid.IsConfigured)
        {
            return PushSendResult.Failed;
        }

        try
        {
            if (!Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("The endpoint isn't an https URL.");
            }
            // VAPID (RFC 8292): a token signed for the push service's origin, sent with our public key.
            var vapidHeaders = VapidHelper.GetVapidHeaders(
                endpoint.GetLeftPart(UriPartial.Authority), vapid.Subject, vapid.PublicKey, vapid.PrivateKey);
            var token = vapidHeaders["Authorization"]["WebPush ".Length..];

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(WebPushEncryption.Encrypt(Encoding.UTF8.GetBytes(payload), target.P256dh, target.Auth)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={token}, k={vapid.PublicKey}");
            request.Headers.Add("TTL", TimeToLiveSeconds.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("Urgency", "high");

            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return PushSendResult.Sent;
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                logger.LogInformation("Push endpoint at {Host} is gone ({Status}); pruning it", HostOf(target.Endpoint), (int)response.StatusCode);
                return PushSendResult.Expired;
            }
            logger.LogWarning("Web Push to {Host} was refused: {Status}", HostOf(target.Endpoint), (int)response.StatusCode);
            return PushSendResult.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Web Push to {Host} failed: {Error}", HostOf(target.Endpoint), ex.Message);
            return PushSendResult.Failed;
        }
    }

    private static string HostOf(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "(invalid endpoint)";
}
