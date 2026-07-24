using CalendarIT.CalDav;
using Microsoft.Extensions.Caching.Memory;

namespace CalendarIT.Tests;

/// <summary>
/// The cache that spares a syncing CalDAV client a PBKDF2 verification per request. What matters
/// is that it only ever recognises the exact credential that was proven — anything else has to
/// miss and fall through to the real password check.
/// </summary>
public sealed class CalDavCredentialCacheTests
{
    private static CalDavCredentialCache NewCache() => new(new MemoryCache(new MemoryCacheOptions()));

    private static readonly CalDavCredentialCache.CachedPrincipal Principal =
        new(Guid.NewGuid(), "user@example.com");

    [Fact]
    public void RecognisesTheCredentialItStored()
    {
        var cache = NewCache();
        cache.Store("user@example.com", "correct horse", Principal);

        Assert.True(cache.TryGet("user@example.com", "correct horse", out var hit));
        Assert.Equal(Principal.UserId, hit!.UserId);
    }

    [Fact]
    public void MissesOnAWrongPassword()
    {
        var cache = NewCache();
        cache.Store("user@example.com", "correct horse", Principal);

        Assert.False(cache.TryGet("user@example.com", "wrong horse", out _));
    }

    [Fact]
    public void MissesForADifferentUser()
    {
        var cache = NewCache();
        cache.Store("user@example.com", "correct horse", Principal);

        Assert.False(cache.TryGet("someone-else@example.com", "correct horse", out _));
    }

    [Fact]
    public void MissesWhenNothingWasStored()
    {
        Assert.False(NewCache().TryGet("user@example.com", "correct horse", out _));
    }

    [Fact]
    public void UsernameMatchIsCaseInsensitive()
    {
        // Identity treats the login as case-insensitive, so the cache must not force a client
        // that capitalises differently back onto the slow path every request.
        var cache = NewCache();
        cache.Store("User@Example.com", "correct horse", Principal);

        Assert.True(cache.TryGet("user@example.com", "correct horse", out _));
    }

    [Fact]
    public void TwoInstancesDoNotShareKeys()
    {
        // Each instance pepper is random, so a key is meaningless outside the process that made
        // it — nothing derived from a password survives a restart or leaks between instances.
        var first = NewCache();
        first.Store("user@example.com", "correct horse", Principal);

        Assert.False(NewCache().TryGet("user@example.com", "correct horse", out _));
    }
}
