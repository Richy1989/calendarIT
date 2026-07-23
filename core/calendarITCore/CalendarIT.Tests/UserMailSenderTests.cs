using CalendarIT.Domain;
using CalendarIT.Infrastructure.Mail;

namespace CalendarIT.Tests;

/// <summary>The "From" identity for a user's system mail: the override when set, else the address.</summary>
public sealed class UserMailSenderTests
{
    [Fact]
    public void FromOf_UsesTheAddress_WhenNoOverride()
    {
        var account = new MailAccount { Address = "richy@example.com" };
        Assert.Equal("richy@example.com", UserMailSender.FromOf(account).Address);
    }

    [Fact]
    public void FromOf_UsesTheOverride_WhenSet()
    {
        var account = new MailAccount { Address = "richy@example.com", FromAddress = "  noreply@example.com " };
        Assert.Equal("noreply@example.com", UserMailSender.FromOf(account).Address);
    }

    [Fact]
    public void FromOf_FallsBackToAddress_WhenOverrideIsBlank()
    {
        var account = new MailAccount { Address = "richy@example.com", FromAddress = "   " };
        Assert.Equal("richy@example.com", UserMailSender.FromOf(account).Address);
    }
}
