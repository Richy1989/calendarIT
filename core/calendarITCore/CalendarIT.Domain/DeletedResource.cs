namespace CalendarIT.Domain;

/// <summary>
/// A calendar resource (an event and its edited occurrences, by UID) that left a calendar —
/// deleted, or moved to another one. CalDAV incremental sync (RFC 6578) reports it to clients as
/// gone. Written automatically on save (<c>AppDbContext</c>), pruned after a while: a sync token
/// older than the oldest kept record is refused, and the client starts over.
/// </summary>
public class DeletedResource
{
    public Guid Id { get; set; }

    public Guid CalendarId { get; set; }

    public string Uid { get; set; } = string.Empty;

    public DateTime DeletedAtUtc { get; set; }
}
