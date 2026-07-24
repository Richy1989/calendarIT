using System.Text;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// Shared plumbing for pulling iCalendar data out of inbound iMIP (RFC 6047) emails, used by
/// both the REPLY parser (guest RSVPs) and the REQUEST parser (invitations others send us).
/// </summary>
public static class ImipMime
{
    /// <summary>The text/calendar body of the message, or an .ics attachment as a fallback.</summary>
    public static string? FindCalendarText(MimeMessage message)
    {
        foreach (var part in message.BodyParts)
        {
            if (part is TextPart text && text.ContentType.IsMimeType("text", "calendar"))
            {
                return text.Text;
            }
            if (part is MimePart { Content: not null } mime &&
                (mime.ContentType.IsMimeType("application", "ics")
                 || (mime.FileName?.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                using var stream = new MemoryStream();
                mime.Content.DecodeTo(stream);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the message actually came from the address the iCalendar body claims sent it —
    /// the ORGANIZER of a REQUEST, or the ATTENDEE of a REPLY.
    ///
    /// Nothing in an .ics is authenticated: the body is just text an attacker can write. Without
    /// this check, anyone able to email the user could put events on their calendar under any
    /// organizer's name, or mark a guest as having accepted. Comparing against the envelope
    /// headers is what a desktop client does, and it's the cheapest defence available here
    /// (short of verifying DKIM, which the transport layer should be doing anyway).
    ///
    /// Sender and Reply-To count too: a delegate or a calendar system legitimately sends "on
    /// behalf of" an organizer, and that arrives as From = the sender, not the organizer.
    /// </summary>
    public static bool IsFromClaimedSender(MimeMessage message, string? claimedEmail)
    {
        if (string.IsNullOrWhiteSpace(claimedEmail))
        {
            return false; // nothing to attribute the message to
        }

        return message.From.Mailboxes
            .Concat(message.ResentFrom.Mailboxes)
            .Concat(message.ReplyTo.Mailboxes)
            .Concat(message.Sender is null ? [] : new[] { message.Sender })
            .Any(m => string.Equals(m.Address, claimedEmail, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The bare email address from a <c>mailto:</c> ORGANIZER/ATTENDEE value, or null.</summary>
    public static string? ExtractEmail(Uri? value)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        const string mailto = "mailto:";
        if (raw.StartsWith(mailto, StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[mailto.Length..];
        }
        raw = raw.Trim();
        return raw.Length == 0 ? null : raw;
    }
}
