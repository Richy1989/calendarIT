using CalendarIT.Domain;
using CalendarIT.Infrastructure.Net;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// The operator's own mail account (<c>SMTP_HOST</c>, <c>SMTP_PORT</c>, <c>SMTP_USE_SSL</c>,
/// <c>SMTP_USERNAME</c>, <c>SMTP_PASSWORD</c>, <c>SMTP_FROM</c>), optional.
///
/// <para>Everything else goes out through each user's own account. Account mail can't: an
/// email confirmation proves the user can read the address only if the code never passes
/// through a server the user chose — anyone can connect an SMTP server they run and read it
/// there. So confirmation links are only ever sent through this account, and password resets
/// prefer it. Without it, accounts are confirmed by the operator (<c>confirm-email</c>).</para>
/// </summary>
public sealed class SystemMailOptions
{
    public string? Host { get; init; }

    public int Port { get; init; } = 587;

    /// <summary>Implicit TLS (port 465) when true, STARTTLS otherwise — TLS is required either way.</summary>
    public bool UseSsl { get; init; }

    /// <summary>Empty for a relay that takes mail without a login.</summary>
    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>The sender, e.g. <c>24seven &lt;noreply@example.com&gt;</c>.</summary>
    public string? From { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);

    /// <summary>The operator named this host; unlike a user's, it isn't policed.</summary>
    public static readonly MailConnectionOptions Connection = new() { HostScope = OutboundHostScope.Any };

    public static SystemMailOptions FromConfiguration(IConfiguration configuration)
    {
        var port = configuration.GetValue("SMTP_PORT", 587);
        var from = configuration["SMTP_FROM"]?.Trim();
        if (!string.IsNullOrEmpty(from) && !MailboxAddress.TryParse(from, out _))
        {
            throw new InvalidOperationException($"SMTP_FROM is not a valid address: '{from}'.");
        }
        return new SystemMailOptions
        {
            Host = configuration["SMTP_HOST"]?.Trim(),
            Port = port,
            UseSsl = configuration.GetValue("SMTP_USE_SSL", port == 465),
            Username = configuration["SMTP_USERNAME"],
            Password = configuration["SMTP_PASSWORD"],
            From = from,
        };
    }

    /// <summary>The settings in the shape the SMTP code takes (never stored).</summary>
    public MailAccount ToAccount() => new()
    {
        Address = SenderAddress,
        SmtpHost = Host ?? string.Empty,
        SmtpPort = Port,
        SmtpUseSsl = UseSsl,
        Username = Username ?? string.Empty,
    };

    public MailboxAddress Sender => MailboxAddress.Parse(From!);

    private string SenderAddress => IsConfigured ? Sender.Address : string.Empty;
}
