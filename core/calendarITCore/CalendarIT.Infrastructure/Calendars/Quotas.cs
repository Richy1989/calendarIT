using CalendarIT.Application;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// How much one account may store (<c>QUOTA_*</c>). With open sign-up anyone can make an
/// account, and nothing else stops one from filling the disk or having the server fetch hundreds
/// of feeds every few minutes. The defaults are far above what a person uses.
/// </summary>
public sealed class QuotaOptions
{
    public static readonly QuotaOptions Default = new();

    /// <summary>Event rows (series, one-offs and edited occurrences) the user wrote themselves;
    /// invitations they received don't count, so nobody can fill someone else's quota.</summary>
    public int Events { get; init; } = 100_000;

    public int Tasks { get; init; } = 20_000;

    /// <summary>Calendars of the user's own (subscriptions are counted apart).</summary>
    public int Calendars { get; init; } = 50;

    /// <summary>Subscribed feeds — each one is fetched again every few minutes.</summary>
    public int Subscriptions { get; init; } = 10;

    public int Categories { get; init; } = 200;

    public static QuotaOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Events = Positive(configuration, "QUOTA_EVENTS", Default.Events),
        Tasks = Positive(configuration, "QUOTA_TASKS", Default.Tasks),
        Calendars = Positive(configuration, "QUOTA_CALENDARS", Default.Calendars),
        Subscriptions = Positive(configuration, "QUOTA_SUBSCRIPTIONS", Default.Subscriptions),
        Categories = Positive(configuration, "QUOTA_CATEGORIES", Default.Categories),
    };

    private static int Positive(IConfiguration configuration, string key, int fallback) =>
        configuration.Setting(key, fallback) is var v && v > 0 ? v : fallback;
}

/// <summary>Raised when a write would take an account over a <see cref="QuotaOptions"/> limit: a
/// 400 with the reason from the API, 507 Insufficient Storage from CalDAV.</summary>
public sealed class QuotaExceededException(string message) : InvalidInputException(message);

/// <summary>The checks, one per kind of row. Each counts what is stored and throws
/// <see cref="QuotaExceededException"/> when <c>adding</c> more wouldn't fit.</summary>
public static class Quotas
{
    public static async Task EnsureEventsAsync(AppDbContext db, QuotaOptions quotas, Guid userId, int adding, CancellationToken ct)
    {
        var count = await db.Events.CountAsync(
            e => e.Calendar!.OwnerUserId == userId && e.Calendar.SourceUrl == null
                && e.InvitationStatus == null && e.SourceOrganizerUserId == null, ct);
        if (count + adding > quotas.Events)
        {
            throw new QuotaExceededException(
                $"Your account is at its limit of {quotas.Events:N0} events. Delete some you no longer need first.");
        }
    }

    public static async Task EnsureTasksAsync(AppDbContext db, QuotaOptions quotas, Guid userId, int adding, CancellationToken ct)
    {
        var count = await db.Tasks.CountAsync(t => t.Calendar!.OwnerUserId == userId, ct);
        if (count + adding > quotas.Tasks)
        {
            throw new QuotaExceededException(
                $"Your account is at its limit of {quotas.Tasks:N0} tasks. Delete some you no longer need first.");
        }
    }

    public static async Task EnsureCalendarsAsync(AppDbContext db, QuotaOptions quotas, Guid userId, CancellationToken ct)
    {
        if (await db.Calendars.CountAsync(c => c.OwnerUserId == userId && c.SourceUrl == null, ct) >= quotas.Calendars)
        {
            throw new QuotaExceededException($"An account can have up to {quotas.Calendars} calendars.");
        }
    }

    public static async Task EnsureSubscriptionsAsync(AppDbContext db, QuotaOptions quotas, Guid userId, CancellationToken ct)
    {
        if (await db.Calendars.CountAsync(c => c.OwnerUserId == userId && c.SourceUrl != null, ct) >= quotas.Subscriptions)
        {
            throw new QuotaExceededException($"An account can subscribe to up to {quotas.Subscriptions} calendars.");
        }
    }

    public static async Task<bool> CategoryFitsAsync(AppDbContext db, QuotaOptions quotas, Guid userId, int pending, CancellationToken ct) =>
        await db.Categories.CountAsync(c => c.OwnerUserId == userId, ct) + pending < quotas.Categories;

    public static async Task EnsureCategoriesAsync(AppDbContext db, QuotaOptions quotas, Guid userId, CancellationToken ct)
    {
        if (!await CategoryFitsAsync(db, quotas, userId, 0, ct))
        {
            throw new QuotaExceededException($"An account can have up to {quotas.Categories} categories.");
        }
    }
}
