using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;
using MimeKit;

namespace CalendarIT.Tests;

/// <summary>Pins how inbound iMIP REPLY messages map to a status change.</summary>
public sealed class ImipReplyParserTests
{
    private static string ReplyIcs(
        string uid = "invite-1@calendarit",
        string attendeeEmail = "guest@example.com",
        string partStat = "ACCEPTED",
        int sequence = 2,
        string method = "REPLY") =>
        "BEGIN:VCALENDAR\r\n" +
        "PRODID:-//Guest Client//EN\r\n" +
        "VERSION:2.0\r\n" +
        $"METHOD:{method}\r\n" +
        "BEGIN:VEVENT\r\n" +
        $"UID:{uid}\r\n" +
        $"SEQUENCE:{sequence}\r\n" +
        "DTSTART:20260901T090000Z\r\n" +
        "DTEND:20260901T100000Z\r\n" +
        "ORGANIZER:mailto:organizer@example.com\r\n" +
        $"ATTENDEE;PARTSTAT={partStat};CN=Guest:mailto:{attendeeEmail}\r\n" +
        "SUMMARY:Planning session\r\n" +
        "END:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    [Theory]
    [InlineData("ACCEPTED", AttendeeStatus.Accepted)]
    [InlineData("DECLINED", AttendeeStatus.Declined)]
    [InlineData("TENTATIVE", AttendeeStatus.Tentative)]
    [InlineData("DELEGATED", AttendeeStatus.NeedsAction)] // anything we don't map falls back
    public void TryParse_MapsPartStatToStatus(string partStat, AttendeeStatus expected)
    {
        var reply = ImipReplyParser.TryParse(ReplyIcs(partStat: partStat));

        Assert.NotNull(reply);
        Assert.Equal("invite-1@calendarit", reply!.Uid);
        Assert.Equal("guest@example.com", reply.AttendeeEmail);
        Assert.Equal(expected, reply.Status);
        Assert.Equal(2, reply.Sequence);
    }

    [Fact]
    public void TryParse_IgnoresNonReplyMethods()
    {
        Assert.Null(ImipReplyParser.TryParse(ReplyIcs(method: "REQUEST")));
    }

    [Fact]
    public void TryParse_ReturnsNullForGarbage()
    {
        Assert.Null(ImipReplyParser.TryParse("this is not a calendar"));
    }

    [Fact]
    public void TryParse_ReadsTheCalendarPartOfAMimeMessage()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Guest", "guest@example.com"));
        message.To.Add(new MailboxAddress("Organizer", "organizer@example.com"));
        message.Subject = "Accepted: Planning session";
        var calendarPart = new TextPart("calendar") { Text = ReplyIcs() };
        calendarPart.ContentType.Parameters.Add("method", "REPLY");
        message.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = "Guest has accepted." },
            calendarPart,
        };

        var reply = ImipReplyParser.TryParse(message);

        Assert.NotNull(reply);
        Assert.Equal(AttendeeStatus.Accepted, reply!.Status);
        Assert.Equal("guest@example.com", reply.AttendeeEmail);
    }
}
