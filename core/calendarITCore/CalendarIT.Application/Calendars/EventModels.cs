using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Calendars;

/// <summary>
/// An event as returned to the client. Times are UTC (ISO 8601 with offset). For a
/// recurring series, the range query returns one DTO per expanded occurrence — all share
/// the master <see cref="Id"/>, carry <see cref="Recurring"/> = true, and the series'
/// <see cref="Recurrence"/> (RRULE).
/// </summary>
public sealed record EventDto(
    Guid Id,
    string Title,
    string? Description,
    string? Location,
    string? Color,
    DateTimeOffset Start,
    DateTimeOffset? End,
    bool AllDay,
    bool Recurring,
    string? Recurrence);

/// <summary>Create/update payload for an event. Used for both POST and PUT.</summary>
public sealed class SaveEventRequest
{
    [Required, MaxLength(500)]
    public string Title { get; init; } = string.Empty;

    [MaxLength(8000)]
    public string? Description { get; init; }

    [MaxLength(500)]
    public string? Location { get; init; }

    /// <summary>Hex color (e.g. "#7B68EE"). Optional.</summary>
    [MaxLength(32)]
    public string? Color { get; init; }

    [Required]
    public DateTimeOffset Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public bool AllDay { get; init; }

    /// <summary>iCalendar RRULE (e.g. "FREQ=WEEKLY"). Null/empty = a single event.</summary>
    [MaxLength(1000)]
    public string? Recurrence { get; init; }

    /// <summary>IANA time zone (e.g. "Europe/Berlin") the event was authored in.</summary>
    [MaxLength(64)]
    public string? TimeZone { get; init; }
}
