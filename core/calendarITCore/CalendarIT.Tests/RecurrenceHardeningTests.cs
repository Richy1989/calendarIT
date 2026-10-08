using System.Diagnostics;
using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// A repeat rule is outside input that is re-read on every page load and every reminder tick.
/// These pin that no rule — unparseable, never-matching, or absurdly dense — can break a
/// calendar or make one request do unbounded work.
/// </summary>
public sealed class RecurrenceHardeningTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EventService _events;
    private readonly Guid _userId = Guid.NewGuid();

    private static readonly DateTimeOffset Start = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    public RecurrenceHardeningTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "rr@test", NormalizedUserName = "RR@TEST" });
        _db.SaveChanges();
        _events = new EventService(_db, TimeProvider.System, new FakeInvitationMailer(),
            new InternalInvitationDelivery(_db, TimeProvider.System));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SaveEventRequest Request(string title, string? rrule = null, DateTimeOffset? start = null) => new()
    {
        Title = title,
        Start = start ?? Start,
        End = (start ?? Start).AddMinutes(30),
        Recurrence = rrule,
        TimeZone = "Europe/Berlin",
    };

    [Theory]
    [InlineData("FREQ=DAILY", true)]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=MO,WE", true)]
    [InlineData("FREQ=HOURLY;COUNT=5", true)]
    [InlineData("NOT-AN-RRULE", false)]
    [InlineData("FREQ=SECONDLY", false)]
    [InlineData("FREQ=MINUTELY;INTERVAL=5", false)]
    public void TryNormalize_AcceptsRealRules_RefusesBrokenAndSubHourly(string rule, bool ok)
    {
        Assert.Equal(ok, RecurrenceRules.TryNormalize(rule, out var normalized, out var error));
        Assert.Equal(ok, normalized is not null);
        Assert.Equal(ok, error is null);
    }

    [Fact]
    public async Task Create_WithUnparseableRule_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidInputException>(() => _events.CreateAsync(_userId, Request("Bad", "BOGUS")));
        Assert.Empty(_db.Events);
    }

    [Fact]
    public async Task Create_WithEndBeforeStart_IsRefused()
    {
        var request = new SaveEventRequest { Title = "Backwards", Start = Start, End = Start.AddHours(-1) };
        await Assert.ThrowsAsync<InvalidInputException>(() => _events.CreateAsync(_userId, request));
    }

    [Fact]
    public async Task StoredBrokenRule_DoesNotBreakTheCalendarOrSearch()
    {
        // A row written before rules were validated: it must not take the other events down.
        var good = await _events.CreateAsync(_userId, Request("Good"));
        var calendarId = good.CalendarId;
        _db.Events.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(), CalendarId = calendarId, Uid = "legacy", Title = "Legacy broken",
            StartUtc = Start.UtcDateTime, EndUtc = Start.UtcDateTime.AddHours(1), RRule = "BOGUS",
        });
        await _db.SaveChangesAsync();

        var listed = await _events.GetEventsAsync(_userId, Start.AddDays(-1), Start.AddDays(7));
        Assert.Contains(listed, e => e.Title == "Good");

        var found = await _events.SearchAsync(_userId, "o", 10);
        Assert.Contains(found, e => e.Title == "Good");
    }

    [Fact]
    public void Expand_NeverMatchingHourlyRule_GivesUpQuickly()
    {
        // February 30th never comes. Unbounded, Ical.Net walks to the year 9999 first — ~40s of
        // CPU for an hourly rule — before throwing.
        var sw = Stopwatch.StartNew();
        var hits = RecurrenceExpander.Expand(
            Start.UtcDateTime, Start.UtcDateTime.AddHours(1), "UTC", "FREQ=HOURLY;BYMONTH=2;BYMONTHDAY=30",
            new HashSet<DateTime>(), Start.UtcDateTime, Start.UtcDateTime.AddDays(60)).ToList();
        Assert.Empty(hits);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Expand_IsCapped_EvenForAHugeWindow()
    {
        var hits = RecurrenceExpander.Expand(
            Start.UtcDateTime, Start.UtcDateTime.AddMinutes(30), "UTC", "FREQ=HOURLY",
            new HashSet<DateTime>(), Start.UtcDateTime, Start.UtcDateTime.AddYears(50)).Count();
        Assert.Equal(RecurrenceExpander.MaxOccurrencesPerExpansion, hits);
    }

    [Fact]
    public void Expand_IncludesAnOccurrenceAlreadyRunningWhenTheWindowOpens()
    {
        // A weekly three-day event seen from its second day.
        var start = new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);
        var hits = RecurrenceExpander.Expand(
            start, start.AddDays(3), "UTC", "FREQ=WEEKLY;COUNT=2",
            new HashSet<DateTime>(), start.AddDays(1), start.AddDays(2)).ToList();
        Assert.Equal(start, Assert.Single(hits).StartUtc);
    }

    [Fact]
    public void Expand_AllDaySeries_MatchesExclusionsByDate()
    {
        // An exclusion stored at 01:00Z (a winter occurrence of a series created in summer, as
        // the web UI used to store it) still excludes that day's all-day occurrence.
        var start = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var excluded = new HashSet<DateTime> { new(2026, 10, 27, 1, 0, 0, DateTimeKind.Utc) };
        var hits = RecurrenceExpander.Expand(
            start, start, "Europe/Berlin", "FREQ=WEEKLY;COUNT=3", excluded, start.AddDays(-1), start.AddDays(30), allDay: true)
            .Select(o => o.StartUtc.Date).ToList();
        Assert.Equal([start.Date, start.AddDays(14).Date], hits);
    }

    // ---------------------------------------------------------------- rules the custom-repeat editor writes

    [Fact]
    public void EveryFourWeeks_KeepsItsLocalTimeAcrossTheDstChange()
    {
        // A shift pattern every 4 weeks, 07:00 in Berlin, crossing the October DST switch.
        var start = new DateTime(2026, 9, 7, 5, 0, 0, DateTimeKind.Utc); // 07:00 CEST
        var hits = RecurrenceExpander.Expand(
            start, start.AddHours(8), "Europe/Berlin", "FREQ=WEEKLY;INTERVAL=4;BYDAY=MO,TU",
            new HashSet<DateTime>(), start.AddDays(-1), start.AddDays(70)).Select(o => o.StartUtc).ToList();

        Assert.Equal(
            [start, start.AddDays(1), start.AddDays(28), start.AddDays(29),
             start.AddDays(56).AddHours(1), start.AddDays(57).AddHours(1)], // 07:00 CET = 06:00Z
            hits);
    }

    [Fact]
    public void EveryThreeDays_WithACount()
    {
        var hits = RecurrenceExpander.Expand(
            Start.UtcDateTime, Start.UtcDateTime.AddHours(1), "UTC", "FREQ=DAILY;INTERVAL=3;COUNT=4",
            new HashSet<DateTime>(), Start.UtcDateTime.AddDays(-1), Start.UtcDateTime.AddDays(60)).ToList();
        Assert.Equal([0, 3, 6, 9], hits.Select(o => (o.StartUtc - Start.UtcDateTime).Days));
    }

    [Fact]
    public void AllDaySeries_UntilADate_IncludesThatDate()
    {
        // All-day series carry UNTIL as a DATE (RFC 5545 requires it to match a DATE DTSTART).
        var start = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        var hits = RecurrenceExpander.Expand(
            start, start, "Europe/Berlin", "FREQ=WEEKLY;INTERVAL=2;UNTIL=20261102", new HashSet<DateTime>(),
            start.AddDays(-1), start.AddDays(90), allDay: true).Select(o => o.StartUtc.Date).ToList();
        Assert.Equal([start, start.AddDays(14), start.AddDays(28)], hits);
    }

    [Fact]
    public void MonthlyOnTheSecondTuesday()
    {
        var start = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc); // 2nd Tuesday of Oct 2026
        var hits = RecurrenceExpander.Expand(
            start, start.AddHours(1), "UTC", "FREQ=MONTHLY;BYDAY=2TU;COUNT=3", new HashSet<DateTime>(),
            start.AddDays(-1), start.AddDays(120)).Select(o => o.StartUtc.Date).ToList();
        Assert.Equal([new DateTime(2026, 10, 13), new DateTime(2026, 11, 10), new DateTime(2026, 12, 8)], hits);
    }

    [Theory]
    [InlineData("FREQ=WEEKLY;INTERVAL=4;BYDAY=MO,TU")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR;UNTIL=20271231T225959Z")]
    [InlineData("FREQ=YEARLY;INTERVAL=2;COUNT=5")]
    public void TheEditorsRules_AreAccepted(string rule) =>
        Assert.True(RecurrenceRules.TryNormalize(rule, out _, out _));

    // ---------------------------------------------------------------- single occurrences

    private async Task<List<EventDto>> WeekAsync() =>
        [.. await _events.GetEventsAsync(_userId, Start.AddDays(-1), Start.AddDays(7))];

    [Fact]
    public async Task EditingOneOccurrence_MovesOnlyThatOccurrence()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        var second = Start.AddDays(1);

        var moved = await _events.UpsertOccurrenceAsync(_userId, series.Id, second,
            Request("Standup (late)", start: second.AddHours(3)));

        Assert.NotNull(moved);
        Assert.Equal(series.Id, moved!.SeriesMasterId);
        Assert.Equal(second, moved.RecurrenceId);
        var week = await WeekAsync();
        Assert.Equal(5, week.Count);
        Assert.DoesNotContain(week, e => e.Start == second);
        Assert.Contains(week, e => e.Start == second.AddHours(3) && e.Title == "Standup (late)" && e.Recurring);
    }

    [Fact]
    public async Task EditingTheSameOccurrenceTwice_UpdatesOneOverride()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        var second = Start.AddDays(1);
        await _events.UpsertOccurrenceAsync(_userId, series.Id, second, Request("A", start: second.AddHours(1)));
        await _events.UpsertOccurrenceAsync(_userId, series.Id, second, Request("B", start: second.AddHours(2)));

        Assert.Single(_db.Events.AsNoTracking(), e => e.SeriesMasterId == series.Id);
        Assert.Contains(await WeekAsync(), e => e.Title == "B");
    }

    [Fact]
    public async Task EditingAnOccurrenceTheSeriesDoesNotHave_ReturnsNull()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        Assert.Null(await _events.UpsertOccurrenceAsync(_userId, series.Id, Start.AddHours(5), Request("x")));
        Assert.Null(await _events.UpsertOccurrenceAsync(_userId, series.Id, Start.AddDays(10), Request("x")));
    }

    [Fact]
    public async Task ResettingAnOccurrence_BringsBackTheSeriesVersion()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        var second = Start.AddDays(1);
        await _events.UpsertOccurrenceAsync(_userId, series.Id, second, Request("Moved", start: second.AddHours(3)));

        Assert.True(await _events.ResetOccurrenceAsync(_userId, series.Id, second));

        var week = await WeekAsync();
        Assert.Equal(5, week.Count);
        Assert.Contains(week, e => e.Start == second && e.Title == "Standup");
        Assert.DoesNotContain(week, e => e.Title == "Moved");
    }

    [Fact]
    public async Task ResettingADeletedOccurrence_RestoresIt()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        await _events.DeleteAsync(_userId, series.Id, Start.AddDays(2));
        Assert.Equal(4, (await WeekAsync()).Count);

        await _events.ResetOccurrenceAsync(_userId, series.Id, Start.AddDays(2));
        Assert.Equal(5, (await WeekAsync()).Count);
    }

    [Fact]
    public async Task DeletingAnEditedOccurrence_RemovesThatOccurrence()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        var second = Start.AddDays(1);
        var moved = await _events.UpsertOccurrenceAsync(_userId, series.Id, second, Request("Moved", start: second.AddHours(3)));

        Assert.True(await _events.DeleteAsync(_userId, moved!.Id, occurrence: null));

        var week = await WeekAsync();
        Assert.Equal(4, week.Count);
        Assert.DoesNotContain(week, e => e.Title == "Moved");
    }

    [Fact]
    public async Task DeletingAnOccurrenceBySeriesAndInstant_RemovesItsEditToo()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        var second = Start.AddDays(1);
        await _events.UpsertOccurrenceAsync(_userId, series.Id, second, Request("Moved", start: second.AddHours(3)));

        await _events.DeleteAsync(_userId, series.Id, second);

        Assert.Equal(4, (await WeekAsync()).Count);
        Assert.Empty(_db.Events.AsNoTracking().Where(e => e.SeriesMasterId == series.Id));
    }

    [Fact]
    public async Task ChangingTheSeriesRule_DropsEditedOccurrences()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        await _events.UpsertOccurrenceAsync(_userId, series.Id, Start.AddDays(1), Request("Moved", start: Start.AddDays(1).AddHours(3)));

        await _events.UpdateAsync(_userId, series.Id, Request("Standup", "FREQ=DAILY;COUNT=3"));

        Assert.Empty(_db.Events.AsNoTracking().Where(e => e.SeriesMasterId == series.Id));
        Assert.Equal(3, (await WeekAsync()).Count);
    }

    [Fact]
    public async Task RenamingTheSeries_KeepsEditedOccurrences()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        await _events.UpsertOccurrenceAsync(_userId, series.Id, Start.AddDays(1), Request("Moved", start: Start.AddDays(1).AddHours(3)));

        await _events.UpdateAsync(_userId, series.Id, Request("Daily standup", "FREQ=DAILY;COUNT=5"));

        var week = await WeekAsync();
        Assert.Contains(week, e => e.Title == "Moved");
        Assert.Equal(4, week.Count(e => e.Title == "Daily standup"));
    }

    [Fact]
    public async Task UpdatingAnEditedOccurrenceById_StaysAnOccurrence()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        var moved = await _events.UpsertOccurrenceAsync(_userId, series.Id, Start.AddDays(1), Request("Moved", start: Start.AddDays(1).AddHours(3)));

        // Even if the request tries to make it a series of its own.
        var updated = await _events.UpdateAsync(_userId, moved!.Id, Request("Moved again", "FREQ=DAILY", Start.AddDays(1).AddHours(4)));

        Assert.Equal(series.Id, updated!.SeriesMasterId);
        Assert.Null(updated.Recurrence);
        Assert.Equal(5, (await WeekAsync()).Count);
    }

    [Fact]
    public async Task DeletingTheSeries_DeletesItsEditedOccurrences()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=DAILY;COUNT=5"));
        await _events.UpsertOccurrenceAsync(_userId, series.Id, Start.AddDays(1), Request("Moved", start: Start.AddDays(1).AddHours(3)));

        await _events.DeleteAsync(_userId, series.Id, occurrence: null);

        Assert.Empty(_db.Events.AsNoTracking());
    }
}
