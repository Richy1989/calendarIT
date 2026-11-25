using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>Tasks: creating, editing, ticking off, subtasks one level deep, and where they may live.</summary>
public sealed class TaskServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();

    public TaskServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "t@test", NormalizedUserName = "T@TEST" });
        _db.Users.Add(new ApplicationUser { Id = _otherUserId, UserName = "o@test", NormalizedUserName = "O@TEST" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private TaskService Tasks() => new(_db, TimeProvider.System);

    private static SaveTaskRequest Request(string title, Guid? parentId = null, string? priority = null, Guid? calendarId = null) =>
        new() { Title = title, ParentId = parentId, Priority = priority, CalendarId = calendarId };

    [Fact]
    public async Task ATask_LandsInTheDefaultCalendar_WithItsDetails()
    {
        var due = new DateTimeOffset(2026, 12, 1, 15, 30, 0, TimeSpan.FromHours(1));

        var task = await Tasks().CreateAsync(_userId, new SaveTaskRequest
        {
            Title = "  File taxes  ", Notes = "the blue folder", Due = due, TimeZone = "europe/vienna", Priority = "High",
        });

        Assert.Equal("File taxes", task.Title);
        Assert.Equal(due, task.Due);
        Assert.Equal("Europe/Vienna", task.TimeZone);
        Assert.Equal("high", task.Priority);
        Assert.Equal("NeedsAction", task.Status);
        Assert.Equal(_db.Calendars.Single().Id, task.CalendarId);
        Assert.Equal(1, _db.Tasks.AsNoTracking().Single().Priority); // iCalendar PRIORITY 1
    }

    [Fact]
    public async Task ADueDate_IsADate_WhereverItWasSentFrom()
    {
        var task = await Tasks().CreateAsync(_userId, new SaveTaskRequest
        {
            Title = "Birthday card", Due = new DateTimeOffset(2026, 12, 3, 0, 0, 0, TimeSpan.FromHours(-5)), DueAllDay = true,
        });

        Assert.Equal(new DateTimeOffset(2026, 12, 3, 0, 0, 0, TimeSpan.Zero), task.Due);
        Assert.True(task.DueAllDay);
        Assert.Null(task.TimeZone);
    }

    [Fact]
    public async Task TickingOff_ATask_TicksOffItsSubtasks_AndReopeningASubtask_ReopensIt()
    {
        var task = await Tasks().CreateAsync(_userId, Request("Move house"));
        var boxes = await Tasks().CreateAsync(_userId, Request("Buy boxes", task.Id));
        var van = await Tasks().CreateAsync(_userId, Request("Rent a van", task.Id));

        await Tasks().SetDoneAsync(_userId, task.Id, done: true);

        Assert.All(_db.Tasks.AsNoTracking().ToList(), t => Assert.Equal(TaskItemStatus.Completed, t.Status));
        Assert.All(_db.Tasks.AsNoTracking().ToList(), t => Assert.NotNull(t.CompletedAtUtc));

        await Tasks().SetDoneAsync(_userId, van.Id, done: false);

        var rows = _db.Tasks.AsNoTracking().ToDictionary(t => t.Id);
        Assert.Equal(TaskItemStatus.NeedsAction, rows[van.Id].Status);
        Assert.Equal(TaskItemStatus.NeedsAction, rows[task.Id].Status);
        Assert.Equal(TaskItemStatus.Completed, rows[boxes.Id].Status);
    }

    [Fact]
    public async Task Subtasks_GoOneLevelDeep()
    {
        var task = await Tasks().CreateAsync(_userId, Request("Move house"));
        var sub = await Tasks().CreateAsync(_userId, Request("Pack", task.Id));

        await Assert.ThrowsAsync<InvalidInputException>(() => Tasks().CreateAsync(_userId, Request("Pack books", sub.Id)));
        // …and a task with subtasks can't become one.
        var other = await Tasks().CreateAsync(_userId, Request("Other"));
        await Assert.ThrowsAsync<InvalidInputException>(() => Tasks().UpdateAsync(_userId, task.Id, Request("Move house", other.Id)));
        await Assert.ThrowsAsync<InvalidInputException>(() => Tasks().UpdateAsync(_userId, task.Id, Request("Move house", task.Id)));
    }

    [Fact]
    public async Task ASubtask_LivesInItsTasksCalendar_AndMovesWithIt()
    {
        var work = new Calendar { Id = Guid.NewGuid(), OwnerUserId = _userId, Name = "Work" };
        _db.Calendars.Add(work);
        await _db.SaveChangesAsync();
        var task = await Tasks().CreateAsync(_userId, Request("Launch"));
        var sub = await Tasks().CreateAsync(_userId, Request("Press release", task.Id, calendarId: work.Id));
        Assert.Equal(task.CalendarId, sub.CalendarId); // the parent decides

        await Tasks().UpdateAsync(_userId, task.Id, Request("Launch", calendarId: work.Id));

        Assert.All(_db.Tasks.AsNoTracking().ToList(), t => Assert.Equal(work.Id, t.CalendarId));
    }

    [Fact]
    public async Task Deleting_ATask_DeletesItsSubtasks_AndRecordsThemForPhoneSync()
    {
        var task = await Tasks().CreateAsync(_userId, Request("Move house"));
        await Tasks().CreateAsync(_userId, Request("Pack", task.Id));

        Assert.True(await Tasks().DeleteAsync(_userId, task.Id));

        Assert.Empty(_db.Tasks);
        Assert.Equal(2, _db.DeletedResources.Count());
    }

    [Fact]
    public async Task TheList_ShowsOpenTasks_AndDoneOnesWhenAsked_InOrder()
    {
        var a = await Tasks().CreateAsync(_userId, Request("A"));
        var b = await Tasks().CreateAsync(_userId, Request("B"));
        await Tasks().CreateAsync(_userId, Request("C"));
        await Tasks().SetDoneAsync(_userId, b.Id, true);
        await Tasks().UpdateAsync(_userId, a.Id, new SaveTaskRequest { Title = "A", SortOrder = 99 });

        Assert.Equal(["C", "A"], (await Tasks().ListAsync(_userId, includeDone: false)).Select(t => t.Title));
        Assert.Equal(3, (await Tasks().ListAsync(_userId, includeDone: true)).Count);
    }

    [Fact]
    public async Task AnotherUsersTasks_AreNotThere()
    {
        var theirs = await Tasks().CreateAsync(_otherUserId, Request("Theirs"));

        Assert.Null(await Tasks().GetAsync(_userId, theirs.Id));
        Assert.Null(await Tasks().UpdateAsync(_userId, theirs.Id, Request("Mine")));
        Assert.False(await Tasks().DeleteAsync(_userId, theirs.Id));
        Assert.Empty(await Tasks().ListAsync(_userId, includeDone: true));
        await Assert.ThrowsAsync<InvalidInputException>(() => Tasks().CreateAsync(_userId, Request("Sneaky", theirs.Id)));
    }

    [Fact]
    public async Task ASubscribedCalendar_TakesNoTasks()
    {
        var feed = new Calendar { Id = Guid.NewGuid(), OwnerUserId = _userId, Name = "Holidays", SourceUrl = "https://example.com/h.ics" };
        _db.Calendars.Add(feed);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidInputException>(() => Tasks().CreateAsync(_userId, Request("x", calendarId: feed.Id)));
    }

    [Theory]
    [InlineData("urgent")]
    [InlineData("1")]
    public async Task AnUnknownPriority_IsRefused(string priority) =>
        await Assert.ThrowsAsync<InvalidInputException>(() => Tasks().CreateAsync(_userId, Request("x", priority: priority)));

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "high")]
    [InlineData(3, "high")]
    [InlineData(5, "normal")]
    [InlineData(7, "low")]
    [InlineData(9, "low")]
    public void PhonePriorities_AreReadAsTheThreeLevels(int priority, string? name) =>
        Assert.Equal(name, TaskService.PriorityName(priority));
}
