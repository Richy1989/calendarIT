namespace CalendarIT.Application.Notifications;

/// <summary>One reminder occurrence that has come due, for the client to show locally. For an
/// all-day event <paramref name="OccurrenceStartUtc"/> is its date (at midnight UTC), not an instant.</summary>
/// <param name="SnoozeToken">For the notification's Snooze button (POST /api/reminders/snooze).</param>
/// <param name="SnoozedUntil">Set when this is a snoozed reminder coming back: when it was due again.</param>
public sealed record DueReminderDto(
    Guid ReminderId, DateTimeOffset OccurrenceStartUtc, string Title, string? Location, bool AllDay = false,
    string? SnoozeToken = null, DateTimeOffset? SnoozedUntil = null);

/// <summary>The due-reminders poll response.</summary>
public sealed record DueRemindersResponse(IReadOnlyList<DueReminderDto> Items);

/// <summary>A notification's snooze token and how long to snooze it.</summary>
public sealed record SnoozeReminderRequest(string Token, int Minutes);
