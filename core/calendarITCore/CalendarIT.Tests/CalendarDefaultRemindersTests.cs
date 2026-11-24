using CalendarIT.Application;
using CalendarIT.Application.Calendars;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// A calendar's default reminders: what the web editor starts a new timed event with, so the
/// quick create form needs no reminder controls of its own.
/// </summary>
public sealed class CalendarDefaultRemindersTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();

    public CalendarDefaultRemindersTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "rem@test", NormalizedUserName = "REM@TEST" });
        _db.Users.Add(new ApplicationUser { Id = _otherUserId, UserName = "other@test", NormalizedUserName = "OTHER@TEST" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private CalendarService Calendars() => new(_db, TimeProvider.System);

    private static ReminderInput R(int minutes, string channel) => new() { MinutesBefore = minutes, Channel = channel };

    [Fact]
    public async Task ACalendar_StartsWithoutDefaults()
    {
        var calendar = Assert.Single(await Calendars().ListAsync(_userId));

        Assert.Empty(calendar.DefaultReminders!);
    }

    [Fact]
    public async Task Defaults_AreStoredInOrder_WithoutDuplicates_AndListed()
    {
        var calendar = await Calendars().CreateAsync(_userId, new SaveCalendarRequest { Name = "Work" });

        var updated = await Calendars().SetDefaultRemindersAsync(_userId, calendar.Id,
            [R(1440, "email"), R(15, "WebPush"), R(15, "WebPush")]);

        Assert.Equal([new ReminderDto(15, "WebPush"), new ReminderDto(1440, "Email")], updated!.DefaultReminders);
        var listed = (await Calendars().ListAsync(_userId)).Single(c => c.Id == calendar.Id);
        Assert.Equal(updated.DefaultReminders, listed.DefaultReminders);
        Assert.Equal("15:WebPush;1440:Email", _db.Calendars.AsNoTracking().Single(c => c.Id == calendar.Id).DefaultReminders);
    }

    [Fact]
    public async Task AnEmptyList_ClearsThem()
    {
        var calendar = await Calendars().CreateAsync(_userId, new SaveCalendarRequest { Name = "Work" });
        await Calendars().SetDefaultRemindersAsync(_userId, calendar.Id, [R(15, "Email")]);

        var cleared = await Calendars().SetDefaultRemindersAsync(_userId, calendar.Id, []);

        Assert.Empty(cleared!.DefaultReminders!);
        Assert.Null(_db.Calendars.AsNoTracking().Single(c => c.Id == calendar.Id).DefaultReminders);
    }

    [Theory]
    [InlineData("Pigeon")]
    [InlineData("1")] // an enum's number is no channel name
    public async Task AnUnknownChannel_IsRejected(string channel)
    {
        var calendar = await Calendars().CreateAsync(_userId, new SaveCalendarRequest { Name = "Work" });

        await Assert.ThrowsAsync<InvalidInputException>(() =>
            Calendars().SetDefaultRemindersAsync(_userId, calendar.Id, [R(15, channel)]));
    }

    [Fact]
    public async Task AnotherUsersCalendar_IsNotFound()
    {
        var theirs = await Calendars().CreateAsync(_otherUserId, new SaveCalendarRequest { Name = "Theirs" });

        Assert.Null(await Calendars().SetDefaultRemindersAsync(_userId, theirs.Id, [R(15, "Email")]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("15:Pigeon;x:Email;-5:Email;99999:Email;7")]
    public void UnreadableStoredValues_ReadAsNone(string? stored) =>
        Assert.Empty(DefaultReminders.Parse(stored));

    [Fact]
    public void ReadableValues_SurviveNextToUnreadableOnes() =>
        Assert.Equal([new ReminderDto(30, "Email")], DefaultReminders.Parse("junk;30:email;15:Pigeon"));
}
