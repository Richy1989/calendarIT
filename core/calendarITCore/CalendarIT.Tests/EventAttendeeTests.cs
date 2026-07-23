using CalendarIT.Application.Calendars;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Identity;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Tests;

/// <summary>Attendees on events: persistence, status preservation, SEQUENCE, and what gets mailed.</summary>
public sealed class EventAttendeeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EventService _events;
    private readonly FakeInvitationMailer _mailer = new();
    private readonly Guid _userId = Guid.NewGuid();

    public EventAttendeeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = _userId, UserName = "att@test", NormalizedUserName = "ATT@TEST" });
        _db.SaveChanges();
        _events = new EventService(_db, TimeProvider.System, _mailer, new InternalInvitationDelivery(_db, TimeProvider.System));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SaveEventRequest Request(params string[] guests) => new()
    {
        Title = "Team lunch",
        Start = DateTimeOffset.UtcNow,
        Attendees = guests.Select(g => new AttendeeInput { Email = g }).ToList(),
    };

    [Fact]
    public async Task Create_PersistsAttendees_AndSendsRequests()
    {
        var dto = await _events.CreateAsync(_userId, Request("a@example.com", "b@example.com"));

        Assert.Equal(2, dto.Attendees.Count);
        Assert.All(dto.Attendees, a => Assert.Equal("NeedsAction", a.Status));

        var sent = Assert.Single(_mailer.Requests);
        Assert.Equal(["a@example.com", "b@example.com"], sent.Recipients.Order().ToArray());
    }

    [Fact]
    public async Task Create_DeduplicatesGuestsByEmail()
    {
        var dto = await _events.CreateAsync(_userId, Request("a@example.com", "A@Example.com "));
        Assert.Single(dto.Attendees);
    }

    [Fact]
    public async Task Update_ReplacesSet_KeepsStatus_BumpsSequence_AndCancelsRemoved()
    {
        var created = await _events.CreateAsync(_userId, Request("keep@example.com", "drop@example.com"));

        // Simulate an earlier reply from "keep" (phase 3 will do this for real).
        var kept = await _db.Attendees.SingleAsync(a => a.Email == "keep@example.com");
        kept.Status = AttendeeStatus.Accepted;
        await _db.SaveChangesAsync();
        _mailer.Requests.Clear();

        var updated = await _events.UpdateAsync(_userId, created.Id, Request("keep@example.com", "new@example.com"));

        Assert.Equal(2, updated!.Attendees.Count);
        Assert.Equal("Accepted", updated.Attendees.Single(a => a.Email == "keep@example.com").Status);
        Assert.Equal("NeedsAction", updated.Attendees.Single(a => a.Email == "new@example.com").Status);

        var entity = await _db.Events.SingleAsync();
        Assert.Equal(1, entity.Sequence);

        var request = Assert.Single(_mailer.Requests);
        Assert.Equal(["keep@example.com", "new@example.com"], request.Recipients.Order().ToArray());
        var cancel = Assert.Single(_mailer.Cancels);
        Assert.Equal(["drop@example.com"], cancel.Recipients);
    }

    [Fact]
    public async Task Update_WithNullAttendees_LeavesTheSetUntouched()
    {
        var created = await _events.CreateAsync(_userId, Request("a@example.com"));

        // e.g. a drag-move that doesn't carry attendee data
        var updated = await _events.UpdateAsync(_userId, created.Id, new SaveEventRequest
        {
            Title = "Team lunch (moved)",
            Start = DateTimeOffset.UtcNow.AddHours(1),
        });

        Assert.Single(updated!.Attendees);
    }

    [Fact]
    public async Task Update_ReplacingReminders_DoesNotTripEfsExistingKeyHeuristic()
    {
        // Regression: navigation-discovered child entities with pre-set Guid keys were
        // tracked as Modified instead of Added, so updating an event's reminders threw
        // DbUpdateConcurrencyException. Attendees shared the same pattern.
        var created = await _events.CreateAsync(_userId, new SaveEventRequest
        {
            Title = "With reminder",
            Start = DateTimeOffset.UtcNow,
            Reminders = [new ReminderInput { MinutesBefore = 10, Channel = "Email" }],
        });

        var updated = await _events.UpdateAsync(_userId, created.Id, new SaveEventRequest
        {
            Title = "With reminder",
            Start = DateTimeOffset.UtcNow,
            Reminders = [new ReminderInput { MinutesBefore = 30, Channel = "Email" }],
        });

        var reminder = Assert.Single(updated!.Reminders);
        Assert.Equal(30, reminder.MinutesBefore);
    }

    [Fact]
    public async Task Delete_SendsCancelToAllInvited()
    {
        var created = await _events.CreateAsync(_userId, Request("a@example.com", "b@example.com"));

        await _events.DeleteAsync(_userId, created.Id, occurrence: null);

        var cancel = Assert.Single(_mailer.Cancels);
        Assert.Equal(["a@example.com", "b@example.com"], cancel.Recipients.Order().ToArray());
        Assert.Empty(await _db.Events.ToListAsync());
    }
}
