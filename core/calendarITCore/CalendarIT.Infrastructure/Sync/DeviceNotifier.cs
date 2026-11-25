using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CalendarIT.Application.Notifications;
using CalendarIT.Domain;
using CalendarIT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CalendarIT.Infrastructure.Sync;

/// <summary>
/// Tells a user's app devices (UnifiedPush) that something of theirs changed, so they sync now
/// instead of at their next poll. The push carries no content — just "sync" — so nothing of the
/// calendar passes through the push service. Changes are bundled per user for a moment: an import
/// of a thousand events is one nudge, not a thousand.
/// </summary>
public sealed class DeviceNotifier
{
    private readonly Channel<Guid> _changed = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Notes that these users' data changed. Never blocks, never throws.</summary>
    public void Changed(IEnumerable<Guid> userIds)
    {
        foreach (var id in userIds)
        {
            _changed.Writer.TryWrite(id);
        }
    }

    /// <summary>The users whose data changed, in order; read by <see cref="DeviceNotifierService"/>.</summary>
    public ChannelReader<Guid> Reader => _changed.Reader;
}

/// <summary>Sends the bundled nudges (see <see cref="DeviceNotifier"/>).</summary>
public sealed class DeviceNotifierService(
    DeviceNotifier notifier, IServiceScopeFactory scopes, ILogger<DeviceNotifierService> logger) : BackgroundService
{
    /// <summary>How long changes gather before the nudge goes out.</summary>
    public static readonly TimeSpan Bundle = TimeSpan.FromSeconds(3);

    public const string SyncPayload = """{"type":"sync"}""";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = notifier.Reader;
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            await Task.Delay(Bundle, stoppingToken);
            var users = new HashSet<Guid>();
            while (reader.TryRead(out var id))
            {
                users.Add(id);
            }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await SendAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IWebPushSender>(), users, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't nudge devices about a change");
            }
        }
    }

    /// <summary>One nudge per device of [users] that takes them; a device whose endpoint is gone stops getting them.</summary>
    public static async Task SendAsync(AppDbContext db, IWebPushSender sender, IReadOnlyCollection<Guid> users, CancellationToken ct)
    {
        if (users.Count == 0 || !sender.IsConfigured)
        {
            return;
        }
        var devices = await db.Devices
            .Where(d => users.Contains(d.UserId) && d.PushKind == DeviceService.UnifiedPush && d.PushEndpoint != null)
            .ToListAsync(ct);
        foreach (var device in devices)
        {
            var result = await sender.SendAsync(new PushTarget(device.PushEndpoint!, device.PushP256dh ?? "", device.PushAuth ?? ""), SyncPayload, ct);
            if (result == PushSendResult.Expired)
            {
                device.PushKind = null;
                device.PushEndpoint = null;
                device.PushP256dh = null;
                device.PushAuth = null;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Sees every save of calendars, events, tasks and categories — whichever path made it (the API,
/// CalDAV, imports, invitations, subscription refreshes) — and tells <see cref="DeviceNotifier"/>
/// whose data changed.
/// </summary>
public sealed class SyncChangeInterceptor(DeviceNotifier notifier) : SaveChangesInterceptor
{
    private sealed class Pending
    {
        public HashSet<Guid> Users { get; } = [];
        public HashSet<Guid> Calendars { get; } = [];
    }

    private readonly ConditionalWeakTable<DbContext, Pending> _pending = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }
        var pending = _pending.GetOrCreateValue(context);
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }
            switch (entry.Entity)
            {
                case Calendar c:
                    pending.Users.Add(c.OwnerUserId);
                    break;
                case Category c:
                    pending.Users.Add(c.OwnerUserId);
                    break;
                case CalendarEvent e:
                    pending.Calendars.Add(e.CalendarId);
                    pending.Calendars.Add(entry.Property(nameof(CalendarEvent.CalendarId)).OriginalValue is Guid was ? was : e.CalendarId);
                    break;
                case TaskItem t:
                    pending.Calendars.Add(t.CalendarId);
                    break;
            }
        }
    }

    private void Flush(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending))
        {
            return;
        }
        _pending.Remove(context);
        if (pending.Calendars.Count > 0)
        {
            var ids = pending.Calendars.ToList();
            pending.Users.UnionWith(context.Set<Calendar>().AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => c.OwnerUserId).ToList());
        }
        if (pending.Users.Count > 0)
        {
            notifier.Changed(pending.Users);
        }
    }
}
