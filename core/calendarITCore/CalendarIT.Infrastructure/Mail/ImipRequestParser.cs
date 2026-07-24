using MimeKit;
using ICalCalendar = Ical.Net.Calendar;
using ICalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>Which kind of organizer-sent iMIP message this is.</summary>
public enum ImipRequestMethod
{
    /// <summary>METHOD:REQUEST — a new invitation or an update to one.</summary>
    Request,

    /// <summary>METHOD:CANCEL — the organizer withdrew the event.</summary>
    Cancel,
}

/// <summary>One parsed inbound iMIP REQUEST/CANCEL: the event (by UID), who sent it, its
/// SEQUENCE (for update ordering), and the raw VEVENT to copy onto the recipient's calendar.</summary>
public sealed record ImipRequest(
    string Uid, ImipRequestMethod Method, string? OrganizerEmail, int Sequence, ICalEvent Event);

/// <summary>
/// Parses inbound iMIP (RFC 6047) REQUEST/CANCEL emails — the messages someone else's calendar
/// sends when they invite you to an event (or cancel one). Pulls out the VEVENT, its UID, the
/// organizer, and the SEQUENCE. Pure (no I/O), so the mapping is unit-testable; anything that
/// isn't a well-formed REQUEST/CANCEL yields null and is simply skipped by the scanner. REPLY
/// messages (guest RSVPs) are handled separately by <see cref="ImipReplyParser"/>.
/// </summary>
public static class ImipRequestParser
{
    public static ImipRequest? TryParse(MimeMessage message)
    {
        var ics = ImipMime.FindCalendarText(message);
        return ics is null ? null : TryParse(ics);
    }

    public static ImipRequest? TryParse(string icsText)
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

        var method = calendar?.Method?.ToUpperInvariant() switch
        {
            "REQUEST" => (ImipRequestMethod?)ImipRequestMethod.Request,
            "CANCEL" => ImipRequestMethod.Cancel,
            _ => null, // REPLY and anything else are not our concern here
        };
        if (method is null)
        {
            return null;
        }

        var ve = calendar!.Events.FirstOrDefault();
        if (ve is null || string.IsNullOrWhiteSpace(ve.Uid) || ve.Start is null)
        {
            return null; // a REQUEST/CANCEL we can't identify or place on a calendar
        }

        var organizer = ImipMime.ExtractEmail(ve.Organizer?.Value);
        return new ImipRequest(ve.Uid, method.Value, organizer, ve.Sequence, ve);
    }
}
