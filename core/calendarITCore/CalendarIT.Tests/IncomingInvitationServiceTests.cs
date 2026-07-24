using CalendarIT.Domain;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Mail;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Calendar = CalendarIT.Domain.Calendar;

namespace CalendarIT.Tests;

/// <summary>An invitation others email us lands on our own calendar as pending, updates in place,
/// and is withdrawn on cancel — without ever disturbing an event we own under the same UID.</summary>
public sealed class IncomingInvitationServiceTests : IDisposable
{
    private const string Uid = "invite-9@example.com";
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly IncomingInvitationService _service;
    private readonly Guid _recipientId = Guid.NewGuid();
    private readonly Guid _calendarId = Guid.NewGuid();

    public IncomingInvitationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _db.Users.Add(new ApplicationUser { Id = _recipientId, UserName = "me@test", NormalizedUserName = "ME@TEST" });
        _db.Calendars.Add(new Calendar { Id = _calendarId, OwnerUserId = _recipientId, Name = "Personal", CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        _service = new IncomingInvitationService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static ImipRequest Request(string method = "REQUEST", int sequence = 0, string summary = "Design review")
    {
        var ics =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Org//EN\r\n" +
            $"METHOD:{method}\r\nBEGIN:VEVENT\r\nUID:{Uid}\r\n" +
            $"SEQUENCE:{sequence}\r\nDTSTART:20260901T090000Z\r\nDTEND:20260901T100000Z\r\n" +
            $"ORGANIZER:mailto:organizer@example.com\r\nSUMMARY:{summary}\r\nLOCATION:Room 1\r\n" +
            "END:VEVENT\r\nEND:VCALENDAR\r\n";
        return ImipRequestParser.TryParse(ics)!;
    }

    private Task<CalendarEvent?> InviteRow() =>
        _db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Uid == Uid && e.Calendar!.OwnerUserId == _recipientId);

    [Fact]
    public async Task Request_AddsPendingInvitationToRecipientCalendar()
    {
        var applied = await _service.ApplyRequestAsync(_recipientId, Request(summary: "Design review"));

        Assert.True(applied);
        var evt = await InviteRow();
        Assert.NotNull(evt);
        Assert.Equal(_calendarId, evt!.CalendarId);
        Assert.Equal("Design review", evt.Title);
        Assert.Equal(AttendeeStatus.NeedsAction, evt.InvitationStatus);
        Assert.Equal("organizer@example.com", evt.OrganizerEmail);
        Assert.Equal(new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), evt.StartUtc);
    }

    [Fact]
    public async Task Request_ForUserWithoutCalendar_CreatesOne()
    {
        var loner = Guid.NewGuid();
        _db.Users.Add(new ApplicationUser { Id = loner, UserName = "loner@test", NormalizedUserName = "LONER@TEST" });
        await _db.SaveChangesAsync();

        var applied = await _service.ApplyRequestAsync(loner, Request());

        Assert.True(applied);
        var calendar = await _db.Calendars.AsNoTracking().SingleAsync(c => c.OwnerUserId == loner);
        Assert.Equal("Personal", calendar.Name);
        Assert.True(await _db.Events.AnyAsync(e => e.CalendarId == calendar.Id && e.Uid == Uid));
    }

    [Fact]
    public async Task Request_HigherSequence_UpdatesTheInvitationInPlace()
    {
        await _service.ApplyRequestAsync(_recipientId, Request(sequence: 0, summary: "Design review"));
        var applied = await _service.ApplyRequestAsync(_recipientId, Request(sequence: 1, summary: "Design review (moved)"));

        Assert.True(applied);
        Assert.Equal(1, await _db.Events.CountAsync(e => e.Uid == Uid)); // updated, not duplicated
        var evt = await InviteRow();
        Assert.Equal("Design review (moved)", evt!.Title);
        Assert.Equal(AttendeeStatus.NeedsAction, evt.InvitationStatus); // still pending after an update
    }

    [Fact]
    public async Task Request_StaleSequence_IsIgnored()
    {
        await _service.ApplyRequestAsync(_recipientId, Request(sequence: 5, summary: "Current"));
        var applied = await _service.ApplyRequestAsync(_recipientId, Request(sequence: 2, summary: "Old resend"));

        Assert.False(applied);
        Assert.Equal("Current", (await InviteRow())!.Title);
    }

    [Fact]
    public async Task Cancel_RemovesTheInvitation()
    {
        await _service.ApplyRequestAsync(_recipientId, Request(sequence: 0));
        var applied = await _service.ApplyRequestAsync(_recipientId, Request(method: "CANCEL", sequence: 1));

        Assert.True(applied);
        Assert.Null(await InviteRow());
    }

    [Fact]
    public async Task Request_NeverOverwritesAnEventTheUserOwns()
    {
        // The user already owns an event with this UID (InvitationStatus is null).
        _db.Events.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(),
            CalendarId = _calendarId,
            Uid = Uid,
            Title = "My own event",
            StartUtc = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
        });
        await _db.SaveChangesAsync();

        var applied = await _service.ApplyRequestAsync(_recipientId, Request(summary: "Spoofed"));

        Assert.False(applied);
        Assert.Equal("My own event", (await InviteRow())!.Title); // untouched
    }

    [Fact]
    public async Task Cancel_ForUnknownInvitation_DoesNothing()
    {
        var applied = await _service.ApplyRequestAsync(_recipientId, Request(method: "CANCEL"));

        Assert.False(applied);
    }
}
