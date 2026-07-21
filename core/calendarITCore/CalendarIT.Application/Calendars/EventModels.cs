using System.ComponentModel.DataAnnotations;

namespace CalendarIT.Application.Calendars;

/// <summary>An event as returned to the client. Times are UTC (ISO 8601 with offset).</summary>
public sealed record EventDto(
    Guid Id,
    string Title,
    string? Description,
    string? Location,
    string? Color,
    DateTimeOffset Start,
    DateTimeOffset? End,
    bool AllDay);

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
}
