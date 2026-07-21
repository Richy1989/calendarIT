namespace CalendarIT.Application.Calendars;

/// <summary>
/// CRUD for a user's events. Each operation is scoped to the calling user; events live
/// in the user's default calendar (auto-created on first use in this phase).
/// </summary>
public interface IEventService
{
    Task<IReadOnlyList<EventDto>> GetEventsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default);

    Task<EventDto> CreateAsync(Guid userId, SaveEventRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the updated event, or null if it doesn't exist or isn't the user's.</summary>
    Task<EventDto?> UpdateAsync(Guid userId, Guid eventId, SaveEventRequest request, CancellationToken cancellationToken = default);

    /// <summary>True if an event was deleted; false if none matched for this user.</summary>
    Task<bool> DeleteAsync(Guid userId, Guid eventId, CancellationToken cancellationToken = default);
}
