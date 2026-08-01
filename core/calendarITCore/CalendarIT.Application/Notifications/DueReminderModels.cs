namespace CalendarIT.Application.Notifications;

/// <summary>One reminder occurrence that has come due, for the client to show locally.</summary>
public sealed record DueReminderDto(Guid ReminderId, DateTimeOffset OccurrenceStartUtc, string Title, string? Location);

/// <summary>The due-reminders poll response.</summary>
public sealed record DueRemindersResponse(IReadOnlyList<DueReminderDto> Items);
