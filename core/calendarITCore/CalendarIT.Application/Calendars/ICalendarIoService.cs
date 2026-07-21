namespace CalendarIT.Application.Calendars;

/// <summary>Outcome of an .ics import.</summary>
public sealed record ImportResult(int Imported, int Skipped);

/// <summary>Import and export of the user's calendar as standard iCalendar (.ics).</summary>
public interface ICalendarIoService
{
    /// <summary>Serializes all of the user's events to a single VCALENDAR string.</summary>
    Task<string> ExportAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses an .ics document and adds its events to the user's default calendar.
    /// Events whose UID already exists for the user are skipped (idempotent re-import).
    /// </summary>
    Task<ImportResult> ImportAsync(Guid userId, string ics, CancellationToken cancellationToken = default);
}
