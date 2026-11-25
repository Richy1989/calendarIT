using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using CalendarIT.Infrastructure.Net;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>What fetching a calendar feed came to.</summary>
public abstract record FeedFetchResult
{
    /// <summary>New content, with the validators for the next conditional request.</summary>
    public sealed record Fetched(string Ics, string? ETag, string? LastModified) : FeedFetchResult;

    /// <summary>The server says nothing changed since the validators we sent.</summary>
    public sealed record NotModified : FeedFetchResult;

    /// <summary>It failed; <see cref="Message"/> is meant for the user.</summary>
    public sealed record Failed(string Message) : FeedFetchResult;
}

/// <summary>
/// Downloads a subscribed calendar's .ics feed. The URL is user input pointed at the outside
/// world, so the request is as careful as a mail connection: every connection — the first and
/// each redirect's — goes through <see cref="OutboundHostPolicy"/> with the public-only scope on
/// the address actually dialled; redirects are followed by hand (at most five, http/https only);
/// a login in the URL is sent to its own host only and never follows a redirect elsewhere; the
/// body is capped (after decompression) and the whole fetch is time-limited.
/// </summary>
public class CalendarFeedFetcher
{
    public const int MaxBytes = 5 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpMessageInvoker _http;

    public CalendarFeedFetcher() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = await OutboundHostPolicy.ConnectAsync(
                context.DnsEndPoint.Host, context.DnsEndPoint.Port, OutboundHostScope.Public, TimeSpan.FromSeconds(10), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        },
    })
    {
    }

    /// <summary>For tests: a handler standing in for the network. It must not follow redirects.</summary>
    public CalendarFeedFetcher(HttpMessageHandler handler) => _http = new HttpMessageInvoker(handler, disposeHandler: false);

    /// <summary>
    /// Turns what a user pasted into the URL that is stored and fetched: webcal(s) becomes https,
    /// only http/https are accepted, the fragment is dropped. Null when it can't be a feed URL.
    /// </summary>
    public static string? Normalize(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 2000)
        {
            return null;
        }
        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text["webcal://".Length..];
        }
        else if (text.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text["webcals://".Length..];
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host)
            || OutboundHostPolicy.IsBlockedLiteral(uri.Host, OutboundHostScope.Public))
        {
            return null;
        }
        return new UriBuilder(uri) { Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    public virtual async Task<FeedFetchResult> FetchAsync(
        string url, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var original = new Uri(url);
        var credentials = string.IsNullOrEmpty(original.UserInfo) ? null : Uri.UnescapeDataString(original.UserInfo);
        var current = original;
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, WithoutUserInfo(current));
                request.Headers.Accept.ParseAdd("text/calendar, */*;q=0.5");
                request.Headers.UserAgent.ParseAdd("CalendarIT/1.0");
                // The login belongs to the host it was written for: a redirect to anyone else
                // must not receive it (nor may a downgrade to plain http on the same host).
                if (credentials is not null && SameOrigin(current, original))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
                }
                if (hop == 0 || SameOrigin(current, original))
                {
                    if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var tag))
                    {
                        request.Headers.IfNoneMatch.Add(tag);
                    }
                    if (DateTimeOffset.TryParse(lastModified, out var since))
                    {
                        request.Headers.IfModifiedSince = since;
                    }
                }

                using var response = await _http.SendAsync(request, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.StatusCode != HttpStatusCode.NotModified)
                {
                    if (response.Headers.Location is not { } location)
                    {
                        return new FeedFetchResult.Failed($"{current.Host} redirected without saying where to.");
                    }
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next.Scheme != Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttp)
                    {
                        return new FeedFetchResult.Failed($"{current.Host} redirected to an address that isn't a web address.");
                    }
                    current = next;
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return new FeedFetchResult.NotModified();
                }
                if (!response.IsSuccessStatusCode)
                {
                    return new FeedFetchResult.Failed(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? $"{current.Host} wants a login for this calendar (answered {(int)response.StatusCode}). Use the private or secret link it offers."
                        : $"{current.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                }
                if (response.Content.Headers.ContentLength > MaxBytes)
                {
                    return TooLarge();
                }

                var body = await ReadCappedAsync(response.Content, timeout.Token);
                if (body is null)
                {
                    return TooLarge();
                }
                if (!body.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                {
                    return new FeedFetchResult.Failed("That address didn't return a calendar (.ics) file.");
                }
                return new FeedFetchResult.Fetched(
                    body,
                    response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified?.ToString("R"));
            }
            return new FeedFetchResult.Failed("The calendar's address redirected too many times.");
        }
        catch (OutboundHostBlockedException)
        {
            return new FeedFetchResult.Failed(
                $"{current.Host} isn't reachable from this server: only calendars on the public internet can be subscribed to.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FeedFetchResult.Failed($"{current.Host} didn't answer in time.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is OutboundHostBlockedException)
        {
            return new FeedFetchResult.Failed(
                $"{current.Host} isn't reachable from this server: only calendars on the public internet can be subscribed to.");
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or TimeoutException)
        {
            return new FeedFetchResult.Failed($"Couldn't connect to {current.Host}.");
        }
    }

    private static FeedFetchResult TooLarge() =>
        new FeedFetchResult.Failed($"The calendar is larger than {MaxBytes / (1024 * 1024)} MB.");

    /// <summary>The body as text, or null when it is longer than <see cref="MaxBytes"/>.</summary>
    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static Uri WithoutUserInfo(Uri uri) =>
        string.IsNullOrEmpty(uri.UserInfo) ? uri : new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;
}
