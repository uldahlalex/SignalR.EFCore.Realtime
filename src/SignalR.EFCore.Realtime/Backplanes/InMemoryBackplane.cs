using Microsoft.Extensions.Logging;

namespace SignalR.EFCore.Realtime.Backplanes;

/// <summary>
/// In-memory backplane for single-server deployments or development/testing.
/// Does not distribute notifications across servers - only processes local changes.
/// This is the default backplane when no other is configured.
/// </summary>
public class InMemoryBackplane(ILogger<InMemoryBackplane> logger) : IRealtimeBackplane
{
    private Func<EntityChangeNotification, Task>? _handler;

    public Task PublishAsync(EntityChangeNotification notification)
    {
        // In single-server mode, we don't need to publish to other servers
        // The local interceptor will handle the notification directly
        logger.LogDebug(
            "InMemory: Entity change {EntityType} ({ChangeType}) - local processing only",
            notification.EntityTypeName,
            notification.ChangeType);

        return Task.CompletedTask;
    }

    public Task SubscribeAsync(Func<EntityChangeNotification, Task> handler)
    {
        _handler = handler;
        logger.LogInformation("InMemory: Subscribed to local entity changes");
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync()
    {
        _handler = null;
        logger.LogInformation("InMemory: Unsubscribed from entity changes");
        return Task.CompletedTask;
    }
}
