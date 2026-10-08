using System.Collections.Concurrent;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CalendarIT.Infrastructure.Auth;

/// <summary>
/// Whether the session an access token came from is still signed in.
///
/// Access tokens are self-contained JWTs, so on their own they stay valid until they expire —
/// signing a device out (or changing your password) left it working for up to a quarter of an
/// hour. Each token now names its session (<c>sid</c>), and a request is accepted only while that
/// session still has a live refresh token. The answer is cached briefly per session; revoking a
/// session evicts its entry, so on this instance a sign-out takes effect on the very next request.
/// </summary>
public sealed class SessionValidator(IMemoryCache cache, AppDbContext db, TimeProvider clock)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(Key(sessionId), out bool active))
        {
            return active;
        }
        var now = clock.GetUtcNow();
        // Expiry is compared in memory: SQLite can't compare DateTimeOffset in SQL. A session
        // has one live token at a time, so this reads a row or two.
        var expiries = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.SessionId == sessionId && t.RevokedAt == null)
            .Select(t => t.ExpiresAt)
            .ToListAsync(cancellationToken);
        active = expiries.Any(e => e > now);
        cache.Set(Key(sessionId), active, CacheFor);
        return active;
    }

    /// <summary>Forget the cached answer for these sessions (after revoking them).</summary>
    public static void Evict(IMemoryCache cache, IEnumerable<Guid> sessionIds)
    {
        foreach (var id in sessionIds)
        {
            cache.Remove(Key(id));
        }
    }

    private static string Key(Guid sessionId) => $"session-active:{sessionId:N}";
}

/// <summary>
/// A per-user counter bumped whenever the user's password changes. The CalDAV credential cache
/// folds it into its keys, so a password change retires every cached CalDAV login at once
/// instead of leaving the old password working until the cache entry times out.
/// </summary>
public sealed class CredentialEpochs
{
    private readonly ConcurrentDictionary<Guid, long> _epochs = new();

    public long Of(Guid userId) => _epochs.GetValueOrDefault(userId);

    public void Bump(Guid userId) => _epochs.AddOrUpdate(userId, 1, (_, e) => e + 1);
}
