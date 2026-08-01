namespace CalendarIT.Application.Notifications;

/// <summary>
/// Read-only: the signed-in user's browser-channel reminder occurrences whose trigger time falls
/// in (sinceUtc, now]. Used by the local-notification fallback poller. Never writes NotificationLog.
/// </summary>
public interface IDueReminderQuery
{
    Task<IReadOnlyList<DueReminderDto>> GetDueAsync(Guid userId, DateTime sinceUtc, CancellationToken cancellationToken = default);
}
