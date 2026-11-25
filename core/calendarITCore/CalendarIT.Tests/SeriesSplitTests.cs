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
/// "This and following": a series split in two at one occurrence — what each half keeps, and
/// what guests are told.
/// </summary>
public sealed class SeriesSplitTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly FakeInvitationMailer _mailer = new();
    private readonly EventService _events;
    private readonly Guid _userId = Guid.NewGuid();

    // Mondays at 09:00 in Berlin, from 3 Aug 2026 (07:00Z in summer).
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Fourth = Start.AddDays(21); // 24 Aug

    public SeriesSplitTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "split@test", NormalizedUserName = "SPLIT@TEST" });
        _db.SaveChanges();
        _events = new EventService(_db, TimeProvider.System, _mailer, new InternalInvitationDelivery(_db, TimeProvider.System));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SaveEventRequest Request(
        string title, string? rule, DateTimeOffset start, bool allDay = false, IReadOnlyList<AttendeeInput>? guests = null) => new()
    {
        Title = title,
        Start = start,
        End = allDay ? start : start.AddHours(1),
        AllDay = allDay,
        Recurrence = rule,
        TimeZone = allDay ? null : "Europe/Berlin",
        Attendees = guests,
        Reminders = [new ReminderInput { MinutesBefore = 15, Channel = "Email" }],
    };

    private async Task<List<DateTimeOffset>> StartsAsync(string title) =>
        (await _events.GetEventsAsync(_userId, Start.AddDays(-1), Start.AddDays(70)))
            .Where(e => e.Title == title)
            .Select(e => e.Start)
            .Order()
            .ToList();

    [Fact]
    public async Task Splitting_EndsTheOldSeries_AndStartsANewOne_There()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=WEEKLY", Start));

        var next = await _events.SplitSeriesAsync(_userId, series.Id, Fourth, Request("Standup (new room)", "FREQ=WEEKLY", Fourth));

        Assert.NotNull(next);
        Assert.NotEqual(series.Id, next.Id);
        Assert.Equal([Start, Start.AddDays(7), Start.AddDays(14)], await StartsAsync("Standup"));
        var later = await StartsAsync("Standup (new room)");
        Assert.Equal(Fourth, later[0]);
        Assert.Equal(7, later.Count); // through 5 Oct, the end of the window
        var uids = _db.Events.AsNoTracking().Select(e => e.Uid).Distinct().Count();
        Assert.Equal(2, uids); // its own UID: a separate resource for phones and guests
        Assert.Single(_db.Reminders.AsNoTracking().Where(r => r.EventId == next.Id));
    }

    [Fact]
    public async Task ACount_IsSharedOut()
    {
        var series = await _events.CreateAsync(_userId, Request("Course", "FREQ=WEEKLY;COUNT=6", Start));

        await _events.SplitSeriesAsync(_userId, series.Id, Fourth, Request("Course", "FREQ=WEEKLY;COUNT=6", Fourth));

        Assert.Equal(6, (await StartsAsync("Course")).Count); // 3 before, 3 after — not 3 + 6
        Assert.Contains(_db.Events.AsNoTracking(), e => e.RRule == "FREQ=WEEKLY;COUNT=3");
    }

    [Fact]
    public async Task ExclusionsAndEditedOccurrences_GoWithTheirHalf()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=WEEKLY", Start));
        await _events.DeleteAsync(_userId, series.Id, Start.AddDays(7));            // before the split
        await _events.DeleteAsync(_userId, series.Id, Start.AddDays(35));           // after it
        await _events.UpsertOccurrenceAsync(_userId, series.Id, Start.AddDays(28),   // after it, edited
            Request("Standup (moved)", null, Start.AddDays(28).AddHours(2)));

        var next = await _events.SplitSeriesAsync(_userId, series.Id, Fourth, Request("Standup", "FREQ=WEEKLY", Fourth));

        var starts = await StartsAsync("Standup");
        Assert.DoesNotContain(Start.AddDays(7), starts);
        Assert.DoesNotContain(Start.AddDays(35), starts);
        Assert.DoesNotContain(Start.AddDays(28), starts);
        Assert.Equal([Start.AddDays(28).AddHours(2)], await StartsAsync("Standup (moved)"));
        var moved = _db.Events.AsNoTracking().Single(e => e.Title == "Standup (moved)");
        Assert.Equal(next!.Id, moved.SeriesMasterId);
        Assert.Equal(_db.Events.AsNoTracking().Single(e => e.Id == next.Id).Uid, moved.Uid);
    }

    [Fact]
    public async Task MovingTheNewHalf_DropsItsOldExclusionsAndEdits()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=WEEKLY", Start));
        await _events.DeleteAsync(_userId, series.Id, Start.AddDays(35));
        await _events.UpsertOccurrenceAsync(_userId, series.Id, Start.AddDays(28), Request("Edited", null, Start.AddDays(28)));

        // From the 4th on, an hour later: the old instants no longer exist.
        await _events.SplitSeriesAsync(_userId, series.Id, Fourth, Request("Later", "FREQ=WEEKLY", Fourth.AddHours(1)));

        Assert.Empty(await StartsAsync("Edited"));
        Assert.Contains(Start.AddDays(35).AddHours(1), await StartsAsync("Later"));
    }

    [Fact]
    public async Task OnTheFirstOccurrence_ItIsAWholeSeriesEdit()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=WEEKLY", Start));

        var edited = await _events.SplitSeriesAsync(_userId, series.Id, Start, Request("Renamed", "FREQ=WEEKLY", Start));

        Assert.Equal(series.Id, edited!.Id);
        Assert.Single(_db.Events.AsNoTracking());
    }

    [Fact]
    public async Task AnAllDaySeries_SplitsOnTheDay()
    {
        var day = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var series = await _events.CreateAsync(_userId, Request("Gym", "FREQ=DAILY", day, allDay: true));

        await _events.SplitSeriesAsync(_userId, series.Id, day.AddDays(3), Request("Gym+", "FREQ=DAILY", day.AddDays(3), allDay: true));

        Assert.Equal([day, day.AddDays(1), day.AddDays(2)], await StartsAsync("Gym"));
        Assert.Equal(day.AddDays(3), (await StartsAsync("Gym+"))[0]);
        Assert.Contains(_db.Events.AsNoTracking(), e => e.RRule == "FREQ=DAILY;UNTIL=20260805");
    }

    [Fact]
    public async Task AcrossADaylightSavingChange_TheTimesStayAt9InBerlin()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=WEEKLY", Start));
        var november = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero); // 09:00 CET

        await _events.SplitSeriesAsync(_userId, series.Id, november, Request("Winter", "FREQ=WEEKLY", november));

        var winter = (await _events.GetEventsAsync(_userId, november, november.AddDays(8))).Where(e => e.Title == "Winter").ToList();
        Assert.Equal([november, november.AddDays(7)], winter.Select(e => e.Start));
        var summer = await _events.GetEventsAsync(_userId, new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), november);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero), Assert.Single(summer).Start); // after the switch, still 09:00
    }

    [Fact]
    public async Task Guests_GetTheShortenedSeries_AndAnInvitationToTheNewOne()
    {
        var guests = new[] { new AttendeeInput { Email = "ann@example.com" } };
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=WEEKLY", Start, guests: guests));
        var ann = _db.Attendees.Single();
        ann.Status = AttendeeStatus.Accepted;
        await _db.SaveChangesAsync();
        _mailer.Requests.Clear();

        var next = await _events.SplitSeriesAsync(_userId, series.Id, Fourth, Request("Standup v2", "FREQ=WEEKLY", Fourth, guests: guests));

        Assert.Equal(["Standup", "Standup v2"], _mailer.Requests.Select(r => r.Title));
        Assert.All(_mailer.Requests, r => Assert.Equal(["ann@example.com"], r.Recipients));
        Assert.Equal(AttendeeStatus.Accepted, _db.Attendees.AsNoTracking().Single(a => a.EventId == series.Id).Status);
        Assert.Equal(AttendeeStatus.NeedsAction, _db.Attendees.AsNoTracking().Single(a => a.EventId == next!.Id).Status);
        Assert.True(_db.Events.AsNoTracking().Single(e => e.Id == series.Id).Sequence > 0);
    }

    [Fact]
    public async Task AReceivedInvitation_CannotBeSplit()
    {
        var series = await _events.CreateAsync(_userId, Request("Their meeting", "FREQ=WEEKLY", Start));
        var row = _db.Events.Single();
        row.InvitationStatus = AttendeeStatus.Accepted;
        row.OrganizerEmail = "boss@example.com";
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidInputException>(() =>
            _events.SplitSeriesAsync(_userId, series.Id, Fourth, Request("Mine now", "FREQ=WEEKLY", Fourth)));
    }

    [Fact]
    public async Task AnInstantTheSeriesDoesNotHave_IsNotFound()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=WEEKLY", Start));

        Assert.Null(await _events.SplitSeriesAsync(_userId, series.Id, Fourth.AddHours(3), Request("x", "FREQ=WEEKLY", Fourth)));
    }

    [Fact]
    public async Task DeletingFollowing_EndsTheSeries_AndDropsLaterEdits()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=WEEKLY", Start));
        await _events.UpsertOccurrenceAsync(_userId, series.Id, Start.AddDays(28), Request("Edited", null, Start.AddDays(28)));

        Assert.True(await _events.DeleteFollowingAsync(_userId, series.Id, Fourth));

        Assert.Equal([Start, Start.AddDays(7), Start.AddDays(14)], await StartsAsync("Standup"));
        Assert.Empty(await StartsAsync("Edited"));
    }

    [Fact]
    public async Task DeletingFollowing_FromTheFirst_DeletesTheSeries()
    {
        var series = await _events.CreateAsync(_userId, Request("Standup", "FREQ=WEEKLY", Start));

        Assert.True(await _events.DeleteFollowingAsync(_userId, series.Id, Start));

        Assert.Empty(_db.Events);
    }
}
