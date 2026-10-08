namespace calendarITCore.Extensions;

/// <summary>
/// Baseline browser hardening on every response the app itself serves.
///
/// These used to live only in the bundled image's nginx config, so the other way of running the
/// app — Kestrel serving the SPA directly, as the compose file does — sent none of them: no CSP,
/// and nothing stopping the calendar being framed by someone else's page. Setting them here makes
/// them hold however the app is deployed; the bundled nginx hides the copies it proxies so a
/// response never carries two.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Scripts are bundled files only, so script-src stays strict. Styles allow inline because
    /// FullCalendar positions events with style attributes; data: images are the avatar data URLs.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                // nosniff matters most for avatars: stored with a client-supplied content type.
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                // Origin only, even to ourselves: a reset page's URL carries its token, and a full
                // referer would repeat it in every request that page makes — into any proxy's log.
                headers["Referrer-Policy"] = "strict-origin";
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                return Task.CompletedTask;
            });
            await next();
        });
}
