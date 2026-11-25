using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Application.Sync;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CalendarIT.Infrastructure.Sync;

/// <summary>
/// The app's delta sync (protocol 1). Pull works like CalDAV's sync-collection, over the same
/// facts: a change is a row whose <c>UpdatedAt</c> moved (every write path bumps it, and an edited
/// occurrence bumps its series), a deletion is a <see cref="DeletedResource"/> record. Push runs
/// each change through the same service the web app's endpoint uses, so validation, read-only
/// calendars and guests' invitations behave exactly as they do there.
/// </summary>
public sealed class SyncService(
    AppDbContext db,
    TimeProvider timeProvider,
    IEventService events,
    ITaskService tasks,
    ICalendarService calendars,
    ICategoryService categories,
    ILogger<SyncService> logger) : ISyncService
{
    public const int Protocol = 1;

    /// <summary>Changes from this long before a cursor are sent again: a write that committed late
    /// may carry an <c>UpdatedAt</c> from before the cursor was handed out. As CalDAV's.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    /// <summary>How long a cursor is honoured. Deletion records are pruned after 61 days
    /// (<c>MaintenanceJob</c>), so any cursor still accepted finds every deletion since.</summary>
    public static readonly TimeSpan CursorLifetime = TimeSpan.FromDays(60);

    public const int MinLimit = 50;
    public const int MaxLimit = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- pull

    public async Task<SyncPullResponse> PullAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        limit = Math.Clamp(limit, MinLimit, MaxLimit);

        var state = cursor is null ? null : SyncCursor.Parse(cursor);
        var reset = cursor is not null && (state is null || state.Since is { } s && s < now - CursorLifetime);
        if (reset || state is null)
        {
            state = new SyncCursor(Since: null, Issued: now, Phase: SyncCursor.EventsPhase, Position: null);
        }
        else if (state.Issued is null)
        {
            state = state with { Issued = now }; // a new listing begins: its start is the next cursor
        }
        var changedFrom = state.Since - Overlap;

        // A fresh account gets its first calendar here too, as on every other path in.
        if (!await db.Calendars.AnyAsync(c => c.OwnerUserId == userId, cancellationToken))
        {
            await DefaultCalendar.GetOrCreateAsync(db, timeProvider, userId, cancellationToken);
        }

        var budget = limit;
        var phase = state.Phase;
        var position = state.Position;
        var eventPage = new List<SyncSeriesDto>();
        var taskPage = new List<SyncTaskDto>();

        if (phase == SyncCursor.EventsPhase)
        {
            var masters = db.Events.AsNoTracking()
                .Where(e => e.Calendar!.OwnerUserId == userId && e.SeriesMasterId == null);
            if (changedFrom is { } from)
            {
                masters = masters.Where(e => e.UpdatedAt >= from);
            }
            var (ids, last, done) = await PageAsync(masters, e => e.UpdatedAt, e => e.Id, position, budget, cancellationToken);
            if (ids.Count > 0)
            {
                var rows = await db.Events.AsNoTracking()
                    .Include(e => e.Reminders)
                    .Include(e => e.Attendees)
                    .Include(e => e.Overrides).ThenInclude(o => o.Reminders)
                    .AsSplitQuery()
                    .Where(e => ids.Contains(e.Id))
                    .ToListAsync(cancellationToken);
                eventPage.AddRange(rows.OrderBy(e => e.UpdatedAt).Select(ToSeries));
            }
            budget -= ids.Count;
            if (done)
            {
                phase = SyncCursor.TasksPhase;
                position = null;
            }
            else
            {
                position = last;
            }
        }

        if (phase == SyncCursor.TasksPhase && budget > 0)
        {
            var own = db.Tasks.AsNoTracking().Where(t => t.Calendar!.OwnerUserId == userId);
            if (changedFrom is { } from)
            {
                own = own.Where(t => t.UpdatedAt >= from);
            }
            var (ids, last, done) = await PageAsync(own, t => t.UpdatedAt, t => t.Id, position, budget, cancellationToken);
            if (ids.Count > 0)
            {
                var rows = await db.Tasks.AsNoTracking().Include(t => t.Reminders)
                    .Where(t => ids.Contains(t.Id))
                    .ToListAsync(cancellationToken);
                taskPage.AddRange(rows.OrderBy(t => t.UpdatedAt).Select(ToTask));
            }
            if (done)
            {
                phase = SyncCursor.DonePhase;
                position = null;
            }
            else
            {
                position = last;
            }
        }

        var hasMore = phase != SyncCursor.DonePhase;
        var deleted = new SyncDeletions([], []);
        if (!hasMore && changedFrom is { } goneFrom)
        {
            deleted = await DeletionsAsync(userId, goneFrom, cancellationToken);
        }

        var next = hasMore
            ? state with { Phase = phase, Position = position }
            : new SyncCursor(Since: state.Issued, Issued: null, Phase: SyncCursor.EventsPhase, Position: null);

        return new SyncPullResponse(
            Protocol, reset, next.Format(), hasMore,
            await CalendarsAsync(userId, cancellationToken),
            await CategoriesAsync(userId, cancellationToken),
            eventPage, taskPage, deleted);
    }

    /// <summary>
    /// Ids of the next page, in <c>UpdatedAt</c> order after <paramref name="after"/>. A page never
    /// ends inside a run of rows sharing one <c>UpdatedAt</c> (an import writes thousands at one
    /// instant): the run is taken whole, so "after the last one" can't skip any of it. That keeps
    /// the position a plain timestamp, the same in SQLite and Postgres.
    /// </summary>
    private static async Task<(List<Guid> Ids, DateTime? Last, bool Done)> PageAsync<T>(
        IQueryable<T> rows,
        System.Linq.Expressions.Expression<Func<T, DateTime>> updatedAt,
        System.Linq.Expressions.Expression<Func<T, Guid>> id,
        DateTime? after,
        int budget,
        CancellationToken cancellationToken) where T : class
    {
        if (after is { } a)
        {
            rows = rows.Where(Greater(updatedAt, a));
        }
        var head = await rows.OrderBy(updatedAt)
            .Select(r => new Stamp { Id = EF.Property<Guid>(r, "Id"), At = EF.Property<DateTime>(r, "UpdatedAt") })
            .Take(budget)
            .ToListAsync(cancellationToken);
        if (head.Count < budget)
        {
            return (head.Select(h => h.Id).ToList(), head.Count > 0 ? head[^1].At : after, Done: true);
        }
        var last = head[^1].At;
        var ids = head.Select(h => h.Id).ToHashSet();
        var tail = await rows.Where(Equal(updatedAt, last)).Select(id).ToListAsync(cancellationToken);
        ids.UnionWith(tail);
        return (ids.ToList(), last, Done: false);
    }

    private sealed class Stamp
    {
        public Guid Id { get; init; }

        public DateTime At { get; init; }
    }

    private static System.Linq.Expressions.Expression<Func<T, bool>> Greater<T>(
        System.Linq.Expressions.Expression<Func<T, DateTime>> member, DateTime value) =>
        System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
            System.Linq.Expressions.Expression.GreaterThan(member.Body, Constant(value)), member.Parameters);

    private static System.Linq.Expressions.Expression<Func<T, bool>> Equal<T>(
        System.Linq.Expressions.Expression<Func<T, DateTime>> member, DateTime value) =>
        System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
            System.Linq.Expressions.Expression.Equal(member.Body, Constant(value)), member.Parameters);

    /// <summary>A value EF sends as a parameter (a closure member), not inlined as a literal.</summary>
    private static System.Linq.Expressions.Expression Constant(DateTime value)
    {
        var holder = new { Value = value };
        return System.Linq.Expressions.Expression.Property(
            System.Linq.Expressions.Expression.Constant(holder), nameof(holder.Value));
    }

    /// <summary>Events and tasks gone from the user's calendars since <paramref name="from"/>. A record
    /// whose row is still there — moved to another of the user's calendars, or deleted and written
    /// again under the same id — is not a deletion.</summary>
    private async Task<SyncDeletions> DeletionsAsync(Guid userId, DateTime from, CancellationToken cancellationToken)
    {
        var calendarIds = db.Calendars.Where(c => c.OwnerUserId == userId).Select(c => c.Id);
        var gone = await db.DeletedResources.AsNoTracking()
            .Where(d => d.EntityId != null && d.DeletedAtUtc >= from && calendarIds.Contains(d.CalendarId))
            .Select(d => new { d.Kind, Id = d.EntityId!.Value })
            .Distinct()
            .ToListAsync(cancellationToken);
        var eventIds = gone.Where(g => g.Kind == DeletedResourceKinds.Event).Select(g => g.Id).ToList();
        var taskIds = gone.Where(g => g.Kind == DeletedResourceKinds.Task).Select(g => g.Id).ToList();
        if (eventIds.Count > 0)
        {
            var present = await db.Events.AsNoTracking()
                .Where(e => eventIds.Contains(e.Id) && e.Calendar!.OwnerUserId == userId)
                .Select(e => e.Id)
                .ToListAsync(cancellationToken);
            eventIds = eventIds.Except(present).ToList();
        }
        if (taskIds.Count > 0)
        {
            var present = await db.Tasks.AsNoTracking()
                .Where(t => taskIds.Contains(t.Id) && t.Calendar!.OwnerUserId == userId)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
            taskIds = taskIds.Except(present).ToList();
        }
        return new SyncDeletions(eventIds, taskIds);
    }

    private async Task<IReadOnlyList<SyncCalendarDto>> CalendarsAsync(Guid userId, CancellationToken cancellationToken) =>
        (await db.Calendars.AsNoTracking()
            .Where(c => c.OwnerUserId == userId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken))
        .Select(c => new SyncCalendarDto(
            c.Id, c.Name, c.Color, c.TimeZoneId, c.DefaultCategoryId, DefaultReminders.Parse(c.DefaultReminders),
            ReadOnly: c.SourceUrl is not null, Version: VersionOf(c.UpdatedAt)))
        .ToList();

    private async Task<IReadOnlyList<SyncCategoryDto>> CategoriesAsync(Guid userId, CancellationToken cancellationToken) =>
        (await db.Categories.AsNoTracking()
            .Where(c => c.OwnerUserId == userId)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken))
        .Select(c => new SyncCategoryDto(c.Id, c.Name, c.Color, VersionOf(c.UpdatedAt)))
        .ToList();

    /// <summary>A row's version: its <c>UpdatedAt</c> in microseconds — the precision Postgres keeps,
    /// as CalDAV ETags.</summary>
    public static string VersionOf(DateTime updatedAt) =>
        (updatedAt.Ticks / 10).ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private static IReadOnlyList<ReminderDto> Reminders(IEnumerable<Reminder> reminders) =>
        reminders.OrderBy(r => r.MinutesBefore).Select(r => new ReminderDto(r.MinutesBefore, r.Channel.ToString())).ToList();

    private static SyncSeriesDto ToSeries(CalendarEvent e) => new(
        e.Id, e.CalendarId, e.Title, e.Description, e.Location, e.CategoryId, e.Color,
        Utc(e.StartUtc), Utc(e.EndUtc), e.IsAllDay, e.TimeZoneId, e.RRule,
        ExDates.Parse(e.ExDates).Order().Select(Utc).ToList(),
        Reminders(e.Reminders),
        e.Attendees.OrderBy(a => a.Email, StringComparer.OrdinalIgnoreCase)
            .Select(a => new AttendeeDto(a.Email, a.Name, a.Status.ToString())).ToList(),
        e.InvitationStatus?.ToString(), e.OrganizerEmail,
        e.Overrides.OrderBy(o => o.RecurrenceIdUtc)
            .Select(o => new SyncOccurrenceDto(
                o.Id, Utc(o.RecurrenceIdUtc!.Value), o.Title, o.Description, o.Location, o.CategoryId,
                Utc(o.StartUtc), Utc(o.EndUtc), o.IsAllDay, o.TimeZoneId, Reminders(o.Reminders)))
            .ToList(),
        VersionOf(e.UpdatedAt));

    private static SyncTaskDto ToTask(TaskItem t) => new(
        t.Id, t.CalendarId, t.Title, t.Notes, Utc(t.DueUtc), t.DueAllDay, t.TimeZoneId, t.Status.ToString(),
        Utc(t.CompletedAtUtc), TaskService.PriorityName(t.Priority), t.ParentId, t.SortOrder,
        t.Reminders.OrderBy(r => r.MinutesBefore).Select(r => new ReminderDto(r.MinutesBefore, r.Channel.ToString())).ToList(),
        VersionOf(t.UpdatedAt));

    // ---------------------------------------------------------------- push

    public async Task<SyncPushResponse> PushAsync(Guid userId, SyncPushRequest request, CancellationToken cancellationToken = default)
    {
        var results = new List<SyncOpResult>(request.Ops.Count);
        foreach (var op in request.Ops)
        {
            SyncOpResult result;
            try
            {
                result = new SyncOpResult(op.OpId, await ApplyAsync(userId, op, cancellationToken));
            }
            catch (InvalidInputException ex) when (ex.Message == EventService.SubscribedCalendarMessage)
            {
                result = new SyncOpResult(op.OpId, SyncOpStatus.Forbidden, ex.Message);
            }
            catch (InvalidInputException ex)
            {
                result = new SyncOpResult(op.OpId, SyncOpStatus.Invalid, ex.Message);
            }
            catch (JsonException)
            {
                result = new SyncOpResult(op.OpId, SyncOpStatus.Invalid, "The change's data couldn't be read.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One change that breaks must not hold up the rest; the device tries it again later.
                logger.LogError(ex, "Sync change {Op} failed", op.Op);
                result = new SyncOpResult(op.OpId, SyncOpStatus.Error, "The server couldn't apply this change. It will be tried again.");
            }
            finally
            {
                // Each change starts from the database, not from what the one before left tracked
                // (a failed save leaves its rows behind in the tracker).
                db.ChangeTracker.Clear();
            }
            results.Add(result);
        }
        return new SyncPushResponse(results);
    }

    private async Task<string> ApplyAsync(Guid userId, SyncOp op, CancellationToken ct)
    {
        switch (op.Op)
        {
            case SyncOps.EventCreate:
                if (await OwnsEventAsync(userId, op.Id, ct))
                {
                    return SyncOpStatus.Conflict; // sent again after a lost answer, or a clash
                }
                await events.CreateAsync(userId, Read<SaveEventRequest>(op, withId: op.Id), ct);
                return SyncOpStatus.Ok;

            case SyncOps.EventUpdate:
                return await WithEventVersionAsync(userId, op, recurringOnly: false, ct, async () =>
                    await events.UpdateAsync(userId, op.Id, Read<SaveEventRequest>(op), ct) is not null);

            case SyncOps.EventDelete:
                return await WithEventVersionAsync(userId, op, recurringOnly: false, ct, () =>
                    events.DeleteAsync(userId, op.Id, occurrence: null, ct));

            case SyncOps.EventOccurrenceUpsert:
                return await WithEventVersionAsync(userId, op, recurringOnly: true, ct, async () =>
                    await events.UpsertOccurrenceAsync(userId, op.Id, Occurrence(op), Read<SaveEventRequest>(op), ct) is not null);

            case SyncOps.EventOccurrenceDelete:
                return await WithEventVersionAsync(userId, op, recurringOnly: true, ct, () =>
                    events.DeleteAsync(userId, op.Id, Occurrence(op), ct));

            case SyncOps.EventOccurrenceReset:
                return await WithEventVersionAsync(userId, op, recurringOnly: true, ct, () =>
                    events.ResetOccurrenceAsync(userId, op.Id, Occurrence(op), ct));

            case SyncOps.EventFollowingUpdate:
                return await WithEventVersionAsync(userId, op, recurringOnly: true, ct, async () =>
                    await events.SplitSeriesAsync(userId, op.Id, Occurrence(op), Read<SaveEventRequest>(op), ct) is not null);

            case SyncOps.EventFollowingDelete:
                return await WithEventVersionAsync(userId, op, recurringOnly: true, ct, () =>
                    events.DeleteFollowingAsync(userId, op.Id, Occurrence(op), ct));

            case SyncOps.EventRsvp:
            {
                if (!await OwnsEventAsync(userId, op.Id, ct))
                {
                    return SyncOpStatus.NotFound;
                }
                var rsvp = Read<RsvpRequest>(op);
                return await events.RespondToInvitationAsync(userId, op.Id, rsvp.Status, ct) is not null
                    ? SyncOpStatus.Ok
                    : throw new InvalidInputException("Only a received invitation can be answered, with Accepted, Declined or Tentative.");
            }

            case SyncOps.TaskCreate:
                if (await db.Tasks.AnyAsync(t => t.Id == op.Id && t.Calendar!.OwnerUserId == userId, ct))
                {
                    return SyncOpStatus.Conflict;
                }
                await tasks.CreateAsync(userId, Read<SaveTaskRequest>(op, withId: op.Id), ct);
                return SyncOpStatus.Ok;

            case SyncOps.TaskUpdate:
                return await WithTaskVersionAsync(userId, op, ct, async () =>
                    await tasks.UpdateAsync(userId, op.Id, Read<SaveTaskRequest>(op), ct) is not null);

            case SyncOps.TaskDone:
                return await WithTaskVersionAsync(userId, op, ct, async () =>
                    await tasks.SetDoneAsync(userId, op.Id, Read<SetTaskDoneRequest>(op).Done, ct) is not null);

            case SyncOps.TaskDelete:
                return await WithTaskVersionAsync(userId, op, ct, () => tasks.DeleteAsync(userId, op.Id, ct));

            // Calendars and categories are few, small and rarely edited at once on two devices:
            // the last change wins, without versions.
            case SyncOps.CalendarCreate:
                if (await db.Calendars.AnyAsync(c => c.Id == op.Id && c.OwnerUserId == userId, ct))
                {
                    return SyncOpStatus.Conflict;
                }
                await calendars.CreateAsync(userId, Read<SaveCalendarRequest>(op, withId: op.Id), ct);
                return SyncOpStatus.Ok;

            case SyncOps.CalendarUpdate:
                return await UpdateCalendarAsync(userId, op, ct);

            case SyncOps.CalendarDelete:
                return await calendars.DeleteAsync(userId, op.Id, ct) switch
                {
                    DeleteCalendarResult.Deleted => SyncOpStatus.Ok,
                    DeleteCalendarResult.NotFound => SyncOpStatus.NotFound,
                    _ => throw new InvalidInputException("Every account keeps at least one calendar of its own."),
                };

            case SyncOps.CategoryCreate:
                if (await db.Categories.AnyAsync(c => c.Id == op.Id && c.OwnerUserId == userId, ct))
                {
                    return SyncOpStatus.Conflict;
                }
                return CategoryStatus(await categories.CreateAsync(userId, Read<SaveCategoryRequest>(op, withId: op.Id), ct));

            case SyncOps.CategoryUpdate:
                return CategoryStatus(await categories.UpdateAsync(userId, op.Id, Read<SaveCategoryRequest>(op), ct));

            case SyncOps.CategoryDelete:
                return await categories.DeleteAsync(userId, op.Id, ct) ? SyncOpStatus.Ok : SyncOpStatus.NotFound;

            default:
                throw new InvalidInputException($"Unknown change \"{op.Op}\".");
        }
    }

    private Task<bool> OwnsEventAsync(Guid userId, Guid id, CancellationToken ct) =>
        db.Events.AnyAsync(e => e.Id == id && e.Calendar!.OwnerUserId == userId, ct);

    /// <summary>
    /// Runs an event change if the series it touches is still at <see cref="SyncOp.BaseVersion"/>.
    /// The version is the series' even when <see cref="SyncOp.Id"/> names one of its edited
    /// occurrences: an edit to any occurrence moves the series' version, as it moves its ETag.
    /// </summary>
    private async Task<string> WithEventVersionAsync(
        Guid userId, SyncOp op, bool recurringOnly, CancellationToken ct, Func<Task<bool>> apply)
    {
        var row = await db.Events.AsNoTracking()
            .Where(e => e.Id == op.Id && e.Calendar!.OwnerUserId == userId)
            .Select(e => new { e.SeriesMasterId, e.UpdatedAt, e.RRule })
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return SyncOpStatus.NotFound;
        }
        if (recurringOnly && (row.SeriesMasterId is not null || row.RRule is null))
        {
            throw new InvalidInputException("This change is for a repeating series: its id must be the series'.");
        }
        var version = row.SeriesMasterId is { } masterId
            ? await db.Events.AsNoTracking().Where(e => e.Id == masterId).Select(e => e.UpdatedAt).SingleAsync(ct)
            : row.UpdatedAt;
        if (Conflicts(op, version))
        {
            return SyncOpStatus.Conflict;
        }
        return await apply() ? SyncOpStatus.Ok : SyncOpStatus.NotFound;
    }

    private async Task<string> WithTaskVersionAsync(Guid userId, SyncOp op, CancellationToken ct, Func<Task<bool>> apply)
    {
        var version = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == op.Id && t.Calendar!.OwnerUserId == userId)
            .Select(t => (DateTime?)t.UpdatedAt)
            .SingleOrDefaultAsync(ct);
        if (version is null)
        {
            return SyncOpStatus.NotFound;
        }
        if (Conflicts(op, version.Value))
        {
            return SyncOpStatus.Conflict;
        }
        return await apply() ? SyncOpStatus.Ok : SyncOpStatus.NotFound;
    }

    private static bool Conflicts(SyncOp op, DateTime current)
    {
        if (string.IsNullOrWhiteSpace(op.BaseVersion))
        {
            throw new InvalidInputException("A change to an existing event or task needs its baseVersion.");
        }
        return op.BaseVersion.Trim() != VersionOf(current);
    }

    private async Task<string> UpdateCalendarAsync(Guid userId, SyncOp op, CancellationToken ct)
    {
        if (!await db.Calendars.AnyAsync(c => c.Id == op.Id && c.OwnerUserId == userId, ct))
        {
            return SyncOpStatus.NotFound;
        }
        if (op.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            throw new InvalidInputException("This change needs its data.");
        }
        if (Property(data, "name") is { } name)
        {
            var rename = new SaveCalendarRequest { Name = name.GetString() ?? string.Empty };
            Validate(rename);
            await calendars.RenameAsync(userId, op.Id, rename, ct);
        }
        if (Property(data, "defaultCategoryId") is { } category)
        {
            Guid? categoryId = category.ValueKind == JsonValueKind.Null ? null : category.GetGuid();
            await calendars.SetDefaultCategoryAsync(userId, op.Id, categoryId, ct);
        }
        if (Property(data, "defaultReminders") is { } reminders)
        {
            var set = new SetCalendarRemindersRequest
            {
                Reminders = reminders.Deserialize<List<ReminderInput>>(Json) ?? [],
            };
            Validate(set);
            await calendars.SetDefaultRemindersAsync(userId, op.Id, set.Reminders, ct);
        }
        return SyncOpStatus.Ok;
    }

    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }
        return null;
    }

    private static string CategoryStatus(CategorySaveOutcome outcome) => outcome.Status switch
    {
        CategorySaveStatus.Saved => SyncOpStatus.Ok,
        CategorySaveStatus.NotFound => SyncOpStatus.NotFound,
        _ => throw new InvalidInputException("A category with that name already exists."),
    };

    private static DateTimeOffset Occurrence(SyncOp op) =>
        op.Occurrence ?? throw new InvalidInputException("This change needs the occurrence it is about.");

    /// <summary>The op's payload as the request type the matching REST endpoint takes, validated as
    /// model binding would. <paramref name="withId"/> sets the id a create takes from the op.</summary>
    private static T Read<T>(SyncOp op, Guid? withId = null) where T : class
    {
        if (op.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            throw new InvalidInputException("This change needs its data.");
        }
        T? value;
        if (withId is { } id)
        {
            if (id == Guid.Empty)
            {
                throw new InvalidInputException("A new item needs its id.");
            }
            var node = JsonNode.Parse(data.GetRawText())!.AsObject();
            foreach (var key in node.Select(p => p.Key).Where(k => string.Equals(k, "id", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                node.Remove(key);
            }
            node["id"] = id.ToString();
            value = node.Deserialize<T>(Json);
        }
        else
        {
            value = data.Deserialize<T>(Json);
        }
        if (value is null)
        {
            throw new InvalidInputException("This change needs its data.");
        }
        Validate(value);
        return value;
    }

    /// <summary>Data annotations on the object and on the items of its lists (reminders, guests).</summary>
    private static void Validate(object value)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value, new ValidationContext(value), errors, validateAllProperties: true))
        {
            throw new InvalidInputException(errors[0].ErrorMessage ?? "The change isn't valid.");
        }
        foreach (var property in value.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length == 0
                && property.GetValue(value) is IEnumerable items and not string)
            {
                foreach (var item in items)
                {
                    if (item is not null && item.GetType().IsClass && item is not string)
                    {
                        Validate(item);
                    }
                }
            }
        }
    }
}

/// <summary>
/// Where a pull stands, handed to the client as an opaque string. Between pulls it is just
/// <see cref="Since"/> (when the last listing began); while a listing is paged it also carries
/// when the listing began (<see cref="Issued"/>, the next <c>Since</c>), the phase (events, then
/// tasks) and the <c>UpdatedAt</c> reached in it.
/// </summary>
internal sealed record SyncCursor(DateTime? Since, DateTime? Issued, char Phase, DateTime? Position)
{
    public const char EventsPhase = 'e';
    public const char TasksPhase = 't';
    public const char DonePhase = 'd';

    private const string Version = "1";

    public string Format()
    {
        var raw = string.Join(':', Version, Ticks(Since), Ticks(Issued), Phase, Ticks(Position));
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The cursor, or null when it isn't one this server handed out.</summary>
    public static SyncCursor? Parse(string value)
    {
        try
        {
            var b64 = value.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(b64)).Split(':');
            if (parts.Length != 5 || parts[0] != Version || parts[3].Length != 1
                || parts[3][0] is not (EventsPhase or TasksPhase))
            {
                return null;
            }
            var since = FromTicks(parts[1]);
            var issued = FromTicks(parts[2]);
            var position = FromTicks(parts[4]);
            // Between pulls there is no listing in progress; within one, it has a start.
            if (issued is null && (parts[3][0] != EventsPhase || position is not null))
            {
                return null;
            }
            return new SyncCursor(since, issued, parts[3][0], position);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static string Ticks(DateTime? value) =>
        value is { } v ? v.Ticks.ToString(CultureInfo.InvariantCulture) : "-";

    private static DateTime? FromTicks(string value) =>
        value == "-" ? null : new DateTime(long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture), DateTimeKind.Utc);
}
