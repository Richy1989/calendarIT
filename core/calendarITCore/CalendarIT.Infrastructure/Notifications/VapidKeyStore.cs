using System.Text.Json;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using WebPush;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Holds the VAPID key pair used to sign Web Push messages. Keys come from
/// <c>VAPID_PUBLIC_KEY</c> / <c>VAPID_PRIVATE_KEY</c> when both are set (production); otherwise a
/// pair is generated once and persisted to <c>{AppDataPath}/vapid.json</c> — so dev works with
/// zero config and the same keys survive restarts, keeping existing browser subscriptions valid
/// (they're bound to the public key). The subject is <c>VAPID_SUBJECT</c> or a mailto: default.
/// </summary>
public sealed class VapidKeyStore
{
    public string Subject { get; }

    public string PublicKey { get; }

    public string PrivateKey { get; }

    public bool IsConfigured => !string.IsNullOrEmpty(PublicKey) && !string.IsNullOrEmpty(PrivateKey);

    public VapidKeyStore(IConfiguration configuration, DatabaseOptions dbOptions)
    {
        Subject = configuration["VAPID_SUBJECT"] is { Length: > 0 } s ? s : "mailto:admin@calendarit.local";

        var pub = configuration["VAPID_PUBLIC_KEY"];
        var priv = configuration["VAPID_PRIVATE_KEY"];
        if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(priv))
        {
            PublicKey = pub;
            PrivateKey = priv;
            return;
        }

        (PublicKey, PrivateKey) = LoadOrCreate(dbOptions.AppDataPath);
    }

    private static (string PublicKey, string PrivateKey) LoadOrCreate(string appDataPath)
    {
        Directory.CreateDirectory(appDataPath);
        var path = Path.Combine(appDataPath, "vapid.json");

        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize<StoredKeys>(File.ReadAllText(path));
            if (stored is { PublicKey.Length: > 0, PrivateKey.Length: > 0 })
            {
                return (stored.PublicKey, stored.PrivateKey);
            }
        }

        var keys = VapidHelper.GenerateVapidKeys();
        File.WriteAllText(path, JsonSerializer.Serialize(new StoredKeys(keys.PublicKey, keys.PrivateKey)));
        return (keys.PublicKey, keys.PrivateKey);
    }

    private sealed record StoredKeys(string PublicKey, string PrivateKey);
}
