using CalendarIT.Domain;
using CalendarIT.Infrastructure.Calendars;
using Ical.Net.Serialization;
using MimeKit;
using DomainEvent = CalendarIT.Domain.CalendarEvent;
using ICalAttendee = Ical.Net.DataTypes.Attendee;
using ICalCalendar = Ical.Net.Calendar;
using ICalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using Organizer = Ical.Net.DataTypes.Organizer;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// Builds iMIP (RFC 6047) invitation emails: a plain-text summary plus a text/calendar
/// part carrying METHOD:REQUEST (invite/update) or METHOD:CANCEL. Pure — no I/O — so
/// the message shape is unit-testable.
/// </summary>
public static class InvitationBuilder
{
    public static MimeMessage BuildRequest(MailAccount organizer, DomainEvent evt, Attendee recipient) =>
        Build(organizer, evt, recipient, "REQUEST",
            subject: $"Invitation: {evt.Title}",
            intro: $"{organizer.Address} invites you to:");

    public static MimeMessage BuildCancel(MailAccount organizer, DomainEvent evt, Attendee recipient) =>
        Build(organizer, evt, recipient, "CANCEL",
            subject: $"Cancelled: {evt.Title}",
            intro: $"{organizer.Address} cancelled:");

    private static MimeMessage Build(
        MailAccount organizer, DomainEvent evt, Attendee recipient, string method, string subject, string intro)
    {
        var cal = new ICalCalendar { ProductId = "-//CalendarIT//EN", Method = method };
        cal.Events.Add(ToInviteEvent(organizer, evt, method, recipient));
        var ics = new CalendarSerializer().SerializeToString(cal)!;

        var when = evt.IsAllDay
            ? evt.StartUtc.ToString("ddd, MMM d yyyy") + " (all day)"
            : evt.StartUtc.ToString("ddd, MMM d yyyy HH:mm 'UTC'");
        var lines = new List<string> { intro, string.Empty, evt.Title, when };
        if (!string.IsNullOrWhiteSpace(evt.Location))
        {
            lines.Add(evt.Location);
        }
        if (!string.IsNullOrWhiteSpace(evt.Description))
        {
            lines.Add(string.Empty);
            lines.Add(evt.Description);
        }

        var calendarPart = new TextPart("calendar") { Text = ics };
        calendarPart.ContentType.Parameters.Add("method", method);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(organizer.Address, organizer.Address));
        message.To.Add(new MailboxAddress(recipient.Name ?? recipient.Email, recipient.Email));
        message.Subject = subject;
        message.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = string.Join('\n', lines) },
            calendarPart,
        };
        return message;
    }

    /// <summary>The VEVENT as sent to guests: base mapping plus ORGANIZER/ATTENDEE/SEQUENCE.</summary>
    private static ICalEvent ToInviteEvent(MailAccount organizer, DomainEvent evt, string method, Attendee recipient)
    {
        var ve = ICalEventMapper.ToICalEvent(evt);
        ve.Sequence = evt.Sequence;
        ve.Organizer = new Organizer($"mailto:{organizer.Address}");
        if (method == "CANCEL")
        {
            ve.Status = "CANCELLED";
        }
        // A CANCEL for a guest just removed from the event must still list them, or their
        // calendar can't match the cancellation to themselves.
        var attendees = evt.Attendees.Any(a => a.Email.Equals(recipient.Email, StringComparison.OrdinalIgnoreCase))
            ? evt.Attendees
            : evt.Attendees.Append(recipient);
        foreach (var a in attendees)
        {
            ve.Attendees.Add(new ICalAttendee($"mailto:{a.Email}")
            {
                CommonName = a.Name ?? a.Email,
                Role = "REQ-PARTICIPANT",
                Rsvp = true,
                ParticipationStatus = a.Status switch
                {
                    AttendeeStatus.Accepted => "ACCEPTED",
                    AttendeeStatus.Declined => "DECLINED",
                    AttendeeStatus.Tentative => "TENTATIVE",
                    _ => "NEEDS-ACTION",
                },
            });
        }
        return ve;
    }
}
