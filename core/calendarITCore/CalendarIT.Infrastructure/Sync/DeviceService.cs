using CalendarIT.Application;
using CalendarIT.Application.Notifications;
using CalendarIT.Application.Sync;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Net;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalendarIT.Infrastructure.Sync;

/// <summary>EF Core-backed <see cref="IDeviceService"/>.</summary>
public sealed class DeviceService(AppDbContext db, TimeProvider timeProvider, IWebPushSender sender) : IDeviceService
{
    /// <summary>Devices per account: each one is a push per change, so the list can't be open-ended.</summary>
    public const int MaxDevices = 20;

    public const string UnifiedPush = "unifiedpush";
    public const string Fcm = "fcm";

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(Guid userId, CancellationToken cancellationToken = default) =>
        (await db.Devices.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .ToListAsync(cancellationToken))
        .Select(ToDto)
        .ToList();

    public async Task<DeviceDto> RegisterAsync(
        Guid userId, Guid deviceId, RegisterDeviceRequest request, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new InvalidInputException("A device needs an id.");
        }
        var push = ValidatePush(request.Push);

        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is not null && device.UserId != userId)
        {
            // An id is a key across accounts; whose it is stays unsaid.
            throw new InvalidInputException("That device id is already in use.");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (device is null)
        {
            if (await db.Devices.CountAsync(d => d.UserId == userId, cancellationToken) >= MaxDevices)
            {
                throw new InvalidInputException($"An account can have up to {MaxDevices} devices. Remove one you no longer use first.");
            }
            device = new Device { Id = deviceId, UserId = userId, CreatedAtUtc = now };
            db.Devices.Add(device);
        }

        device.Name = request.Name.Trim();
        device.Platform = request.Platform.Trim().ToLowerInvariant();
        device.AppVersion = request.AppVersion?.Trim();
        device.PushKind = push?.Kind;
        device.PushEndpoint = push?.Endpoint;
        device.PushP256dh = push?.P256dh;
        device.PushAuth = push?.Auth;
        device.LastSeenAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(device);
    }

    public async Task<bool> RemoveAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken = default) =>
        await db.Devices.Where(d => d.Id == deviceId && d.UserId == userId).ExecuteDeleteAsync(cancellationToken) > 0;

    public const string TestPayload = """{"type":"test"}""";

    public async Task<TestPushDto?> TestPushAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, cancellationToken);
        if (device is null)
        {
            return null;
        }
        if (device.PushKind != UnifiedPush || device.PushEndpoint is null)
        {
            return new("noEndpoint", "This device gave the server no push address yet. Set up instant updates on it first.");
        }
        if (!sender.IsConfigured)
        {
            return new("notConfigured", "The server can't send pushes: it has no push keys (VAPID).");
        }
        var result = await sender.SendAsync(
            new PushTarget(device.PushEndpoint, device.PushP256dh ?? "", device.PushAuth ?? ""), TestPayload, cancellationToken);
        switch (result)
        {
            case PushSendResult.Sent:
                return new("sent", "Sent. It should arrive within a few seconds.");
            case PushSendResult.Expired:
                device.PushKind = null;
                device.PushEndpoint = null;
                device.PushP256dh = null;
                device.PushAuth = null;
                await db.SaveChangesAsync(cancellationToken);
                return new("gone", "The push service no longer knows this device's address. Set up instant updates again.");
            default:
                return new("failed", "The push service refused it. Open your UnifiedPush app (e.g. ntfy) once so it connects, then try again. "
                    + "A push server on your home network can't be used: the server only pushes to public addresses.");
        }
    }

    /// <summary>
    /// A UnifiedPush endpoint is a Web Push URL the server will POST to, so it is held to what a
    /// browser's is: https, on the public internet (checked again on the address dialled when
    /// sending), with the keys to encrypt for it.
    /// </summary>
    private static (string Kind, string Endpoint, string? P256dh, string? Auth)? ValidatePush(DevicePushInput? input)
    {
        if (input is null)
        {
            return null;
        }
        var kind = input.Kind.Trim().ToLowerInvariant();
        var endpoint = input.Endpoint.Trim();
        switch (kind)
        {
            case UnifiedPush:
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                    || OutboundHostPolicy.IsBlockedLiteral(uri.Host, OutboundHostScope.Public))
                {
                    throw new InvalidInputException("That isn't a valid push endpoint.");
                }
                if (string.IsNullOrWhiteSpace(input.P256dh) || string.IsNullOrWhiteSpace(input.Auth))
                {
                    throw new InvalidInputException("A UnifiedPush endpoint needs its keys.");
                }
                return (kind, endpoint, input.P256dh.Trim(), input.Auth.Trim());
            case Fcm:
                if (endpoint.Length == 0)
                {
                    throw new InvalidInputException("An FCM registration needs its token.");
                }
                return (kind, endpoint, null, null);
            default:
                throw new InvalidInputException("Push is unifiedpush or fcm.");
        }
    }

    private static DeviceDto ToDto(Device d) => new(
        d.Id, d.Name, d.Platform, d.AppVersion, d.PushKind,
        new DateTimeOffset(DateTime.SpecifyKind(d.CreatedAtUtc, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(d.LastSeenAtUtc, DateTimeKind.Utc)));
}
