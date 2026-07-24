using CalendarIT.Infrastructure.Mail;
using MimeKit;

namespace CalendarIT.Tests;

/// <summary>
/// Nothing inside an .ics is authenticated — the ORGANIZER and ATTENDEE lines are text the
/// sender chose. These pin the check that the message headers have to back up that claim, which
/// is what stops a stranger writing to (or deleting from) someone's calendar by email.
/// </summary>
public sealed class ImipSenderVerificationTests
{
    private static MimeMessage MessageFrom(string from, string? sender = null, string? replyTo = null)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        if (sender is not null)
        {
            message.Sender = MailboxAddress.Parse(sender);
        }
        if (replyTo is not null)
        {
            message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        }
        return message;
    }

    [Fact]
    public void AcceptsAMessageFromTheAddressItClaims()
    {
        Assert.True(ImipMime.IsFromClaimedSender(
            MessageFrom("organizer@example.com"), "organizer@example.com"));
    }

    [Fact]
    public void AddressComparisonIgnoresCase()
    {
        Assert.True(ImipMime.IsFromClaimedSender(
            MessageFrom("Organizer@Example.COM"), "organizer@example.com"));
    }

    [Fact]
    public void RejectsAForgedOrganizer()
    {
        // The attack: anyone can write ORGANIZER:mailto:boss@example.com into an .ics.
        Assert.False(ImipMime.IsFromClaimedSender(
            MessageFrom("attacker@evil.example"), "boss@example.com"));
    }

    [Fact]
    public void RejectsWhenThereIsNoClaimedAddressToCheck()
    {
        // An invitation with no ORGANIZER can't be attributed to anyone, so it isn't trusted.
        Assert.False(ImipMime.IsFromClaimedSender(MessageFrom("someone@example.com"), null));
        Assert.False(ImipMime.IsFromClaimedSender(MessageFrom("someone@example.com"), "  "));
    }

    [Fact]
    public void AcceptsSendOnBehalfOf()
    {
        // A delegate or calendar system sending for the organizer: From is the sender, and the
        // organizer shows up as Sender or Reply-To. Rejecting these would break real invitations.
        Assert.True(ImipMime.IsFromClaimedSender(
            MessageFrom("assistant@example.com", sender: "organizer@example.com"), "organizer@example.com"));
        Assert.True(ImipMime.IsFromClaimedSender(
            MessageFrom("calendar-robot@example.com", replyTo: "organizer@example.com"), "organizer@example.com"));
    }

    [Fact]
    public void RejectsALookalikeDomain()
    {
        Assert.False(ImipMime.IsFromClaimedSender(
            MessageFrom("organizer@examp1e.com"), "organizer@example.com"));
    }
}
