namespace CalendarIT.Application.Calendars;

/// <summary>
/// CRUD for a user's events. Each operation is scoped to the calling user; events live
/// in the user's default calendar (auto-created on first use in this phase).
/// </summary>
public interface IEventService
{
    /// <summary>Returns events in the window; recurring series are expanded into occurrences.</summary>
    Task<IReadOnlyList<EventDto>> GetEventsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default);

    /// <summary>Returns the single master event (unexpanded), or null. Used when editing a series.</summary>
    Task<EventDto?> GetByIdAsync(Guid userId, Guid eventId, CancellationToken cancellationToken = default);

    Task<EventDto> CreateAsync(Guid userId, SaveEventRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the updated event, or null if it doesn't exist or isn't the user's.</summary>
    Task<EventDto?> UpdateAsync(Guid userId, Guid eventId, SaveEventRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an event. If <paramref name="occurrence"/> is given and the event is a
    /// recurring series, that single occurrence is excluded (EXDATE) instead of deleting
    /// the whole series. Returns false if nothing matched for this user.
    /// </summary>
    Task<bool> DeleteAsync(Guid userId, Guid eventId, DateTimeOffset? occurrence, CancellationToken cancellationToken = default);
}
