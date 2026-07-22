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
    Guid CalendarId,
    string Title,
    string? Description,
    string? Location,
    string? Color,
    DateTimeOffset Start,
    DateTimeOffset? End,
    bool AllDay,
    bool Recurring,
    string? Recurrence,
    IReadOnlyList<ReminderDto> Reminders);

/// <summary>A reminder: fire <paramref name="MinutesBefore"/> minutes before start, via <paramref name="Channel"/>.</summary>
public sealed record ReminderDto(int MinutesBefore, string Channel);

/// <summary>
/// A lightweight search hit (title/location match). For a recurring series, <see cref="Start"/>
/// is the next upcoming occurrence, or the most recent past one if none remain.
/// </summary>
public sealed record EventSearchResult(
    Guid Id,
    string Title,
    string? Location,
    string? Color,
    DateTimeOffset Start,
    bool AllDay,
    bool Recurring);

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

    /// <summary>Reminders for this event; replaces the existing set on update.</summary>
    public IReadOnlyList<ReminderInput>? Reminders { get; init; }

    /// <summary>
    /// Target calendar. Create: null = the user's default (first) calendar.
    /// Update: null = leave the event where it is; a value moves it there.
    /// </summary>
    public Guid? CalendarId { get; init; }
}

/// <summary>One reminder in a save request.</summary>
public sealed class ReminderInput
{
    [Range(0, 40320)] // up to 4 weeks before
    public int MinutesBefore { get; init; }

    /// <summary>"Email" or "WebPush".</summary>
    [Required, MaxLength(16)]
    public string Channel { get; init; } = "Email";
}
