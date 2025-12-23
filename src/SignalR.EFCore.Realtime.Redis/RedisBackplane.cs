using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace SignalR.EFCore.Realtime.Backplanes;

/// <summary>
/// Redis-based backplane for horizontal scaling across multiple server instances.
/// Uses Redis pub/sub to distribute entity change notifications to all servers.
/// Each server receives notifications from other servers and processes them locally.
/// </summary>
public class RedisBackplane : IRealtimeBackplane, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisBackplane> _logger;
    private readonly string _channelName;
    private readonly string _serverId;
    private ISubscriber? _subscriber;
    private Func<EntityChangeNotification, Task>? _handler;

    public RedisBackplane(
        IConnectionMultiplexer redis,
        ILogger<RedisBackplane> logger,
        string? channelName = null)
    {
        _redis = redis;
        _logger = logger;
        _channelName = channelName ?? "realtime:entity-changes";
        _serverId = $"{Environment.MachineName}_{Guid.NewGuid():N}";

        _logger.LogInformation(
            "Using RedisBackplane (horizontal scaling mode) - Server: {ServerId}, Channel: {Channel}",
            _serverId,
            _channelName);
    }

    public async Task PublishAsync(EntityChangeNotification notification)
    {
        try
        {
            // Add server ID for debugging
            var notificationWithServer = notification with { ServerId = _serverId };

            var json = JsonSerializer.Serialize(notificationWithServer);
            var subscriber = _redis.GetSubscriber();

            await subscriber.PublishAsync(
                RedisChannel.Literal(_channelName),
                json,
                CommandFlags.FireAndForget);

            _logger.LogDebug(
                "Redis: Published {EntityType} ({ChangeType}) from {ServerId}",
                notification.EntityTypeName,
                notification.ChangeType,
                _serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Redis: Failed to publish entity change notification for {EntityType}",
                notification.EntityTypeName);
            // Don't throw - we don't want Redis failures to break the save operation
        }
    }

    public async Task SubscribeAsync(Func<EntityChangeNotification, Task> handler)
    {
        _handler = handler;
        _subscriber = _redis.GetSubscriber();

        await _subscriber.SubscribeAsync(
            RedisChannel.Literal(_channelName),
            async (channel, message) =>
            {
                try
                {
                    var notification = JsonSerializer.Deserialize<EntityChangeNotification>(message.ToString());

                    if (notification == null)
                    {
                        _logger.LogWarning("Redis: Received null notification");
                        return;
                    }

                    // Skip notifications from our own server (already processed locally)
                    if (notification.ServerId == _serverId)
                    {
                        _logger.LogDebug(
                            "Redis: Skipping own notification for {EntityType}",
                            notification.EntityTypeName);
                        return;
                    }

                    _logger.LogDebug(
                        "Redis: Received {EntityType} ({ChangeType}) from {RemoteServerId}",
                        notification.EntityTypeName,
                        notification.ChangeType,
                        notification.ServerId ?? "unknown");

                    await _handler(notification);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Redis: Failed to process entity change notification from channel {Channel}",
                        channel);
                }
            });

        _logger.LogInformation(
            "Redis: Subscribed to channel {Channel} as {ServerId}",
            _channelName,
            _serverId);
    }

    public async Task UnsubscribeAsync()
    {
        if (_subscriber != null)
        {
            await _subscriber.UnsubscribeAsync(RedisChannel.Literal(_channelName));
            _logger.LogInformation("Redis: Unsubscribed from channel {Channel}", _channelName);
        }

        _handler = null;
    }

    public async ValueTask DisposeAsync()
    {
        await UnsubscribeAsync();
        GC.SuppressFinalize(this);
    }
}
