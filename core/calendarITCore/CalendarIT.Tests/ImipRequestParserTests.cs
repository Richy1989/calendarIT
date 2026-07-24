using CalendarIT.Infrastructure.Mail;
using MimeKit;

namespace CalendarIT.Tests;

/// <summary>Pins how inbound iMIP REQUEST/CANCEL messages (invitations others send us) are read.</summary>
public sealed class ImipRequestParserTests
{
    private static string RequestIcs(
        string uid = "invite-9@example.com",
        string organizer = "organizer@example.com",
        int sequence = 0,
        string method = "REQUEST",
        string summary = "Design review") =>
        "BEGIN:VCALENDAR\r\n" +
        "PRODID:-//Organizer Client//EN\r\n" +
        "VERSION:2.0\r\n" +
        $"METHOD:{method}\r\n" +
        "BEGIN:VEVENT\r\n" +
        $"UID:{uid}\r\n" +
        $"SEQUENCE:{sequence}\r\n" +
        "DTSTART:20260901T090000Z\r\n" +
        "DTEND:20260901T100000Z\r\n" +
        $"ORGANIZER:mailto:{organizer}\r\n" +
        "ATTENDEE;PARTSTAT=NEEDS-ACTION;CN=Me:mailto:me@example.com\r\n" +
        $"SUMMARY:{summary}\r\n" +
        "LOCATION:Room 1\r\n" +
        "END:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    [Theory]
    [InlineData("REQUEST", ImipRequestMethod.Request)]
    [InlineData("CANCEL", ImipRequestMethod.Cancel)]
    public void TryParse_ReadsMethodUidOrganizerAndEvent(string method, ImipRequestMethod expected)
    {
        var request = ImipRequestParser.TryParse(RequestIcs(method: method, sequence: 3));

        Assert.NotNull(request);
        Assert.Equal(expected, request!.Method);
        Assert.Equal("invite-9@example.com", request.Uid);
        Assert.Equal("organizer@example.com", request.OrganizerEmail);
        Assert.Equal(3, request.Sequence);
        Assert.Equal("Design review", request.Event.Summary);
    }

    [Fact]
    public void TryParse_IgnoresReplies()
    {
        // REPLY is the guest-RSVP path, handled by ImipReplyParser — not here.
        Assert.Null(ImipRequestParser.TryParse(RequestIcs(method: "REPLY")));
    }

    [Fact]
    public void TryParse_ReturnsNullForGarbage()
    {
        Assert.Null(ImipRequestParser.TryParse("this is not a calendar"));
    }

    [Fact]
    public void TryParse_ReadsTheCalendarPartOfAMimeMessage()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Organizer", "organizer@example.com"));
        message.To.Add(new MailboxAddress("Me", "me@example.com"));
        message.Subject = "Invitation: Design review";
        var calendarPart = new TextPart("calendar") { Text = RequestIcs() };
        calendarPart.ContentType.Parameters.Add("method", "REQUEST");
        message.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = "You are invited." },
            calendarPart,
        };

        var request = ImipRequestParser.TryParse(message);

        Assert.NotNull(request);
        Assert.Equal(ImipRequestMethod.Request, request!.Method);
        Assert.Equal("invite-9@example.com", request.Uid);
    }
}
