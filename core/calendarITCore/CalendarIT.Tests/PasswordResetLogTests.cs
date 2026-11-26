using CalendarIT.Infrastructure.Mail;

namespace CalendarIT.Tests;

/// <summary>A usable reset link must never reach the application log.</summary>
public sealed class PasswordResetLogTests
{
    private const string Token = "SYNTHETIC-RESET-TOKEN-7f3a9c";
    private const string Link = "https://cal.example.com/reset-password?email=alice%40example.com&token=" + Token;

    [Fact]
    public async Task NoMailAccount_LogsHowToIssueALink_ButNotTheLinkOrAddress()
    {
        var logger = new CapturingLogger<PasswordResetMailer>();
        var mailer = new PasswordResetMailer(new FakeMailOutbox { HasAccount = false }, logger);

        await mailer.SendAsync(Guid.NewGuid(), "alice@example.com", Link);

        var line = Assert.Single(logger.Lines);
        Assert.DoesNotContain(Token, logger.All);
        Assert.DoesNotContain("reset-password?", logger.All);
        Assert.DoesNotContain("alice@example.com", logger.All);
        Assert.Contains("reset-password-link", line);
    }

    [Fact]
    public async Task WithAMailAccount_LogsNothingAndQueuesTheLink()
    {
        var logger = new CapturingLogger<PasswordResetMailer>();
        var outbox = new FakeMailOutbox();

        await new PasswordResetMailer(outbox, logger).SendAsync(Guid.NewGuid(), "alice@example.com", Link);

        Assert.Empty(logger.Lines);
        Assert.Contains(Token, Assert.Single(outbox.Sent).Message.TextBody);
    }
}
