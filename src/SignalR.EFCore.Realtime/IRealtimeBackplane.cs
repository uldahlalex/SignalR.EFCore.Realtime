namespace SignalR.EFCore.Realtime;

/// <summary>
/// Abstraction for distributing entity change notifications across multiple server instances.
/// Enables horizontal scaling by ensuring all servers receive entity change notifications,
/// not just the server that processed the SaveChanges.
/// </summary>
public interface IRealtimeBackplane
{
    /// <summary>
    /// Publishes an entity change notification to all server instances.
    /// Called after SaveChanges completes on one server.
    /// </summary>
    /// <param name="notification">The entity change notification to publish</param>
    Task PublishAsync(EntityChangeNotification notification);

    /// <summary>
    /// Subscribes to entity change notifications from other server instances.
    /// The handler will be called when another server publishes a change.
    /// </summary>
    /// <param name="handler">Handler to invoke when remote changes are received</param>
    Task SubscribeAsync(Func<EntityChangeNotification, Task> handler);

    /// <summary>
    /// Unsubscribes from entity change notifications.
    /// Called during application shutdown.
    /// </summary>
    Task UnsubscribeAsync();
}

/// <summary>
/// Represents an entity change notification that can be serialized and sent across servers.
/// </summary>
public record EntityChangeNotification
{
    /// <summary>
    /// The fully qualified type name of the changed entity (e.g., "dataaccess.Game")
    /// </summary>
    public required string EntityTypeName { get; init; }

    /// <summary>
    /// The type of change that occurred
    /// </summary>
    public required EntityChangeType ChangeType { get; init; }

    /// <summary>
    /// Serialized entity data (typically the entity's ID and key properties)
    /// </summary>
    public required string EntityData { get; init; }

    /// <summary>
    /// Timestamp when the change occurred
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Optional server identifier (for debugging/monitoring)
    /// </summary>
    public string? ServerId { get; init; }
}

/// <summary>
/// Type of entity change
/// </summary>
public enum EntityChangeType
{
    Added = 0,
    Modified = 1,
    Deleted = 2
}
