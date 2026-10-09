using System.Text;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// Shared plumbing for pulling iCalendar data out of inbound iMIP (RFC 6047) emails, used by
/// both the REPLY parser (guest RSVPs) and the REQUEST parser (invitations others send us).
/// </summary>
public static class ImipMime
{
    /// <summary>How many forwarded-as-attachment layers <see cref="FindCalendarText"/> unwraps.</summary>
    private const int MaxAttachedMessageDepth = 3;

    /// <summary>The text/calendar body of the message, or an .ics attachment as a fallback —
    /// looking inside an attached email (a forward "as attachment") when the message itself
    /// carries none.</summary>
    public static string? FindCalendarText(MimeMessage message) => FindCalendarText(message, 0);

    private static string? FindCalendarText(MimeMessage message, int depth)
    {
        // BodyParts doesn't descend into attached messages, so the message's own parts win.
        var text = FindOwnCalendarText(message);
        if (text is not null || depth >= MaxAttachedMessageDepth)
        {
            return text;
        }
        foreach (var attached in message.BodyParts.OfType<MessagePart>())
        {
            if (attached.Message is { } inner && FindCalendarText(inner, depth + 1) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static string? FindOwnCalendarText(MimeMessage message)
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
    /// <para>What hangs on it: a REPLY or CANCEL that fails it is ignored — those change the
    /// calendar without asking. A REQUEST that fails it is still delivered (a forwarded invite
    /// is From the forwarder, not the organizer), but only ever as a pending invitation the user
    /// accepts or declines; see <c>IncomingInvitationService</c>.</para>
    ///
    /// <para>Only From counts. It is the one header the receiving mail server has already had an
    /// opinion about — DMARC aligns on From, so forging it has to survive the provider first.
    /// Reply-To, Sender and Resent-From are checked by nothing: an attacker sends from their own
    /// domain, passes SPF and DMARC honestly, and names whoever they like in those headers. When
    /// this accepted them, the check was satisfiable by anyone who could send mail at all.</para>
    ///
    /// <para>This costs less than it looks. In a genuine delegated send, RFC 5322 puts the author
    /// — the organizer — in From, and the delegate in Sender, so "on behalf of" still matches.
    /// What no longer matches is a system that names the organizer <em>only</em> in Sender or
    /// Reply-To; those invitations are ignored rather than trusted, and the reason is logged.</para>
    /// </summary>
    public static bool IsFromClaimedSender(MimeMessage message, string? claimedEmail)
    {
        if (string.IsNullOrWhiteSpace(claimedEmail))
        {
            return false; // nothing to attribute the message to
        }

        return message.From.Mailboxes
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
