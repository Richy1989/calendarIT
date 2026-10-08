using Microsoft.Extensions.Caching.Memory;

namespace CalendarIT.CalDav;

/// <summary>
/// Caps failed CalDAV logins per client address. Per-account lockout already stops guessing at
/// one account; this stops guessing across many (one try each, never tripping any lockout) — and
/// since every wrong guess costs the server a full password hash, it also stops /dav from being a
/// cheap way to burn CPU. Successful logins are never counted.
/// </summary>
public sealed class CalDavFailureThrottle(IMemoryCache cache)
{
    /// <summary>Failed attempts allowed per address per window. Generous: a phone left holding an
    /// old password retries on its own schedule and shouldn't be shut out by it.</summary>
    public const int MaxFailures = 30;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private sealed class Counter
    {
        public int Failures;
    }

    public bool IsBlocked(string? address) =>
        address is not null && cache.TryGetValue(Key(address), out Counter? c) && c!.Failures >= MaxFailures;

    public void RecordFailure(string? address)
    {
        if (address is null)
        {
            return;
        }
        // The window runs from the first failure; the entry (and the count) expires with it.
        var counter = cache.GetOrCreate(Key(address), e =>
        {
            e.AbsoluteExpirationRelativeToNow = Window;
            return new Counter();
        })!;
        Interlocked.Increment(ref counter.Failures);
    }

    private static string Key(string address) => $"caldav-failures:{address}";
}
