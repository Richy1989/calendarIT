using System.Net;
using System.Net.Sockets;

namespace CalendarIT.Infrastructure.Net;

/// <summary>Which destinations the server may open connections to on a user's behalf.</summary>
public enum OutboundHostScope
{
    /// <summary>Public internet addresses only — for anything a user names that ought to be a
    /// public service (Web Push endpoints always; mail servers on a shared instance).</summary>
    Public,

    /// <summary>Public addresses plus private networks (RFC 1918, ULA, CGNAT): the default for mail
    /// servers, because a self-hosted instance often talks to a mail server on the same LAN. The
    /// server itself (loopback) and link-local — which holds cloud metadata endpoints — stay off.</summary>
    Private,

    /// <summary>No restriction. Only for an operator who knowingly runs a mail bridge on loopback.</summary>
    Any,
}

/// <summary>Raised when a user-supplied host resolves only to addresses the policy refuses.</summary>
public sealed class OutboundHostBlockedException(string host)
    : Exception($"Connections to '{host}' aren't allowed on this server.");

/// <summary>
/// Vets the destination of a connection the server makes because a user told it to: their mail
/// server, their browser's push endpoint.
///
/// Without this, those settings were a way to make the server reach places its users can't —
/// the database next to it, the loopback admin port of some other service, a cloud metadata
/// endpoint — and, through the error text of a "test connection", to read back what answered.
/// The check runs on the address actually connected to (after DNS), so a hostname that resolves
/// somewhere internal is caught too, not just an IP typed in directly.
/// </summary>
public static class OutboundHostPolicy
{
    /// <summary>Whether a connection to <paramref name="address"/> is allowed under <paramref name="scope"/>.</summary>
    public static bool IsAllowed(IPAddress address, OutboundHostScope scope)
    {
        if (scope == OutboundHostScope.Any)
        {
            return true;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || IsLinkLocal(address) || IsUnspecifiedOrSpecial(address))
        {
            return false;
        }
        return scope == OutboundHostScope.Private || !IsPrivate(address);
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and returns the addresses a connection may use, in
    /// resolver order. Throws <see cref="OutboundHostBlockedException"/> when none are allowed.
    /// </summary>
    public static async Task<IPAddress[]> ResolveAllowedAsync(string host, OutboundHostScope scope, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(a => IsAllowed(a, scope)).ToArray();
        return allowed.Length > 0 ? allowed : throw new OutboundHostBlockedException(host);
    }

    /// <summary>Opens a TCP connection to the first allowed address of <paramref name="host"/>.</summary>
    public static async Task<Socket> ConnectAsync(
        string host, int port, OutboundHostScope scope, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var addresses = await ResolveAllowedAsync(host, scope, timeoutCts.Token);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw last is OperationCanceledException
            ? new TimeoutException($"Connecting to {host}:{port} timed out.")
            : last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Whether a literal address would be refused — for validating input before it is
    /// stored. Hostnames pass here; they are checked when actually connected to.</summary>
    public static bool IsBlockedLiteral(string host, OutboundHostScope scope) =>
        IPAddress.TryParse(host.Trim().Trim('[', ']'), out var ip) && !IsAllowed(ip, scope);

    private static bool IsLinkLocal(IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork
        ? a.GetAddressBytes() is [169, 254, ..]
        : a.IsIPv6LinkLocal || a.IsIPv6SiteLocal;

    private static bool IsUnspecifiedOrSpecial(IPAddress a)
    {
        if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.Broadcast) || a.IsIPv6Multicast)
        {
            return true;
        }
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 0 || b[0] >= 224; // "this network", multicast and reserved
        }
        return false;
    }

    private static bool IsPrivate(IPAddress a)
    {
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127); // CGNAT
        }
        return a.IsIPv6UniqueLocal;
    }
}
