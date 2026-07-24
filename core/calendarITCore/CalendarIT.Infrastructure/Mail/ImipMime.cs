using System.Text;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// Shared plumbing for pulling iCalendar data out of inbound iMIP (RFC 6047) emails, used by
/// both the REPLY parser (guest RSVPs) and the REQUEST parser (invitations others send us).
/// </summary>
internal static class ImipMime
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
