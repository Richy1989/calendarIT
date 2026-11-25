using System.Globalization;
using CalendarIT.Application;
using CalendarIT.Application.Notifications;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Snooze for browser reminders. A notification's Snooze button is handled by the service worker,
/// which has no sign-in, so each notification carries a token: the reminder occurrence and its
/// owner, sealed with the app's data-protection keys (already persisted for mail passwords) and
/// valid for two days. It can snooze that one reminder and nothing else.
/// </summary>
public sealed class ReminderSnoozes(AppDbContext db, IDataProtectionProvider protection, TimeProvider clock) : IReminderSnoozes
{
    public static readonly int[] AllowedMinutes = [5, 10, 15, 30, 60];

    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(2);
    private const string Purpose = "CalendarIT.ReminderSnooze.v1";

    // The expiry is sealed in with the rest and checked against the app's clock — not the
    // time-limited protector's, which reads the system clock directly.
    private IDataProtector Protector => protection.CreateProtector(Purpose);

    public string CreateToken(Guid ownerUserId, Guid reminderId, DateTime occurrenceStartUtc) =>
        Protector.Protect(string.Join('|',
            ownerUserId.ToString("N"),
            reminderId.ToString("N"),
            occurrenceStartUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            (clock.GetUtcNow().UtcDateTime + TokenLifetime).Ticks.ToString(CultureInfo.InvariantCulture)));

    public async Task<bool> SnoozeAsync(string token, int minutes, CancellationToken cancellationToken = default)
    {
        if (!AllowedMinutes.Contains(minutes))
        {
            throw new InvalidInputException($"Snooze for one of {string.Join(", ", AllowedMinutes)} minutes.");
        }
        if (!TryRead(token, out var ownerUserId, out var reminderId, out var occurrence))
        {
            return false;
        }

        // The reminder must still exist and still be the owner's: an event moved to someone
        // else's calendar, or deleted, takes its snoozes with it.
        var owned = await db.Reminders.AnyAsync(
            r => r.Id == reminderId && r.Channel == ReminderChannel.WebPush && r.Event!.Calendar!.OwnerUserId == ownerUserId,
            cancellationToken);
        if (!owned)
        {
            return false;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var snooze = await db.ReminderSnoozes.SingleOrDefaultAsync(
            s => s.ReminderId == reminderId && s.OccurrenceStartUtc == occurrence, cancellationToken);
        if (snooze is null)
        {
            snooze = new ReminderSnooze { Id = Guid.NewGuid(), ReminderId = reminderId, OccurrenceStartUtc = occurrence };
            db.ReminderSnoozes.Add(snooze);
        }
        snooze.DueAtUtc = ReminderOccurrences.Truncate(now.AddMinutes(minutes));
        snooze.SentAtUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private bool TryRead(string token, out Guid ownerUserId, out Guid reminderId, out DateTime occurrence)
    {
        ownerUserId = reminderId = Guid.Empty;
        occurrence = default;
        string plain;
        try
        {
            plain = Protector.Unprotect(token);
        }
        catch (Exception) // tampered, expired, or from other keys — all the same to the caller
        {
            return false;
        }
        var parts = plain.Split('|');
        if (parts.Length != 4
            || !Guid.TryParseExact(parts[0], "N", out ownerUserId)
            || !Guid.TryParseExact(parts[1], "N", out reminderId)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || expires < clock.GetUtcNow().UtcDateTime.Ticks)
        {
            return false;
        }
        occurrence = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }
}
