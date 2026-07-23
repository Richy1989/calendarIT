using CalendarIT.Domain;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;

namespace CalendarIT.Tests;

/// <summary>Applying a guest's reply updates only their row, and only on the organizer's own event.</summary>
public sealed class InvitationReplyServiceTests : IDisposable
{
    private const string Uid = "invite-1@calendarit";
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly InvitationReplyService _service;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();

    public InvitationReplyServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _db.Users.Add(new ApplicationUser { Id = _ownerId, UserName = "owner@test", NormalizedUserName = "OWNER@TEST" });
        var calendarId = Guid.NewGuid();
        _db.Calendars.Add(new Calendar { Id = calendarId, OwnerUserId = _ownerId, Name = "Personal" });
        _db.Events.Add(new CalendarEvent
        {
            Id = _eventId,
            CalendarId = calendarId,
            Uid = Uid,
            Title = "Planning session",
            StartUtc = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
            Attendees =
            [
                new Attendee { Id = Guid.NewGuid(), Email = "guest@example.com", Status = AttendeeStatus.NeedsAction },
            ],
        });
        _db.SaveChanges();
        _service = new InvitationReplyService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<AttendeeStatus> StatusOf(string email) =>
        (await _db.Attendees.AsNoTracking().SingleAsync(a => a.Email == email)).Status;

    [Fact]
    public async Task ApplyReply_UpdatesTheGuestStatus()
    {
        var applied = await _service.ApplyReplyAsync(
            _ownerId, new ImipReply(Uid, "guest@example.com", AttendeeStatus.Accepted, 1));

        Assert.True(applied);
        Assert.Equal(AttendeeStatus.Accepted, await StatusOf("guest@example.com"));
    }

    [Fact]
    public async Task ApplyReply_MatchesTheEmailCaseInsensitively()
    {
        var applied = await _service.ApplyReplyAsync(
            _ownerId, new ImipReply(Uid, "GUEST@Example.com", AttendeeStatus.Declined, 1));

        Assert.True(applied);
        Assert.Equal(AttendeeStatus.Declined, await StatusOf("guest@example.com"));
    }

    [Fact]
    public async Task ApplyReply_UnknownUid_DoesNothing()
    {
        var applied = await _service.ApplyReplyAsync(
            _ownerId, new ImipReply("no-such-uid", "guest@example.com", AttendeeStatus.Accepted, 1));

        Assert.False(applied);
        Assert.Equal(AttendeeStatus.NeedsAction, await StatusOf("guest@example.com"));
    }

    [Fact]
    public async Task ApplyReply_GuestNotOnTheEvent_DoesNothing()
    {
        var applied = await _service.ApplyReplyAsync(
            _ownerId, new ImipReply(Uid, "stranger@example.com", AttendeeStatus.Accepted, 1));

        Assert.False(applied);
    }

    [Fact]
    public async Task ApplyReply_ForAnotherUsersEvent_DoesNothing()
    {
        // The same UID, but scoped to a different organizer — must not match.
        var applied = await _service.ApplyReplyAsync(
            Guid.NewGuid(), new ImipReply(Uid, "guest@example.com", AttendeeStatus.Accepted, 1));

        Assert.False(applied);
        Assert.Equal(AttendeeStatus.NeedsAction, await StatusOf("guest@example.com"));
    }

    [Fact]
    public async Task ApplyReply_WhenStatusUnchanged_ReturnsFalse()
    {
        var applied = await _service.ApplyReplyAsync(
            _ownerId, new ImipReply(Uid, "guest@example.com", AttendeeStatus.NeedsAction, 1));

        Assert.False(applied);
    }
}
