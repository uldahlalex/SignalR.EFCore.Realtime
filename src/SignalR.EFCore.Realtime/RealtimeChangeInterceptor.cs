using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SignalR.EFCore.Realtime;

/// <summary>
/// EF Core SaveChanges interceptor that automatically pushes entity changes to reactive query subscribers.
/// Supports horizontal scaling via IRealtimeBackplane to distribute notifications across servers.
///
/// HOW IT WORKS (Two-Phase Approach):
/// ================================
/// Phase 1 - BEFORE Save (SavingChanges/Async):
///   - Captures entities and their states (Added/Modified/Deleted) from the ChangeTracker
///   - Stores them in AsyncLocal storage to preserve across async boundaries
///   - At this point, entities still have their "Modified" state
///
/// Phase 2 - AFTER Save (SavedChanges/Async):
///   - Retrieves the captured entities from AsyncLocal storage
///   - Processes local subscriptions (re-executes queries, pushes results)
///   - Publishes changes to backplane for other servers
///   - At this point, ChangeTracker has reset all states to "Unchanged"
///
/// BACKPLANE INTEGRATION:
/// =====================
/// - Local changes are published to backplane (Redis, etc.)
/// - Remote changes from other servers are received via backplane subscription
/// - Both local and remote changes trigger the same subscription processing logic
///
/// WHY TWO PHASES?
/// ==============
/// - Can't query entities BEFORE save → database doesn't have the changes yet
/// - Can't detect changes AFTER save → EF Core resets all states to "Unchanged"
/// - Solution: Capture state BEFORE, process AFTER
///
/// AsyncLocal EXPLAINED:
/// ====================
/// - AsyncLocal preserves values across async/await boundaries within the same logical flow
/// - Each SaveChanges call gets its own isolated storage
/// - Thread-safe for concurrent saves on different connections
/// </summary>
public class RealtimeChangeInterceptor(
    IServiceProvider serviceProvider,
    ILogger<RealtimeChangeInterceptor> logger,
    IRealtimeBackplane backplane) : SaveChangesInterceptor
{
    /// <summary>
    /// Thread-local storage for captured entity changes during the save operation.
    /// Each async context gets its own isolated list.
    /// </summary>
    private readonly AsyncLocal<List<(object Entity, EntityState State)>> _capturedChanges = new();

    /// <summary>
    /// When true, skips all broadcast processing (useful during seeding operations).
    /// </summary>
    public bool SkipProcessing { get; set; }

    // ===== Reactive Query Registry (merged from ReactiveQueryRegistry.cs) =====
    private readonly ConcurrentDictionary<string, List<ReactiveQuerySubscription>> _subscriptionsByConnection = new();
    private readonly ConcurrentDictionary<Type, List<ReactiveQuerySubscription>> _subscriptionsByEntityType = new();
    private readonly object _lock = new();
    private bool _backplaneInitialized = false;

    /// <summary>
    /// PHASE 1 (Sync): Captures entity changes BEFORE SaveChanges executes.
    /// Called by EF Core right before writing to the database.
    /// </summary>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CaptureChanges(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <summary>
    /// PHASE 1 (Async): Captures entity changes BEFORE SaveChangesAsync executes.
    /// Called by EF Core right before writing to the database.
    /// </summary>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CaptureChanges(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// PHASE 2 (Sync): Processes captured changes AFTER SaveChanges completes successfully.
    /// Called by EF Core after database write succeeds.
    /// Note: Uses .GetAwaiter().GetResult() to bridge sync/async boundary.
    /// </summary>
    public override int SavedChanges(
        SaveChangesCompletedEventData eventData,
        int result)
    {
        if (eventData.Context != null)
        {
            ProcessChanges().GetAwaiter().GetResult();
        }

        return base.SavedChanges(eventData, result);
    }

    /// <summary>
    /// PHASE 2 (Async): Processes captured changes AFTER SaveChangesAsync completes successfully.
    /// Called by EF Core after database write succeeds.
    /// </summary>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context != null)
        {
            await ProcessChanges();
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// Captures current entity states from the ChangeTracker before save occurs.
    /// Stores them in AsyncLocal so they're available in the "after save" phase.
    /// </summary>
    private void CaptureChanges(DbContext? context)
    {
        if (context == null) return;

        // At this moment, entities still have their Modified/Added/Deleted states
        _capturedChanges.Value = context.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added ||
                        e.State == EntityState.Modified ||
                        e.State == EntityState.Deleted)
            .Select(e => (e.Entity, e.State))
            .ToList();

        logger.LogInformation("Captured {Count} changes before save", _capturedChanges.Value.Count);
    }

    /// <summary>
    /// Processes captured entity changes after save completes.
    /// Re-executes reactive queries and pushes results directly to subscribers.
    /// Also publishes changes to backplane for distribution to other servers.
    /// </summary>
    private async Task ProcessChanges()
    {
        if (SkipProcessing) return;

        var entries = _capturedChanges.Value;
        if (entries == null || entries.Count == 0)
        {
            logger.LogInformation("No changes to process");
            return;
        }

        logger.LogInformation("Processing {Count} local changes for reactive queries", entries.Count);

        // Process local reactive query subscriptions
        await ProcessReactiveQueries(entries);

        // Publish changes to backplane so other servers can process their subscriptions
        await PublishToBackplane(entries);
    }

    /// <summary>
    /// Publishes entity changes to the backplane for distribution to other server instances.
    /// </summary>
    private async Task PublishToBackplane(List<(object Entity, EntityState State)> entries)
    {
        foreach (var (entity, state) in entries)
        {
            try
            {
                var entityType = entity.GetType();
                var changeType = state switch
                {
                    EntityState.Added => EntityChangeType.Added,
                    EntityState.Modified => EntityChangeType.Modified,
                    EntityState.Deleted => EntityChangeType.Deleted,
                    _ => EntityChangeType.Modified
                };

                // Serialize entity for transmission (just ID and type info)
                var entityData = JsonSerializer.Serialize(entity);

                var notification = new EntityChangeNotification
                {
                    EntityTypeName = entityType.FullName ?? entityType.Name,
                    ChangeType = changeType,
                    EntityData = entityData
                };

                await backplane.PublishAsync(notification);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to publish entity change to backplane for {EntityType}",
                    entity.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Re-execute reactive queries and push results to subscribed clients.
    /// This eliminates the client round-trip - results are pushed automatically.
    /// </summary>
    private async Task ProcessReactiveQueries(
        List<(object Entity, EntityState State)> entries)
    {
        // Group changes by entity type to minimize subscription lookups
        var changesByType = entries.GroupBy(e => e.Entity.GetType());

        foreach (var typeGroup in changesByType)
        {
            var entityType = typeGroup.Key;
            var subscriptions = GetSubscriptionsForEntity(
                entityType,
                typeGroup.First().Entity
            );

            if (subscriptions.Count == 0) continue;

            logger.LogInformation(
                "Found {Count} reactive query subscriptions for {EntityType}",
                subscriptions.Count, entityType.Name);

            // Execute each subscribed query and push results
            foreach (var subscription in subscriptions)
            {
                try
                {
                    // Create a new scope to get fresh services with their own DbContext
                    using var scope = serviceProvider.CreateScope();

                    // Re-execute the stored query with fresh service provider
                    var freshData = await subscription.QueryExecutor(scope.ServiceProvider, subscription.UserId);

                    // Push results to the client using the broadcast callback
                    await subscription.BroadcastCallback(freshData);

                    logger.LogInformation(
                        "📡 Pushed reactive query result to connection {ConnectionId}",
                        subscription.ConnectionId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to execute reactive query for connection {ConnectionId}",
                        subscription.ConnectionId);
                }
            }
        }
    }

    // ===== Reactive Query Registry Methods =====

    /// <summary>
    /// Register a new reactive query subscription.
    /// The query will automatically re-execute and push results when relevant data changes.
    /// On first subscription, initializes backplane subscription to receive remote changes.
    /// </summary>
    public void Subscribe(ReactiveQuerySubscription subscription)
    {
        lock (_lock)
        {
            // Initialize backplane subscription on first subscriber
            if (!_backplaneInitialized)
            {
                InitializeBackplaneAsync().GetAwaiter().GetResult();
                _backplaneInitialized = true;
            }

            // Index by connection ID for cleanup
            if (!_subscriptionsByConnection.TryGetValue(subscription.ConnectionId, out var connectionSubs))
            {
                connectionSubs = new List<ReactiveQuerySubscription>();
                _subscriptionsByConnection[subscription.ConnectionId] = connectionSubs;
            }
            connectionSubs.Add(subscription);

            // Index by entity type for fast lookup during data changes
            if (!_subscriptionsByEntityType.TryGetValue(subscription.EntityType, out var entitySubs))
            {
                entitySubs = new List<ReactiveQuerySubscription>();
                _subscriptionsByEntityType[subscription.EntityType] = entitySubs;
            }
            entitySubs.Add(subscription);

            logger.LogInformation(
                "Registered reactive query: Connection={ConnectionId}, EntityType={EntityType}, User={UserId}",
                subscription.ConnectionId, subscription.EntityType.Name, subscription.UserId);
        }
    }

    /// <summary>
    /// Initializes the backplane subscription to receive entity changes from other servers.
    /// </summary>
    private async Task InitializeBackplaneAsync()
    {
        await backplane.SubscribeAsync(async notification =>
        {
            try
            {
                logger.LogDebug(
                    "Received remote entity change: {EntityType} ({ChangeType}) from server {ServerId}",
                    notification.EntityTypeName,
                    notification.ChangeType,
                    notification.ServerId ?? "unknown");

                // Deserialize the entity
                var entityType = Type.GetType(notification.EntityTypeName);
                if (entityType == null)
                {
                    logger.LogWarning(
                        "Could not resolve entity type: {EntityTypeName}",
                        notification.EntityTypeName);
                    return;
                }

                var entity = JsonSerializer.Deserialize(notification.EntityData, entityType);
                if (entity == null)
                {
                    logger.LogWarning(
                        "Failed to deserialize entity data for {EntityTypeName}",
                        notification.EntityTypeName);
                    return;
                }

                // Process remote change as if it was local
                var state = notification.ChangeType switch
                {
                    EntityChangeType.Added => EntityState.Added,
                    EntityChangeType.Modified => EntityState.Modified,
                    EntityChangeType.Deleted => EntityState.Deleted,
                    _ => EntityState.Modified
                };

                await ProcessReactiveQueries(new List<(object Entity, EntityState State)> { (entity, state) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to process remote entity change for {EntityType}",
                    notification.EntityTypeName);
            }
        });

        logger.LogInformation("Backplane subscription initialized");
    }

    /// <summary>
    /// Get all subscriptions that care about changes to a specific entity type.
    /// Optionally filtered by the specific entity instance (e.g., only Game with ID "123").
    /// </summary>
    private List<ReactiveQuerySubscription> GetSubscriptionsForEntity(Type entityType, object? changedEntity = null)
    {
        if (!_subscriptionsByEntityType.TryGetValue(entityType, out var subs))
        {
            return new List<ReactiveQuerySubscription>();
        }

        // If no specific entity or no filters, return all subscriptions for this type
        if (changedEntity == null)
        {
            return subs.ToList();
        }

        // Apply filters to only return subscriptions that care about this specific entity
        return subs.Where(sub => sub.ChangeFilter?.Invoke(changedEntity) ?? true).ToList();
    }

    /// <summary>
    /// Remove all subscriptions for a connection (called on disconnect).
    /// </summary>
    public void UnsubscribeConnection(string connectionId)
    {
        lock (_lock)
        {
            if (_subscriptionsByConnection.TryRemove(connectionId, out var subs))
            {
                // Remove from entity type index as well
                foreach (var sub in subs)
                {
                    if (_subscriptionsByEntityType.TryGetValue(sub.EntityType, out var entitySubs))
                    {
                        entitySubs.Remove(sub);
                        if (entitySubs.Count == 0)
                        {
                            _subscriptionsByEntityType.TryRemove(sub.EntityType, out _);
                        }
                    }
                }

                logger.LogInformation(
                    "Unsubscribed {Count} reactive queries for connection {ConnectionId}",
                    subs.Count, connectionId);
            }
        }
    }

    /// <summary>
    /// Get count of active subscriptions (for monitoring/debugging).
    /// </summary>
    public (int TotalSubscriptions, int UniqueConnections) GetStats()
    {
        return (
            _subscriptionsByConnection.Values.Sum(list => list.Count),
            _subscriptionsByConnection.Count
        );
    }
}
