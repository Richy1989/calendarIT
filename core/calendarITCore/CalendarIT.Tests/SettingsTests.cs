using CalendarIT.Infrastructure;
using CalendarIT.Infrastructure.Calendars;
using CalendarIT.Infrastructure.Mail;
using Microsoft.Extensions.Configuration;

namespace CalendarIT.Tests;

/// <summary>
/// Operator settings arrive as environment variables, and Compose, .env templates and Unraid pass
/// an option left blank as <c>SETTING=</c>. Blank must mean "use the default" — it used to stop
/// the server at start-up for any number or true/false setting.
/// </summary>
public sealed class SettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    [Fact]
    public void BlankSettings_TakeTheirDefaults()
    {
        var config = Config(("SMTP_USE_SSL", ""), ("SMTP_PORT", " "), ("API_RATE_LIMIT_PER_MINUTE", ""), ("DISABLE_REGISTRATION", ""));

        Assert.False(config.Setting("SMTP_USE_SSL", false));
        Assert.Equal(1200, config.Setting("API_RATE_LIMIT_PER_MINUTE", 1200));
        Assert.True(config.Setting("DISABLE_REGISTRATION", true));
        Assert.Equal("calendarit", Config(("JWT_ISSUER", "")).Text("JWT_ISSUER", "calendarit"));
    }

    [Fact]
    public void TheShippedTemplates_BlankServerMailSettings_Start()
    {
        // Exactly what .env.example and the Unraid templates pass when the server has no mail account.
        var mail = SystemMailOptions.FromConfiguration(Config(
            ("SMTP_HOST", ""), ("SMTP_PORT", "587"), ("SMTP_USE_SSL", ""), ("SMTP_USERNAME", ""), ("SMTP_PASSWORD", ""), ("SMTP_FROM", "")));

        Assert.False(mail.IsConfigured);
        Assert.Equal(10, QuotaOptions.FromConfiguration(Config(("QUOTA_SUBSCRIPTIONS", ""))).Subscriptions);
    }

    [Fact]
    public void Port465_MeansTlsOnConnect_UnlessSaidOtherwise()
    {
        Assert.True(SystemMailOptions.FromConfiguration(Config(("SMTP_PORT", "465"), ("SMTP_USE_SSL", ""))).UseSsl);
        Assert.False(SystemMailOptions.FromConfiguration(Config(("SMTP_PORT", "465"), ("SMTP_USE_SSL", "false"))).UseSsl);
    }

    [Fact]
    public void AnUnreadableValue_NamesTheSetting()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Config(("SMTP_USE_SSL", "maybe")).Setting("SMTP_USE_SSL", false));
        Assert.Contains("SMTP_USE_SSL", error.Message);
    }
}
