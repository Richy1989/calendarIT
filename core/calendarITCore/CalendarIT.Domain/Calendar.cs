namespace CalendarIT.Domain;

/// <summary>
/// A named collection of events owned by a user. Every user gets at least one default
/// calendar; multiple calendars / sharing come in a later phase.
/// </summary>
public class Calendar
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public string Name { get; set; } = "Personal";

    /// <summary>Hex color for the calendar itself (distinct from per-event color).</summary>
    public string? Color { get; set; }

    /// <summary>Default IANA time zone for events created in this calendar.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// The category events in this calendar take when they have none of their own — so a whole
    /// imported holiday calendar is colored at once, and recolors with it. An event's own category
    /// always wins. Null = no default.
    /// </summary>
    public Guid? DefaultCategoryId { get; set; }

    public Category? DefaultCategory { get; set; }

    /// <summary>
    /// The reminders a new timed event in this calendar starts with, so creating one doesn't
    /// mean setting them each time: "minutes:channel" pairs, ';'-separated ("15:WebPush;1440:Email").
    /// Only the web editor applies them, as a starting point the user can change. Null = none.
    /// </summary>
    public string? DefaultReminders { get; set; }

    /// <summary>
    /// Set for a subscribed calendar: the .ics feed (https/http; webcal is stored as https) its
    /// events are copied from. Such a calendar is read-only — its events change only at the
    /// source. Null = an ordinary calendar.
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>How often the feed is fetched, in minutes (subscriptions only).</summary>
    public int? RefreshMinutes { get; set; }

    /// <summary>When the feed was last read successfully (subscriptions only).</summary>
    public DateTime? LastFetchedUtc { get; set; }

    /// <summary>When the next fetch is due; null = as soon as possible.</summary>
    public DateTime? NextFetchUtc { get; set; }

    /// <summary>Why the last fetch failed, in words for the user; null after a success.</summary>
    public string? LastFetchError { get; set; }

    /// <summary>The feed's ETag / Last-Modified from the last fetch, for a conditional request.</summary>
    public string? SourceETag { get; set; }

    public string? SourceLastModified { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<CalendarEvent> Events { get; set; } = new List<CalendarEvent>();
}
