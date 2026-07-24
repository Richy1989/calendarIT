using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;

namespace CalendarIT.Tests;

/// <summary>Responding to a received invitation updates our status and mails a REPLY back to the
/// organizer — but only for an actual received invitation, and only with a real RSVP status.</summary>
public sealed class EventRsvpTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EventService _events;
    private readonly FakeInvitationMailer _mailer = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _calendarId = Guid.NewGuid();

    public EventRsvpTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "me@test", NormalizedUserName = "ME@TEST" });
        _db.Calendars.Add(new Calendar { Id = _calendarId, OwnerUserId = _userId, Name = "Personal" });
        _db.SaveChanges();
        _events = new EventService(_db, TimeProvider.System, _mailer, new InternalInvitationDelivery(_db, TimeProvider.System));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Guid> SeedInvitation(
        AttendeeStatus status = AttendeeStatus.NeedsAction, string? organizer = "organizer@example.com")
    {
        var id = Guid.NewGuid();
        _db.Events.Add(new CalendarEvent
        {
            Id = id,
            CalendarId = _calendarId,
            Uid = "invite-9@example.com",
            Title = "Design review",
            StartUtc = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
            InvitationStatus = status,
            OrganizerEmail = organizer,
        });
        await _db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedOwnEvent()
    {
        var id = Guid.NewGuid();
        _db.Events.Add(new CalendarEvent
        {
            Id = id,
            CalendarId = _calendarId,
            Uid = "own-1@calendarit",
            Title = "My own event",
            StartUtc = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
        });
        await _db.SaveChangesAsync();
        return id;
    }

    [Theory]
    [InlineData("Accepted", AttendeeStatus.Accepted)]
    [InlineData("declined", AttendeeStatus.Declined)]
    [InlineData("Tentative", AttendeeStatus.Tentative)]
    public async Task Respond_UpdatesStatus_AndMailsReplyToOrganizer(string status, AttendeeStatus expected)
    {
        var id = await SeedInvitation();

        var dto = await _events.RespondToInvitationAsync(_userId, id, status);

        Assert.NotNull(dto);
        Assert.Equal(expected.ToString(), dto!.InvitationStatus);
        Assert.Equal(expected, (await _db.Events.AsNoTracking().SingleAsync(e => e.Id == id)).InvitationStatus);

        var reply = Assert.Single(_mailer.Replies);
        Assert.Equal("Design review", reply.Title);
        Assert.Equal("organizer@example.com", reply.Organizer);
        Assert.Equal(expected, reply.Status);
    }

    [Fact]
    public async Task Respond_ToOwnEvent_ReturnsNull_AndMailsNothing()
    {
        var id = await SeedOwnEvent();

        var dto = await _events.RespondToInvitationAsync(_userId, id, "Accepted");

        Assert.Null(dto);
        Assert.Empty(_mailer.Replies);
    }

    [Theory]
    [InlineData("NeedsAction")]
    [InlineData("Maybe")]
    [InlineData("")]
    public async Task Respond_WithNonRsvpStatus_ReturnsNull(string status)
    {
        var id = await SeedInvitation();

        var dto = await _events.RespondToInvitationAsync(_userId, id, status);

        Assert.Null(dto);
        Assert.Empty(_mailer.Replies);
        Assert.Equal(AttendeeStatus.NeedsAction, (await _db.Events.AsNoTracking().SingleAsync(e => e.Id == id)).InvitationStatus);
    }

    [Fact]
    public async Task Respond_ForAnotherUsersInvitation_ReturnsNull()
    {
        var id = await SeedInvitation();

        var dto = await _events.RespondToInvitationAsync(Guid.NewGuid(), id, "Accepted");

        Assert.Null(dto);
        Assert.Empty(_mailer.Replies);
    }

    [Fact]
    public async Task Respond_UnknownEvent_ReturnsNull()
    {
        var dto = await _events.RespondToInvitationAsync(_userId, Guid.NewGuid(), "Accepted");

        Assert.Null(dto);
    }
}
