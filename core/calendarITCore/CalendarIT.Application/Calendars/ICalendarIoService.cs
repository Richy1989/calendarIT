namespace CalendarIT.Application.Calendars;

/// <summary>Outcome of an .ics import.</summary>
public sealed record ImportResult(int Imported, int Skipped);

/// <summary>Import and export of the user's calendar as standard iCalendar (.ics).</summary>
public interface ICalendarIoService
{
    /// <summary>
    /// Serializes the user's events to a single VCALENDAR string — all of them, or only
    /// those in the given calendars (ids the user doesn't own are ignored).
    /// </summary>
    Task<string> ExportAsync(Guid userId, IReadOnlyCollection<Guid>? calendarIds = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses an .ics document and adds its events to a calendar: a newly created one when
    /// <paramref name="newCalendarName"/> is given, else <paramref name="calendarId"/> when
    /// the user owns it, else the default calendar. Events whose UID already exists for the
    /// user are skipped (idempotent re-import).
    /// </summary>
    Task<ImportResult> ImportAsync(
        Guid userId,
        string ics,
        Guid? calendarId = null,
        string? newCalendarName = null,
        CancellationToken cancellationToken = default);
}
