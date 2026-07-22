using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Calendars;

/// <summary>One of the user's calendars, as returned to the client.</summary>
public sealed record CalendarDto(Guid Id, string Name, int EventCount);

/// <summary>Create/rename payload for a calendar.</summary>
public sealed class SaveCalendarRequest
{
    [Required, MaxLength(200)]
    public string Name { get; init; } = string.Empty;
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
}
