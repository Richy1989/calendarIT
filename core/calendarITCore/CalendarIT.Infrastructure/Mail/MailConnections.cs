using System.Net.Sockets;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Net;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace CalendarIT.Infrastructure.Mail;

/// <summary>Operator settings for connections to users' mail servers (<c>MAIL_HOST_POLICY</c>).</summary>
public sealed class MailConnectionOptions
{
    /// <summary>Which addresses a user's mail server may resolve to. Default: public and LAN.</summary>
    public OutboundHostScope HostScope { get; init; } = OutboundHostScope.Private;
}

/// <summary>
/// Opens every SMTP/IMAP connection the app makes to a user's mail server — one place for the
/// three things each of them needs and used to be missing somewhere:
/// <list type="bullet">
/// <item>a destination check (<see cref="OutboundHostPolicy"/>), since the host is user input;</item>
/// <item>a timeout, so a server that never answers can't hold a request — or the background job
///   serving everyone — for minutes;</item>
/// <item>required TLS (<see cref="MailSecurity"/>).</item>
/// </list>
/// </summary>
public static class MailConnections
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task<SmtpClient> OpenSmtpAsync(
        MailAccount account, string password, MailConnectionOptions options, CancellationToken cancellationToken)
    {
        var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await ConnectAsync(client, account.SmtpHost, account.SmtpPort, account.SmtpUseSsl, options, cancellationToken);
            await client.AuthenticateAsync(account.Username, password, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<ImapClient> OpenImapAsync(
        MailAccount account, string password, MailConnectionOptions options, CancellationToken cancellationToken)
    {
        var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await ConnectAsync(client, account.ImapHost!, account.ImapPort, account.ImapUseSsl, options, cancellationToken);
            await client.AuthenticateAsync(account.Username, password, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task ConnectAsync(
        IMailService client, string host, int port, bool useSsl, MailConnectionOptions options, CancellationToken cancellationToken)
    {
        // The socket is opened here, against an address the policy approved, and handed over —
        // MailKit still does TLS against the host name, so certificate checks are unchanged.
        var socket = await OutboundHostPolicy.ConnectAsync(host, port, options.HostScope, Timeout, cancellationToken);
        try
        {
            await client.ConnectAsync(socket, host, port, MailSecurity.For(useSsl), cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A user-facing reason a connection failed, by kind rather than verbatim. The raw exception
    /// text described whatever answered on that host and port — banner, protocol, TLS details —
    /// which turned "test my mail account" into a probe of the server's network.
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        OutboundHostBlockedException => "That server address isn't allowed on this instance.",
        AuthenticationException => "The server rejected the username or password.",
        SslHandshakeException => "A secure (TLS) connection couldn't be established. Check the port and the SSL setting.",
        SocketException or TimeoutException or OperationCanceledException => "The server couldn't be reached.",
        SmtpCommandException { ErrorCode: SmtpErrorCode.RecipientNotAccepted } => "The server refused the recipient address.",
        SmtpCommandException { ErrorCode: SmtpErrorCode.SenderNotAccepted } => "The server refused the sender address.",
        SmtpCommandException or ImapCommandException => "The server refused the request.",
        _ => "The connection to the mail server failed.",
    };
}
