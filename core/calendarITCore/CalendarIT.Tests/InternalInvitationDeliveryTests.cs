using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>
/// Inviting a guest whose email belongs to another CalendarIT user drops the event straight onto
/// their calendar (no email needed), and keeps it in step as the organizer edits or deletes.
/// </summary>
public sealed class InternalInvitationDeliveryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EventService _events;
    private readonly Guid _organizerId = Guid.NewGuid();
    private readonly Guid _inviteeId = Guid.NewGuid();

    public InternalInvitationDeliveryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser
        {
            Id = _organizerId, UserName = "alice@example.com", NormalizedUserName = "ALICE@EXAMPLE.COM",
            Email = "alice@example.com", NormalizedEmail = "ALICE@EXAMPLE.COM",
        });
        _db.Users.Add(new ApplicationUser
        {
            Id = _inviteeId, UserName = "bob@example.com", NormalizedUserName = "BOB@EXAMPLE.COM",
            Email = "bob@example.com", NormalizedEmail = "BOB@EXAMPLE.COM",
        });
        _db.SaveChanges();
        _events = new EventService(_db, TimeProvider.System, new FakeInvitationMailer(),
            new InternalInvitationDelivery(_db, TimeProvider.System));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SaveEventRequest Request(string title, params string[] guests) => new()
    {
        Title = title,
        Start = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
        Attendees = guests.Select(g => new AttendeeInput { Email = g }).ToList(),
    };

    private Task<List<CalendarEvent>> InviteeEvents() =>
        _db.Events.AsNoTracking().Where(e => e.Calendar!.OwnerUserId == _inviteeId).ToListAsync();

    [Fact]
    public async Task Invite_ALocalUser_PutsTheEventOnTheirCalendar()
    {
        var created = await _events.CreateAsync(_organizerId, Request("Design sync", "bob@example.com"));

        var copy = Assert.Single(await InviteeEvents());
        Assert.Equal("Design sync", copy.Title);
        Assert.NotEqual(created.Id, copy.Id); // it's a distinct event row…
        Assert.Equal(await UidOf(created.Id), copy.Uid); // …sharing the meeting UID
        Assert.Equal(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), copy.StartUtc);
    }

    [Fact]
    public async Task Invite_AnExternalAddress_CreatesNoCopy()
    {
        await _events.CreateAsync(_organizerId, Request("Lunch", "stranger@elsewhere.com"));
        Assert.Empty(await InviteeEvents());
    }

    [Fact]
    public async Task Invite_YourOwnAddress_DoesNotDuplicateOntoYourCalendar()
    {
        await _events.CreateAsync(_organizerId, Request("Solo", "alice@example.com"));

        // Only the organizer's own event exists — no self-delivered copy.
        var organizerEvents = await _db.Events.AsNoTracking()
            .Where(e => e.Calendar!.OwnerUserId == _organizerId).ToListAsync();
        Assert.Single(organizerEvents);
    }

    [Fact]
    public async Task EditingTheEvent_UpdatesTheLocalCopy()
    {
        var created = await _events.CreateAsync(_organizerId, Request("v1", "bob@example.com"));

        await _events.UpdateAsync(_organizerId, created.Id, new SaveEventRequest
        {
            Title = "v2",
            Start = new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero),
            Attendees = [new AttendeeInput { Email = "bob@example.com" }],
        });

        var copy = Assert.Single(await InviteeEvents());
        Assert.Equal("v2", copy.Title);
        Assert.Equal(new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc), copy.StartUtc);
    }

    [Fact]
    public async Task RemovingTheGuest_WithdrawsTheLocalCopy()
    {
        var created = await _events.CreateAsync(_organizerId, Request("Meeting", "bob@example.com"));
        Assert.Single(await InviteeEvents());

        await _events.UpdateAsync(_organizerId, created.Id, Request("Meeting")); // no guests now
        Assert.Empty(await InviteeEvents());
    }

    [Fact]
    public async Task DeletingTheEvent_WithdrawsTheLocalCopy()
    {
        var created = await _events.CreateAsync(_organizerId, Request("Meeting", "bob@example.com"));
        Assert.Single(await InviteeEvents());

        await _events.DeleteAsync(_organizerId, created.Id, occurrence: null);
        Assert.Empty(await InviteeEvents());
    }

    // ---------------------------------------------------------------- forged UIDs

    // Copies are matched by UID, and a UID is not ours to trust: an .ics import keeps whatever
    // UID the file carries, and a CalDAV PUT takes it from the request body. So any user can mint
    // an event whose UID collides with one on someone else's calendar, invite them, and — without
    // a provenance check — have delivery overwrite or delete an event they never shared.

    /// <summary>Plants an event on <paramref name="ownerId"/>'s calendar with a chosen UID, the
    /// way a user can through .ics import.</summary>
    private async Task<CalendarEvent> ImportWithUid(Guid ownerId, string uid, string title)
    {
        var io = new CalendarIoService(_db, TimeProvider.System);
        await io.ImportAsync(ownerId, $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//attacker//EN
            BEGIN:VEVENT
            UID:{uid}
            SUMMARY:{title}
            DTSTART:20261005T080000Z
            DTEND:20261005T090000Z
            END:VEVENT
            END:VCALENDAR
            """);
        return await _db.Events.AsNoTracking()
            .SingleAsync(e => e.Uid == uid && e.Calendar!.OwnerUserId == ownerId);
    }

    [Fact]
    public async Task Invite_WithAUidTheGuestAlreadyOwns_LeavesTheirEventAlone()
    {
        const string uid = "collision@example.com";
        var theirs = await ImportWithUid(_inviteeId, uid, "Salary review");
        var forged = await ImportWithUid(_organizerId, uid, "Forged");

        await _events.UpdateAsync(_organizerId, forged.Id, Request("Spoofed", "bob@example.com"));

        var untouched = Assert.Single(await InviteeEvents());
        Assert.Equal(theirs.Id, untouched.Id);
        Assert.Equal("Salary review", untouched.Title);
        Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc), untouched.StartUtc);
    }

    [Fact]
    public async Task Delete_WithAUidTheGuestAlreadyOwns_DoesNotRemoveTheirEvent()
    {
        const string uid = "collision@example.com";
        await ImportWithUid(_inviteeId, uid, "Salary review");
        var forged = await ImportWithUid(_organizerId, uid, "Forged");
        await _events.UpdateAsync(_organizerId, forged.Id, Request("Spoofed", "bob@example.com"));

        await _events.DeleteAsync(_organizerId, forged.Id, occurrence: null);

        var survivor = Assert.Single(await InviteeEvents());
        Assert.Equal("Salary review", survivor.Title);
    }

    private async Task<string> UidOf(Guid eventId) =>
        (await _db.Events.AsNoTracking().SingleAsync(e => e.Id == eventId)).Uid;
}
