using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>Every five minutes: refreshes the subscribed calendars that are due (each at its own
/// interval), a batch at a time.</summary>
[DisallowConcurrentExecution]
public sealed class SubscriptionRefreshJob(IServiceScopeFactory scopeFactory) : IJob
{
    public static readonly JobKey Key = new("subscription-refresh");

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CalendarSubscriptionService>().RefreshDueAsync(context.CancellationToken);
    }
}
