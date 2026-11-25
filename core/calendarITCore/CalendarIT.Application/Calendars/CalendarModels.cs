using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Calendars;

/// <summary>One of the user's calendars, as returned to the client. <paramref name="DefaultCategoryId"/>
/// is the category its events take when they have none of their own (null: none);
/// <paramref name="DefaultReminders"/> the reminders a new timed event in it starts with.</summary>
/// <param name="Subscription">Set for a subscribed (read-only) calendar.</param>
public sealed record CalendarDto(
    Guid Id, string Name, int EventCount, Guid? DefaultCategoryId = null, IReadOnlyList<ReminderDto>? DefaultReminders = null,
    CalendarSubscriptionDto? Subscription = null);

/// <summary>Where a subscribed calendar comes from and how its last refresh went. A password in
/// the URL is never sent back.</summary>
public sealed record CalendarSubscriptionDto(
    string Url, int RefreshMinutes, DateTimeOffset? LastFetched, string? LastError);

/// <summary>Subscribes to an .ics feed: a new, read-only calendar kept in step with it.</summary>
public sealed class SubscribeCalendarRequest
{
    public static readonly int[] RefreshChoices = [60, 360, 720, 1440];

    /// <summary>https, http or webcal.</summary>
    [Required, MaxLength(2000)]
    public string Url { get; init; } = string.Empty;

    /// <summary>Optional: else the feed's own name, else its host.</summary>
    [MaxLength(200)]
    public string? Name { get; init; }

    /// <summary>Minutes between refreshes: one of <see cref="RefreshChoices"/>.</summary>
    public int RefreshMinutes { get; init; } = 360;

    public Guid? DefaultCategoryId { get; init; }
}

/// <summary>Changes how often a subscription is refreshed.</summary>
public sealed class SetRefreshRequest
{
    public int RefreshMinutes { get; init; }
}

/// <summary>Calendar subscriptions (.ics feeds).</summary>
public interface ICalendarSubscriptionService
{
    /// <summary>Fetches the feed first: a URL that doesn't give a calendar leaves nothing behind
    /// and throws <see cref="InvalidInputException"/> saying why.</summary>
    Task<CalendarDto> SubscribeAsync(Guid userId, SubscribeCalendarRequest request, CancellationToken cancellationToken = default);

    /// <summary>Fetches now. Null when it isn't one of the user's subscriptions; a failure is
    /// reported in the returned calendar's <see cref="CalendarSubscriptionDto.LastError"/>.</summary>
    Task<CalendarDto?> RefreshAsync(Guid userId, Guid calendarId, CancellationToken cancellationToken = default);

    Task<CalendarDto?> SetRefreshMinutesAsync(Guid userId, Guid calendarId, int minutes, CancellationToken cancellationToken = default);
}

/// <summary>Create/rename payload for a calendar.</summary>
public sealed class SaveCalendarRequest
{
    /// <summary>Create only: the id the new calendar gets, chosen by an offline client (the app) so it
    /// can refer to it before the server has seen it. Null = the server picks one. Ignored on edit.</summary>
    public Guid? Id { get; init; }

    [Required, MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    /// <summary>On create only: the calendar's default category. Rename leaves it alone.</summary>
    public Guid? DefaultCategoryId { get; init; }
}

/// <summary>Sets (or, with null, clears) a calendar's default category.</summary>
public sealed class SetCalendarCategoryRequest
{
    public Guid? CategoryId { get; init; }
}

/// <summary>The reminders a new timed event in a calendar starts with; empty clears them.</summary>
public sealed class SetCalendarRemindersRequest
{
    /// <summary>A handful at most: they are a starting point, not a schedule.</summary>
    public const int MaxDefaults = 5;

    [MaxLength(MaxDefaults)]
    public IReadOnlyList<ReminderInput> Reminders { get; init; } = [];
}

/// <summary>Outcome of a calendar delete — the last calendar can never be deleted.</summary>
public enum DeleteCalendarResult
{
    Deleted,
    NotFound,
    LastCalendar,
}

/// <summary>CRUD over the user's calendars (events live inside calendars).</summary>
public interface ICalendarService
{
    /// <summary>Lists the user's calendars, creating the default one if none exist yet.</summary>
    Task<IReadOnlyList<CalendarDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<CalendarDto> CreateAsync(Guid userId, SaveCalendarRequest request, CancellationToken cancellationToken = default);

    Task<CalendarDto?> RenameAsync(Guid userId, Guid calendarId, SaveCalendarRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a calendar and (by cascade) all its events.</summary>
    Task<DeleteCalendarResult> DeleteAsync(Guid userId, Guid calendarId, CancellationToken cancellationToken = default);

    /// <summary>Sets or clears the calendar's default category. Null when the calendar isn't the
    /// user's; throws <see cref="InvalidInputException"/> for a category that isn't.</summary>
    Task<CalendarDto?> SetDefaultCategoryAsync(Guid userId, Guid calendarId, Guid? categoryId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the calendar's default reminders. Null when the calendar isn't the user's;
    /// throws <see cref="InvalidInputException"/> for a channel that doesn't exist.</summary>
    Task<CalendarDto?> SetDefaultRemindersAsync(
        Guid userId, Guid calendarId, IReadOnlyList<ReminderInput> reminders, CancellationToken cancellationToken = default);
}
