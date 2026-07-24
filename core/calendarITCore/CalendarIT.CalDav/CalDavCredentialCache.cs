using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace CalendarIT.CalDav;

/// <summary>
/// Remembers, briefly, that a Basic credential checked out — so a syncing client doesn't pay for
/// a password hash on every single request.
///
/// Identity verifies passwords with PBKDF2 (100k iterations by design), which is right for a
/// login form and wrong for CalDAV: a client like DAVx⁵ issues a burst of PROPFIND/REPORT/GET
/// calls on every poll, and each one re-ran the full KDF. That made ordinary syncing expensive
/// and handed anyone who could reach /dav a cheap way to spend our CPU — a request costs the
/// attacker nothing and costs us a KDF, unauthenticated.
///
/// Only successful verifications are cached, so this can neither create nor extend a login, and
/// a wrong guess can't poison it. The password is never stored: the key holds an HMAC of it
/// under a per-process random pepper, so the cache is useless if it leaks and dies with the
/// process.
/// </summary>
public sealed class CalDavCredentialCache(IMemoryCache cache)
{
    /// <summary>
    /// How long a proven credential is trusted without re-checking. This is the window in which
    /// a changed password keeps working on /dav, and in which a lockout doesn't reach a client
    /// that already knows the right password — both acceptable, both bounded, and short enough
    /// that a password change takes effect while you're still on the settings page.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly byte[] _pepper = RandomNumberGenerator.GetBytes(32);

    /// <summary>The identity a verified credential resolves to.</summary>
    public sealed record CachedPrincipal(Guid UserId, string UserName);

    public bool TryGet(string username, string password, out CachedPrincipal? principal) =>
        cache.TryGetValue(KeyFor(username, password), out principal) && principal is not null;

    public void Store(string username, string password, CachedPrincipal principal) =>
        cache.Set(KeyFor(username, password), principal, Ttl);

    private string KeyFor(string username, string password)
    {
        var mac = HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(password));
        return $"caldav:{username.ToLowerInvariant()}:{Convert.ToHexStringLower(mac)}";
    }
}
