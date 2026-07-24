using MailKit.Security;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>
/// How every SMTP/IMAP connection in the app is secured — one decision, in one place, because
/// it was previously spelled out at five call sites and had to stay in step at all of them.
/// </summary>
public static class MailSecurity
{
    /// <summary>
    /// The socket options for a mail connection: implicit TLS when the account asks for SSL,
    /// otherwise STARTTLS — <em>required</em>, not opportunistic.
    ///
    /// This deliberately isn't <c>StartTlsWhenAvailable</c>: that silently continues in
    /// plaintext when the server doesn't advertise STARTTLS, so the user's mailbox password
    /// would cross the network in the clear, and an attacker able to strip the capability from
    /// the greeting could force exactly that. Failing to connect is the safer answer.
    /// </summary>
    public static SecureSocketOptions For(bool useSsl) =>
        useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
}
