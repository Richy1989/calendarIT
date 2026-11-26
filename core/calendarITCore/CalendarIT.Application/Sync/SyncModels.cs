using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CalendarIT.Application.Calendars;

namespace CalendarIT.Application.Sync;

/// <summary>
/// One page of the app's delta sync (protocol 1). Calendars and categories always come whole —
/// a calendar or category missing from the list is gone, with everything in it. Events and tasks
/// are those changed since the cursor; <see cref="Deleted"/> names those that went (final page
/// only). Changes from just before the cursor are sent again (late commits), so applying a page
/// must be idempotent. With <see cref="Reset"/> the cursor wasn't usable: the pages that follow
/// are a full listing, and whatever the client holds that isn't in them is gone.
/// </summary>
public sealed record SyncPullResponse(
    int Protocol,
    bool Reset,
    string Cursor,
    bool HasMore,
    IReadOnlyList<SyncCalendarDto> Calendars,
    IReadOnlyList<SyncCategoryDto> Categories,
    IReadOnlyList<SyncSeriesDto> Events,
    IReadOnlyList<SyncTaskDto> Tasks,
    SyncDeletions Deleted);

/// <summary>Ids of events (series or one-offs) and tasks that left the user's calendars.</summary>
public sealed record SyncDeletions(IReadOnlyList<Guid> Events, IReadOnlyList<Guid> Tasks);

/// <summary>A calendar. <see cref="ReadOnly"/> marks a subscription: nothing in it can be changed.</summary>
public sealed record SyncCalendarDto(
    Guid Id,
    string Name,
    string? Color,
    string? TimeZone,
    Guid? DefaultCategoryId,
    IReadOnlyList<ReminderDto> DefaultReminders,
    bool ReadOnly,
    string Version,
    string? SubscriptionError = null);

public sealed record SyncCategoryDto(Guid Id, string Name, string Color, string Version);

/// <summary>
/// An event as stored — a one-off, or a series master with its rule, never expanded: the client
/// expands it. <see cref="ExDates"/> holds every instant the rule produces but that isn't shown
/// from the rule: deleted occurrences and those replaced by one of <see cref="Overrides"/>.
/// <see cref="Version"/> is the <c>baseVersion</c> for changes to it or any of its occurrences.
/// </summary>
public sealed record SyncSeriesDto(
    Guid Id,
    Guid CalendarId,
    string Title,
    string? Description,
    string? Location,
    Guid? CategoryId,
    string? Color,
    DateTimeOffset Start,
    DateTimeOffset? End,
    bool AllDay,
    string? TimeZone,
    string? Recurrence,
    IReadOnlyList<DateTimeOffset> ExDates,
    IReadOnlyList<ReminderDto> Reminders,
    IReadOnlyList<AttendeeDto> Attendees,
    string? InvitationStatus,
    string? OrganizerEmail,
    IReadOnlyList<SyncOccurrenceDto> Overrides,
    string Version);

/// <summary>An edited occurrence of a series: it replaces the one the rule puts at <see cref="RecurrenceId"/>.</summary>
public sealed record SyncOccurrenceDto(
    Guid Id,
    DateTimeOffset RecurrenceId,
    string Title,
    string? Description,
    string? Location,
    Guid? CategoryId,
    DateTimeOffset Start,
    DateTimeOffset? End,
    bool AllDay,
    string? TimeZone,
    IReadOnlyList<ReminderDto> Reminders);

public sealed record SyncTaskDto(
    Guid Id,
    Guid CalendarId,
    string Title,
    string? Notes,
    DateTimeOffset? Due,
    bool DueAllDay,
    string? TimeZone,
    string Status,
    DateTimeOffset? CompletedAt,
    string? Priority,
    Guid? ParentId,
    int SortOrder,
    IReadOnlyList<ReminderDto> Reminders,
    string Version);

/// <summary>Changes made on a device, applied in order. Each one stands alone: one that fails
/// doesn't stop the rest, and its result says why.</summary>
public sealed class SyncPushRequest
{
    public const int MaxOps = 100;

    [Required, MaxLength(MaxOps)]
    public IReadOnlyList<SyncOp> Ops { get; init; } = [];
}

/// <summary>
/// One change. <see cref="Op"/> names it (see <see cref="SyncOps"/>); <see cref="Id"/> is the thing
/// it changes — for a create, the id the client chose; for an occurrence op, the series.
/// <see cref="BaseVersion"/>, required on changes to existing events and tasks, is the version the
/// change was made on: when the server's has moved on, the change is refused as a conflict.
/// </summary>
public sealed class SyncOp
{
    /// <summary>The client's name for this change, echoed in its result.</summary>
    [Required, MaxLength(64)]
    public string OpId { get; init; } = string.Empty;

    [Required, MaxLength(32)]
    public string Op { get; init; } = string.Empty;

    public Guid Id { get; init; }

    [MaxLength(32)]
    public string? BaseVersion { get; init; }

    /// <summary>For occurrence and "this and following" ops: the occurrence's original start.</summary>
    public DateTimeOffset? Occurrence { get; init; }

    /// <summary>The op's payload: the same body the matching REST endpoint takes.</summary>
    public JsonElement? Data { get; init; }
}

/// <summary>The op names. Payloads: events take <see cref="SaveEventRequest"/> (for
/// <see cref="EventFollowingUpdate"/> its <c>id</c> is the new series' id), tasks
/// <see cref="SaveTaskRequest"/>, categories <see cref="SaveCategoryRequest"/>; see the docs of
/// the rest.</summary>
public static class SyncOps
{
    public const string EventCreate = "event.create";
    public const string EventUpdate = "event.update";
    public const string EventDelete = "event.delete";
    public const string EventOccurrenceUpsert = "event.occurrence.upsert";
    public const string EventOccurrenceDelete = "event.occurrence.delete";
    public const string EventOccurrenceReset = "event.occurrence.reset";
    public const string EventFollowingUpdate = "event.following.update";
    public const string EventFollowingDelete = "event.following.delete";

    /// <summary>Payload <see cref="RsvpRequest"/>.</summary>
    public const string EventRsvp = "event.rsvp";

    public const string TaskCreate = "task.create";
    public const string TaskUpdate = "task.update";

    /// <summary>Payload <see cref="SetTaskDoneRequest"/>.</summary>
    public const string TaskDone = "task.done";

    public const string TaskDelete = "task.delete";

    /// <summary>Payload <see cref="SaveCalendarRequest"/>.</summary>
    public const string CalendarCreate = "calendar.create";

    /// <summary>Payload: any of <c>name</c>, <c>defaultCategoryId</c>, <c>defaultReminders</c>; only
    /// those present change.</summary>
    public const string CalendarUpdate = "calendar.update";

    public const string CalendarDelete = "calendar.delete";
    public const string CategoryCreate = "category.create";
    public const string CategoryUpdate = "category.update";
    public const string CategoryDelete = "category.delete";
}

/// <summary>Values of <see cref="SyncOpResult.Status"/>.</summary>
public static class SyncOpStatus
{
    /// <summary>Applied.</summary>
    public const string Ok = "ok";

    /// <summary>The server's copy moved on since <see cref="SyncOp.BaseVersion"/> (or, for a
    /// create, the id already exists): pull, and the server's copy stands.</summary>
    public const string Conflict = "conflict";

    /// <summary>It isn't there (any more). For a delete, that is the outcome wanted.</summary>
    public const string NotFound = "notFound";

    /// <summary>The change can't be made as it is; <see cref="SyncOpResult.Error"/> says why. Retrying won't help.</summary>
    public const string Invalid = "invalid";

    /// <summary>It's in a read-only (subscribed) calendar.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>Something went wrong on the server: try again later.</summary>
    public const string Error = "error";
}

/// <summary>
/// What became of one change. The new state itself comes with the next pull; <see cref="Versions"/>
/// (on success) gives the version each series or task it touched has now — the baseVersion for
/// changes to them still queued on the device. Within one push, a later change to the same thing
/// is sent on the same baseVersion as the first (or none, after a create in that push).
/// </summary>
public sealed record SyncOpResult(
    string OpId, string Status, string? Error = null, IReadOnlyDictionary<Guid, string>? Versions = null);

public sealed record SyncPushResponse(IReadOnlyList<SyncOpResult> Results);

/// <summary>The app's delta sync.</summary>
public interface ISyncService
{
    /// <summary>The next page of changes after <paramref name="cursor"/> (null: everything).</summary>
    Task<SyncPullResponse> PullAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken = default);

    Task<SyncPushResponse> PushAsync(Guid userId, SyncPushRequest request, CancellationToken cancellationToken = default);
}
