namespace CalendarIT.Application.Notifications;

/// <summary>Snoozing a browser reminder from its notification.</summary>
public interface IReminderSnoozes
{
    /// <summary>
    /// Snoozes the reminder occurrence a notification's token names for <paramref name="minutes"/>.
    /// False when the token is invalid, expired or names a reminder that no longer exists.
    /// </summary>
    Task<bool> SnoozeAsync(string token, int minutes, CancellationToken cancellationToken = default);
}
