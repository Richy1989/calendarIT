using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;
using MimeKit;

namespace CalendarIT.Tests;

/// <summary>Pins the iMIP message shape: method, organizer, attendee parameters, sequence.</summary>
public sealed class InvitationBuilderTests
{
    private static readonly MailAccount Organizer = new()
    {
        UserId = Guid.NewGuid(),
        Address = "organizer@example.com",
        SmtpHost = "smtp.example.com",
        Username = "organizer@example.com",
    };

    private static CalendarEvent Event(params Attendee[] attendees) => new()
    {
        Id = Guid.NewGuid(),
        Uid = "invite-1@calendarit",
        Title = "Planning session",
        Location = "Room 2.04",
        StartUtc = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
        EndUtc = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
        Sequence = 3,
        Attendees = attendees.ToList(),
    };

    // iCalendar folds lines at 75 octets (CRLF + space), which can split the substrings
    // we assert on — unfold before matching.
    private static string CalendarPart(MimeMessage message) =>
        ((TextPart)((MultipartAlternative)message.Body).OfType<TextPart>()
            .Single(p => p.ContentType.MimeType == "text/calendar")).Text
        .Replace("\r\n ", string.Empty)
        .Replace("\n ", string.Empty);

    [Fact]
    public void BuildRequest_CarriesMethodOrganizerAttendeeAndSequence()
    {
        var guest = new Attendee { Email = "guest@example.com", Name = "Guest", Status = AttendeeStatus.NeedsAction };
        var message = InvitationBuilder.BuildRequest(Organizer, Event(guest), guest);

        Assert.Equal("Invitation: Planning session", message.Subject);
        Assert.Equal("organizer@example.com", ((MailboxAddress)message.From[0]).Address);
        Assert.Equal("guest@example.com", ((MailboxAddress)message.To[0]).Address);

        var ics = CalendarPart(message);
        Assert.Contains("METHOD:REQUEST", ics);
        Assert.Contains("ORGANIZER", ics);
        Assert.Contains("mailto:organizer@example.com", ics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mailto:guest@example.com", ics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RSVP=TRUE", ics);
        Assert.Contains("PARTSTAT=NEEDS-ACTION", ics);
        Assert.Contains("SEQUENCE:3", ics);

        // The transport-level content type must announce the method too.
        var part = ((MultipartAlternative)message.Body).OfType<TextPart>()
            .Single(p => p.ContentType.MimeType == "text/calendar");
        Assert.Equal("REQUEST", part.ContentType.Parameters["method"]);
    }

    [Fact]
    public void BuildCancel_MarksCancelledAndStillListsARemovedGuest()
    {
        // The recipient was already removed from the event's attendee list.
        var removed = new Attendee { Email = "removed@example.com", Status = AttendeeStatus.Accepted };
        var message = InvitationBuilder.BuildCancel(Organizer, Event(), removed);

        Assert.Equal("Cancelled: Planning session", message.Subject);
        var ics = CalendarPart(message);
        Assert.Contains("METHOD:CANCEL", ics);
        Assert.Contains("STATUS:CANCELLED", ics);
        Assert.Contains("mailto:removed@example.com", ics, StringComparison.OrdinalIgnoreCase);
    }
}
