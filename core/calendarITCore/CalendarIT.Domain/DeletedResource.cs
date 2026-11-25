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

    /// <summary>What left: <see cref="DeletedResourceKinds.Event"/> (a series master or one-off) or
    /// <see cref="DeletedResourceKinds.Task"/>. Null on records written before the app sync API.</summary>
    public string? Kind { get; set; }

    /// <summary>The row's id, for the app sync API (which names things by id, not UID). Null on
    /// records written before it.</summary>
    public Guid? EntityId { get; set; }

    public DateTime DeletedAtUtc { get; set; }
}

/// <summary>Values of <see cref="DeletedResource.Kind"/>.</summary>
public static class DeletedResourceKinds
{
    public const string Event = "event";
    public const string Task = "task";
}
