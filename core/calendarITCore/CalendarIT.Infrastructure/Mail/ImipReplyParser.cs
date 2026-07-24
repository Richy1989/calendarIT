using CalendarIT.Domain;
using MimeKit;
using ICalCalendar = Ical.Net.Calendar;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// Parses inbound iMIP (RFC 6047) REPLY emails — the messages guests' calendars send when
/// they Accept/Decline/Tentatively-accept an invitation. Pulls the event UID and the guest's
/// PARTSTAT out of the text/calendar part. Pure (no I/O), so the mapping is unit-testable;
/// anything that isn't a well-formed REPLY yields null and is simply skipped by the scanner.
/// </summary>
public static class ImipReplyParser
{
    public static ImipReply? TryParse(MimeMessage message)
    {
        var ics = ImipMime.FindCalendarText(message);
        return ics is null ? null : TryParse(ics);
    }

    public static ImipReply? TryParse(string icsText)
    {
        ICalCalendar? calendar;
        try
        {
            calendar = ICalCalendar.Load(icsText);
        }
        catch (Exception)
        {
            return null; // not valid iCalendar
        }
        if (calendar is null || !string.Equals(calendar.Method, "REPLY", StringComparison.OrdinalIgnoreCase))
        {
            return null; // only replies update attendee status
        }

        var ve = calendar.Events.FirstOrDefault();
        if (ve is null || string.IsNullOrWhiteSpace(ve.Uid))
        {
            return null;
        }

        var attendee = ve.Attendees.FirstOrDefault();
        var email = ImipMime.ExtractEmail(attendee?.Value);
        if (email is null)
        {
            return null; // a REPLY without an attendee can't be matched to a guest
        }

        return new ImipReply(ve.Uid, email, MapStatus(attendee!.ParticipationStatus), ve.Sequence);
    }

    private static AttendeeStatus MapStatus(string? partStat) => partStat?.ToUpperInvariant() switch
    {
        "ACCEPTED" => AttendeeStatus.Accepted,
        "DECLINED" => AttendeeStatus.Declined,
        "TENTATIVE" => AttendeeStatus.Tentative,
        _ => AttendeeStatus.NeedsAction,
    };
}
