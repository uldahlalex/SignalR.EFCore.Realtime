namespace SignalR.EFCore.Realtime;

/// <summary>
/// Represents an active reactive query subscription.
/// Stores the query logic so it can be re-executed when data changes.
/// </summary>
public class ReactiveQuerySubscription
{
    /// <summary>
    /// SignalR connection ID that subscribed to this query.
    /// </summary>
    public required string ConnectionId { get; init; }

    /// <summary>
    /// The user ID who owns this subscription (for auth/filtering).
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// The entity type this query is watching (e.g., typeof(Quiz)).
    /// </summary>
    public required Type EntityType { get; init; }

    /// <summary>
    /// Function that executes the query and returns the result.
    /// Takes service provider and userId as parameters.
    /// The lambda should resolve services from the provider to avoid disposed context issues.
    /// </summary>
    public required Func<IServiceProvider, string, Task<object>> QueryExecutor { get; init; }

    /// <summary>
    /// Callback function that broadcasts the result to the client.
    /// Takes the query result as parameter.
    /// </summary>
    public required Func<object, Task> BroadcastCallback { get; init; }

    /// <summary>
    /// Optional filter to determine if this subscription cares about a specific entity change.
    /// For example, for game queries, only re-execute if the changed entity is the specific game we're watching.
    /// Returns true if subscription should be notified.
    /// </summary>
    public Func<object, bool>? ChangeFilter { get; init; }

    /// <summary>
    /// Timestamp when this subscription was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
