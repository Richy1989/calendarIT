using CalendarIT.Application;
using CalendarIT.Application.Notifications;
using CalendarIT.Infrastructure.Net;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using DomainPushSubscription = CalendarIT.Domain.PushSubscription;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>Stores browser Web Push subscriptions, keyed by their unique push endpoint.</summary>
public sealed class PushSubscriptionService(AppDbContext db, VapidKeyStore vapid, TimeProvider timeProvider)
    : IPushSubscriptionService
{
    public string VapidPublicKey => vapid.PublicKey;

    /// <summary>Browser subscriptions kept per user; the oldest beyond this are dropped.</summary>
    private const int MaxSubscriptionsPerUser = 20;

    public async Task SubscribeAsync(
        Guid userId, PushSubscriptionInput input, string? userAgent, CancellationToken cancellationToken = default)
    {
        // Every real push service is an https URL on the public internet. Anything else is not a
        // browser subscription, and storing it would have the reminder job POST to it.
        if (!Uri.TryCreate(input.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || OutboundHostPolicy.IsBlockedLiteral(endpoint.Host, OutboundHostScope.Public))
        {
            throw new InvalidInputException("That isn't a valid push subscription endpoint.");
        }

        var existing = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == input.Endpoint, cancellationToken);

        if (existing is null)
        {
            db.PushSubscriptions.Add(new DomainPushSubscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Endpoint = input.Endpoint,
                P256dh = input.Keys.P256dh,
                Auth = input.Keys.Auth,
                UserAgent = Truncate(userAgent, 500),
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            });
        }
        else
        {
            // Same browser re-subscribing: its keys may have rotated, and it may now belong to a
            // different signed-in user on a shared device — re-point the row either way.
            existing.UserId = userId;
            existing.P256dh = input.Keys.P256dh;
            existing.Auth = input.Keys.Auth;
            existing.UserAgent = Truncate(userAgent, 500);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Each subscription is a delivery per reminder; a user can't accumulate them without end.
        var surplus = await db.PushSubscriptions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .Skip(MaxSubscriptionsPerUser)
            .ToListAsync(cancellationToken);
        if (surplus.Count > 0)
        {
            db.PushSubscriptions.RemoveRange(surplus);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> UnsubscribeAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default)
    {
        var sub = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == endpoint && s.UserId == userId, cancellationToken);
        if (sub is null)
        {
            return false;
        }

        db.PushSubscriptions.Remove(sub);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];
}
