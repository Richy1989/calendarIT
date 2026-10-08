using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using DomainEvent = CalendarIT.Domain.CalendarEvent;
using ICalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Writes one iCalendar resource — a series master plus the overrides (VEVENTs with a
/// RECURRENCE-ID) that share its UID — onto the database, the same way for every way a series
/// arrives: a CalDAV PUT, an .ics import, an emailed invitation.
///
/// <para>A resource is written whole. Overrides the incoming resource no longer carries are
/// removed, and the master's suppressed instants (<see cref="DomainEvent.ExDates"/>) are rebuilt
/// from its exclusions plus the overrides it does carry — so a reverted occurrence comes back at
/// its normal time instead of vanishing. Picking "the first VEVENT" instead, as the CalDAV
/// handler once did, meant a resource listing an override first replaced the entire series with
/// that one occurrence.</para>
/// </summary>
public sealed class SeriesWriter(AppDbContext db)
{
    /// <summary>The target calendar's default category for the write in progress.</summary>
    private Guid? _inherited;

    /// <summary>Overrides kept per series; beyond this a resource is malformed or hostile.</summary>
    public const int MaxOverrides = 1_000;

    /// <summary>
    /// Separates a resource's VEVENTs into the master (no RECURRENCE-ID) and its overrides. A
    /// resource made only of overrides — legal, e.g. an invitation to a single instance — has no
    /// master; the caller decides what that means. Only VEVENTs with the master's UID count.
    /// </summary>
    public static (ICalEvent? Master, List<ICalEvent> Overrides) Split(IEnumerable<ICalEvent> events)
    {
        var withStart = events.Where(e => e.Start is not null).ToList();
        var master = withStart.FirstOrDefault(e => e.RecurrenceIdentifier is null);
        var uid = master?.Uid ?? withStart.FirstOrDefault()?.Uid;
        var overrides = withStart
            .Where(e => e.RecurrenceIdentifier is not null && string.Equals(e.Uid, uid, StringComparison.Ordinal))
            .ToList();
        return (master, overrides);
    }

    /// <summary>
    /// Creates (when <paramref name="existing"/> is null) or updates a series from its VEVENTs.
    /// <paramref name="existing"/> must be tracked with <c>Reminders</c>, <c>Overrides</c> and the
    /// overrides' <c>Reminders</c> loaded. Changes are staged, not saved.
    /// </summary>
    /// <param name="replaceAlarms">When true (a CalDAV client, an import), VALARMs on an incoming
    /// VEVENT replace that row's reminders; an incoming VEVENT with no VALARMs keeps them — many
    /// clients drop alarms they don't manage. Invitations pass false: someone else's alarms
    /// aren't ours.</param>
    public DomainEvent Write(
        DomainEvent? existing,
        ICalEvent masterVe,
        IReadOnlyList<ICalEvent> overrideVes,
        Guid calendarId,
        string uid,
        DateTime now,
        IReadOnlyList<Category>? categories,
        bool replaceAlarms,
        Guid? inheritedCategoryId = null)
    {
        _inherited = inheritedCategoryId;
        DomainEvent master;
        var previousOverrides = new HashSet<DateTime>();
        if (existing is null)
        {
            master = ICalEventMapper.FromICalEvent(masterVe, calendarId, uid, now, categories, inheritedCategoryId);
            if (replaceAlarms)
            {
                master.Reminders = ICalEventMapper.ReadReminders(masterVe);
            }
            db.Events.Add(master);
        }
        else
        {
            master = existing;
            previousOverrides = master.Overrides
                .Where(o => o.RecurrenceIdUtc is not null)
                .Select(o => ExDates.TruncateToSeconds(o.RecurrenceIdUtc!.Value))
                .ToHashSet();
            ICalEventMapper.Apply(masterVe, master, now, categories, inheritedCategoryId);
            if (replaceAlarms)
            {
                ReplaceReminders(master, masterVe);
            }
        }

        // A one-off can't have overrides; anything still attached belonged to a series that
        // stopped repeating.
        var incoming = master.RRule is null
            ? []
            : overrideVes
                .Select(ve => (Ve: ve, Rid: ICalEventMapper.ReadRecurrenceIdUtc(ve)))
                .Where(x => x.Rid is not null)
                .DistinctBy(x => x.Rid)
                .Take(MaxOverrides)
                .ToList();

        // Suppressed instants = the series' own exclusions + the instants overrides replace. The
        // mapper kept the stored list when the VEVENT carried no EXDATE, so the previous
        // overrides' instants are taken out first: they stay suppressed only if still overridden.
        var suppressed = ExDates.Parse(master.ExDates);
        if (ICalEventMapper.ReadExDates(masterVe).Count == 0)
        {
            suppressed.ExceptWith(previousOverrides);
        }
        suppressed.UnionWith(incoming.Select(x => x.Rid!.Value));
        master.ExDates = master.RRule is null || suppressed.Count == 0 ? null : ExDates.Format(suppressed.Order());

        var leftover = UpsertOverrides(master, incoming, now, categories, replaceAlarms);

        // Whatever is left was dropped from the resource.
        foreach (var gone in leftover)
        {
            master.Overrides.Remove(gone);
            db.Events.Remove(gone);
        }

        master.UpdatedAt = now;
        return master;
    }

    /// <summary>
    /// Adds or updates overrides on an existing series without touching the rest of it — what a
    /// single-instance invitation update carries. Their instants are suppressed on the master.
    /// </summary>
    public void UpsertOverrides(
        DomainEvent master, IEnumerable<ICalEvent> overrideVes, DateTime now, IReadOnlyList<Category>? categories, bool replaceAlarms)
    {
        if (master.RRule is null)
        {
            return;
        }
        var incoming = overrideVes
            .Select(ve => (Ve: ve, Rid: ICalEventMapper.ReadRecurrenceIdUtc(ve)))
            .Where(x => x.Rid is not null)
            .DistinctBy(x => x.Rid)
            .Take(MaxOverrides)
            .ToList();
        if (incoming.Count == 0)
        {
            return;
        }
        var suppressed = ExDates.Parse(master.ExDates);
        suppressed.UnionWith(incoming.Select(x => x.Rid!.Value));
        master.ExDates = ExDates.Format(suppressed.Order());
        UpsertOverrides(master, incoming, now, categories, replaceAlarms);
        master.UpdatedAt = now;
    }

    /// <summary>Applies each incoming override to its row (created when new) and returns the
    /// existing override rows no incoming VEVENT matched.</summary>
    private List<DomainEvent> UpsertOverrides(
        DomainEvent master, List<(ICalEvent Ve, DateTime? Rid)> incoming, DateTime now,
        IReadOnlyList<Category>? categories, bool replaceAlarms)
    {
        var byRid = master.Overrides
            .Where(o => o.RecurrenceIdUtc is not null)
            .GroupBy(o => ExDates.TruncateToSeconds(o.RecurrenceIdUtc!.Value))
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var (ve, rid) in incoming)
        {
            if (!byRid.Remove(rid!.Value, out var row))
            {
                row = new DomainEvent
                {
                    Id = Guid.NewGuid(),
                    CalendarId = master.CalendarId,
                    Uid = master.Uid,
                    SeriesMasterId = master.Id,
                    RecurrenceIdUtc = rid,
                    CreatedAt = now,
                };
                ICalEventMapper.Apply(ve, row, now, categories, _inherited);
                if (replaceAlarms)
                {
                    row.Reminders = ICalEventMapper.ReadReminders(ve);
                }
                // Added explicitly before it joins the navigation: it carries a preset key, and
                // EF would otherwise take a discovered row for an existing one.
                db.Events.Add(row);
                EventService.AddOnce(master.Overrides, row);
            }
            else
            {
                ICalEventMapper.Apply(ve, row, now, categories, _inherited);
                if (replaceAlarms)
                {
                    ReplaceReminders(row, ve);
                }
            }
            InheritFromMaster(row, master);
        }
        return [.. byRid.Values];
    }

    /// <summary>What an override takes from its series rather than from its own VEVENT: where it
    /// lives and, for a received invitation, whose it is and how the user answered.</summary>
    public static void InheritFromMaster(DomainEvent row, DomainEvent master)
    {
        row.CalendarId = master.CalendarId;
        row.Uid = master.Uid;
        row.RRule = null;
        row.ExDates = null;
        row.InvitationStatus = master.InvitationStatus;
        row.OrganizerEmail = master.OrganizerEmail;
        row.SourceOrganizerUserId = master.SourceOrganizerUserId;
        row.Sequence = master.Sequence;
    }

    /// <summary>Client VALARMs become the row's reminder set — only when the client sent some.</summary>
    private void ReplaceReminders(DomainEvent row, ICalEvent ve)
    {
        if (ve.Alarms.Count == 0)
        {
            return;
        }
        foreach (var old in row.Reminders.ToList())
        {
            row.Reminders.Remove(old);
            db.Reminders.Remove(old);
        }
        foreach (var reminder in ICalEventMapper.ReadReminders(ve))
        {
            reminder.EventId = row.Id;
            db.Reminders.Add(reminder);
            EventService.AddOnce(row.Reminders, reminder);
        }
    }
}
