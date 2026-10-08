using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Calendars;

/// <summary>One of the user's calendars, as returned to the client. <paramref name="DefaultCategoryId"/>
/// is the category its events take when they have none of their own (null: none).</summary>
public sealed record CalendarDto(Guid Id, string Name, int EventCount, Guid? DefaultCategoryId = null);

/// <summary>Create/rename payload for a calendar.</summary>
public sealed class SaveCalendarRequest
{
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
}
