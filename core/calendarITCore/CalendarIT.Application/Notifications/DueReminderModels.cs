namespace CalendarIT.Application.Notifications;

/// <summary>One reminder occurrence that has come due, for the client to show locally. For an
/// all-day event <paramref name="OccurrenceStartUtc"/> is its date (at midnight UTC), not an instant.</summary>
public sealed record DueReminderDto(Guid ReminderId, DateTimeOffset OccurrenceStartUtc, string Title, string? Location, bool AllDay = false);

/// <summary>The due-reminders poll response.</summary>
public sealed record DueRemindersResponse(IReadOnlyList<DueReminderDto> Items);
